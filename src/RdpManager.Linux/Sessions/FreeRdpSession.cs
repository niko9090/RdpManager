using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Sessions;

/// <summary>
/// Una sessione FreeRDP in una scheda. È anche il contenuto della scheda (la vista la associa a
/// <c>FreeRdpSessionView</c>). Il processo parte quando la vista dice se c'è una superficie X11 in cui incorporarlo
/// (<see cref="AttachSurface"/>) o no (<see cref="StartDetached"/>, finestra separata).
/// Quando il processo termina la scheda resta, segnata come disconnessa, con «Riconnetti».
/// </summary>
public sealed class FreeRdpSession : ViewModelBase, ISession
{
    private readonly RdpConnection _connection;
    private readonly FreeRdpClient _client;
    private readonly ResolvedCredentials? _gateway;
    private readonly Func<CancellationToken, Task<string?>> _prepareVpn;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private FreeRdpProcess? _process;
    private FreeRdpLaunchContext _context = new();
    private bool _closed;
    private bool _starting;
    private bool _isRunning;
    private bool _isEmbedded;
    private bool _started;
    private bool _detached;
    private string _message = "";
    private int _generation;
    private int _attempts;
    private bool _acceptCertificate;
    private DateTime _startedAt;

    public FreeRdpSession(
        RdpConnection connection,
        FreeRdpClient client,
        ResolvedCredentials? gateway,
        Func<CancellationToken, Task<string?>> prepareVpn,
        Action<Action> post)
    {
        _connection = connection;
        _client = client;
        _gateway = gateway;
        _prepareVpn = prepareVpn;
        _post = post;
        ConnectionId = connection.Id;
        _title = connection.DisplayName;
        ReconnectCommand = new RelayCommand(Reconnect, () => !_isRunning && !_starting && !_closed);
    }

    private string _title;

    public Guid ConnectionId { get; }

    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value))
                TitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object Content => this;

    /// <summary>Chiede all'utente se fidarsi di un certificato non verificato (avviso certificato); null = non si chiede e la sessione finisce.</summary>
    public Func<CertificatePrompt, Task<bool>>? CertificateDecider { get; set; }

    /// <summary>Handle X11 della superficie in cui la sessione è incorporata (per la foto); null se in finestra separata.</summary>
    public long? SurfaceXid { get; private set; }

    /// <summary>La sessione è collegata (processo in esecuzione): conta per il passaggio fra schede.</summary>
    public bool IsConnected => _isRunning;

    /// <summary>La sessione sta in una finestra separata di FreeRDP (non nella scheda).</summary>
    public bool IsDetachedWindow => _detached;

    public event EventHandler? Closed;

    public event EventHandler? TitleChanged;

    /// <summary>Il client sa incorporarsi e la connessione non usa tutti i monitor: la vista può offrire una superficie X11.</summary>
    public bool WantsEmbedding => _client.CanEmbed && !_connection.UseAllMonitors;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value))
                return;
            RaiseView();
            ReconnectCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmbedded
    {
        get => _isEmbedded;
        private set
        {
            if (Set(ref _isEmbedded, value))
            {
                RaiseView();
            }
        }
    }

    private void RaiseView()
    {
        OnPropertyChanged(nameof(ShowSurface));
        OnPropertyChanged(nameof(ShowOverlay));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(CanReconnect));
    }

    /// <summary>Messaggio sotto al titolo: stato, errore o motivo della disconnessione.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>
    /// La superficie nativa per la sessione incorporata c'è (e resta anche a sessione finita: i controlli nativi
    /// coprono quelli di Avalonia, quindi l'avviso di disconnessione sta in una fascia sopra, non sopra la superficie).
    /// </summary>
    public bool ShowSurface => !_detached;

    /// <summary>Sessione in finestra separata: avviso al centro della scheda.</summary>
    public bool ShowOverlay => _detached;

    /// <summary>Sessione incorporata terminata: fascia con il motivo e «Riconnetti».</summary>
    public bool ShowBanner => !_detached && _started && !_isRunning && !_starting;

    public bool CanReconnect => !_isRunning && !_starting && !_closed && _started;

    public RelayCommand ReconnectCommand { get; }

    public string TargetText => _connection.Port == RdpConnection.DefaultPort ? _connection.Host : $"{_connection.Host}:{_connection.Port}";

    public string DisplayTitle => _connection.DisplayName;

    /// <summary>Processo in esecuzione (per le prove e per la chiusura generale).</summary>
    internal bool HasProcess => _process is { HasExited: false };

    /// <summary>La vista ha creato la superficie X11 <paramref name="xid"/> grande <paramref name="width"/>x<paramref name="height"/> pixel.</summary>
    public void AttachSurface(long xid, int width, int height)
    {
        if (_started || _closed)
            return;
        IsEmbedded = true;
        SurfaceXid = xid;
        Launch(new FreeRdpLaunchContext { ParentWindow = xid, AvailableWidth = width, AvailableHeight = height, DrivePath = HomePath() });
    }

    /// <summary>Incorporare non è possibile (Wayland, client SDL, più monitor): finestra separata.</summary>
    public void StartDetached(int width = 1280, int height = 720)
    {
        if (_started || _closed)
            return;
        IsEmbedded = false;
        SurfaceXid = null;
        _detached = true;
        Launch(new FreeRdpLaunchContext { AvailableWidth = width, AvailableHeight = height, DrivePath = HomePath() });
    }

    public void Reconnect()
    {
        if (_isRunning || _starting || _closed)
            return;
        _attempts = 0;
        Launch(_context);
    }

    /// <summary>
    /// La scheda cambia sede (finestra staccata o riagganciata): la sessione si ferma e riparte nella nuova superficie,
    /// quando la nuova vista la chiede. La sessione remota si riapre (come una riconnessione).
    /// </summary>
    public void Rehost()
    {
        if (_closed)
            return;
        FreeRdpProcess? process;
        lock (_gate)
        {
            _generation++;
            process = _process;
            _process = null;
        }
        if (process is not null)
            _ = process.StopAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        _started = false;
        _starting = false;
        _detached = false;
        _isEmbedded = false;
        SurfaceXid = null;
        _attempts = 0;
        IsRunning = false;
        Message = "";
        Title = _connection.DisplayName;
        RaiseView();
        ReconnectCommand.RaiseCanExecuteChanged();
    }

    private static string? HomePath() => Environment.GetEnvironmentVariable("HOME");

    private void Launch(FreeRdpLaunchContext context)
    {
        _context = context with { Gateway = _gateway };
        _started = true;
        _starting = true;
        _startedAt = DateTime.UtcNow;
        Title = _connection.DisplayName;
        Message = IsEmbedded ? "Connessione in corso…" : DisplayServer.SessionNote(DisplayServer.Current, false) ?? "Sessione aperta in una finestra separata.";
        RaiseView();
        ReconnectCommand.RaiseCanExecuteChanged();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        var generation = _generation;
        try
        {
            var vpnError = await _prepareVpn(_cts.Token).ConfigureAwait(false);
            if (vpnError is not null)
            {
                _post(() => Ended(vpnError));
                return;
            }
            if (_closed || generation != _generation)
                return;
            var command = BuildCommand();
            var process = FreeRdpProcess.Start(command);
            process.Exited += (code, err) => _post(() => { if (generation == _generation) OnProcessExited(process, code, err); });
            // Il processo si registra subito: se nel frattempo la sessione è stata chiusa non deve restare vivo.
            bool stale;
            lock (_gate)
            {
                stale = _closed || generation != _generation;
                if (stale)
                {
                    var stop = process.StopAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
                    if (_closed)
                        StopTask = Task.WhenAll(StopTask, stop);
                }
                else
                    _process = process;
            }
            if (stale)
                return;
            _post(() =>
            {
                if (_closed || generation != _generation)
                    return;
                _starting = false;
                IsRunning = !process.HasExited;
                if (process.HasExited)
                    return;
                Message = IsEmbedded ? "" : (DisplayServer.SessionNote(DisplayServer.Current, false) ?? "Sessione aperta in una finestra separata.")
                    + " Chiudendo la scheda si chiude anche la finestra.";
                RaiseView();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _post(() => Ended(ex.Message));
        }
    }

    /// <summary>Il comando di FreeRDP; con il certificato accettato dall'utente per questa sessione (vedi <see cref="CertificateTrust"/>).</summary>
    private FreeRdpCommand BuildCommand()
    {
        // Certificato accettato dall'utente: solo per questa sessione la politica passa a «ignora».
        var context = _acceptCertificate ? _context with { CertificatePolicy = CertificatePolicy.Ignore } : _context;
        return FreeRdpCommandBuilder.Build(_connection, _client, context);
    }

    private void OnProcessExited(FreeRdpProcess process, int exitCode, string error)
    {
        if (_closed || !ReferenceEquals(_process, process) && _process is not null)
            return;
        lock (_gate)
            _process = null;
        if (exitCode != 0 && CertificateTrust.IsCertificateProblem(exitCode, error) && CertificateDecider is not null)
        {
            _ = AskCertificateAsync(error);
            return;
        }
        if (DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(15))
            _attempts = 0; // la sessione era stata stabile: il conteggio riparte
        if (exitCode != 0 && CertificateTrust.IsRetryableExit(exitCode, error) && ReconnectPolicy.CanRetry(_connection, _attempts))
        {
            ScheduleRetry();
            return;
        }
        var detail = LastLine(error);
        var message = exitCode == 0
            ? "La sessione è terminata."
            : "La sessione si è chiusa (codice " + exitCode + ")." + (detail.Length > 0 ? "\n" + detail : "");
        Ended(message);
    }

    private async Task AskCertificateAsync(string error)
    {
        var accept = false;
        try
        {
            IsRunning = false;
            Message = "In attesa di una tua decisione sul certificato del server…";
            accept = await CertificateDecider!(new CertificatePrompt(_connection.Host, CertificateTrust.Describe(_connection.Host, error)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
        }
        if (_closed)
            return;
        if (!accept)
        {
            Ended("Connessione annullata: certificato del server non accettato.");
            return;
        }
        _acceptCertificate = true;
        Launch(_context);
    }

    private void ScheduleRetry()
    {
        var attempt = ++_attempts;
        var max = Math.Clamp(_connection.AutoReconnectMaxAttempts, 0, ReconnectPolicy.MaxAllowedAttempts);
        var delay = ReconnectPolicy.DelayBefore(attempt);
        IsRunning = false;
        Message = $"Connessione persa: nuovo tentativo {attempt} di {max} fra {(int)delay.TotalSeconds} secondi…";
        var generation = _generation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _post(() =>
            {
                if (!_closed && generation == _generation && !_isRunning && !_starting)
                    Launch(_context);
            });
        });
    }

    private void Ended(string message)
    {
        _starting = false;
        IsRunning = false;
        Message = message;
        Title = _connection.DisplayName + " (disconnesso)";
        RaiseView();
        ReconnectCommand.RaiseCanExecuteChanged();
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            var process = _process;
            _process = null;
            if (process is not null)
                StopTask = process.StopAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
        _cts.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Termina quando il processo è stato fermato (dopo <see cref="Close"/>).</summary>
    public Task StopTask { get; private set; } = Task.CompletedTask;
}
