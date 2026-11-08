using RdpManager.Core;
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
    private FreeRdpProcess? _process;
    private FreeRdpLaunchContext _context = new();
    private bool _closed;
    private bool _starting;
    private bool _isRunning;
    private bool _isEmbedded;
    private bool _started;
    private bool _detached;
    private string _message = "";

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
        Launch(new FreeRdpLaunchContext { ParentWindow = xid, AvailableWidth = width, AvailableHeight = height, DrivePath = HomePath() });
    }

    /// <summary>Incorporare non è possibile (Wayland, client SDL, più monitor): finestra separata.</summary>
    public void StartDetached(int width = 1280, int height = 720)
    {
        if (_started || _closed)
            return;
        IsEmbedded = false;
        _detached = true;
        Launch(new FreeRdpLaunchContext { AvailableWidth = width, AvailableHeight = height, DrivePath = HomePath() });
    }

    public void Reconnect()
    {
        if (_isRunning || _starting || _closed)
            return;
        Launch(_context);
    }

    private static string? HomePath() => Environment.GetEnvironmentVariable("HOME");

    private void Launch(FreeRdpLaunchContext context)
    {
        _context = context with { Gateway = _gateway };
        _started = true;
        _starting = true;
        Title = _connection.DisplayName;
        Message = IsEmbedded ? "Connessione in corso…" : "Sessione aperta in una finestra separata.";
        RaiseView();
        ReconnectCommand.RaiseCanExecuteChanged();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var vpnError = await _prepareVpn(_cts.Token).ConfigureAwait(false);
            if (vpnError is not null)
            {
                _post(() => Ended(vpnError));
                return;
            }
            if (_closed)
                return;
            var command = FreeRdpCommandBuilder.Build(_connection, _client, _context);
            var process = FreeRdpProcess.Start(command);
            process.Exited += (code, err) => _post(() => OnProcessExited(process, code, err));
            _post(() =>
            {
                if (_closed)
                {
                    _ = process.StopAsync();
                    return;
                }
                _process = process;
                _starting = false;
                IsRunning = !process.HasExited;
                if (process.HasExited)
                    return;
                Message = IsEmbedded ? "" : "Sessione aperta in una finestra separata. Chiudendo la scheda si chiude anche la finestra.";
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

    private void OnProcessExited(FreeRdpProcess process, int exitCode, string error)
    {
        if (_closed || !ReferenceEquals(_process, process) && _process is not null)
            return;
        _process = null;
        var detail = LastLine(error);
        var message = exitCode == 0
            ? "La sessione è terminata."
            : "La sessione si è chiusa (codice " + exitCode + ")." + (detail.Length > 0 ? "\n" + detail : "");
        Ended(message);
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
        if (_closed)
            return;
        _closed = true;
        _cts.Cancel();
        var process = _process;
        _process = null;
        if (process is not null)
            StopTask = process.StopAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Termina quando il processo è stato fermato (dopo <see cref="Close"/>).</summary>
    public Task StopTask { get; private set; } = Task.CompletedTask;
}
