using System.Collections.ObjectModel;
using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Features;

/// <summary>Una riga della plancia VPN: il testo viene da <see cref="VpnDashboardModel"/> (lo stesso di Windows).</summary>
public sealed record VpnDashboardItem(VpnDashboardRow Row)
{
    public string Name => Row.Name;
    public string Address => Row.Address;
    public string StateText => Row.StateText;
    public string LastContactText => Row.LastContactText;
    public bool Connected => Row.Connected;
    public string DeviceName => Row.Device;
    public bool IsRenamed => Row.IsRenamed;

    /// <summary>Seconda riga sotto il nome: il nome vero del PC (se rinominato) e il proprietario.</summary>
    public string SubText => string.Join(" · ", new[] { Row.IsRenamed ? Row.DeviceName : null, Row.Owner }.Where(s => !string.IsNullOrEmpty(s)));
    public bool HasSubText => SubText.Length > 0;

    /// <summary>Cerchio dello stato: verde se collegato, grigio altrimenti (colori di <see cref="UiColors"/>).</summary>
    public string StatusColor => Row.Connected ? UiColors.Connected : UiColors.Unreachable;
}

/// <summary>
/// Menu «Plancia»: i PC della VPN (collegati per primi), gli stessi dati della pagina web /vpn del server.
/// La finestra chiama <see cref="RefreshAsync"/> ogni <see cref="VpnDashboardModel.RefreshSeconds"/> secondi.
/// </summary>
public sealed class VpnDashboardViewModel : ViewModelBase
{
    private readonly Func<bool> _signedIn;
    private readonly Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> _load;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _cts = new();
    private VpnDashboardItem? _selected;
    private VpnDashboardView _view = VpnDashboardModel.NotSignedIn();
    private bool _loading;

    private readonly Func<string, string, CancellationToken, Task>? _send;
    private readonly Func<string, Task<string?>>? _pickFile;
    private readonly IDictionary<string, string> _names;
    private readonly Action _saveNames;
    private readonly Func<string, string, Task<string?>>? _askName;
    private string _query = "";
    private bool _sending;
    private string _sendInfo = "";
    private string? _sendError;

    /// <param name="send">Invia il file (nome del PC, percorso): senza, «Invia file…» non c'è.</param>
    /// <param name="pickFile">Sceglie il file da inviare al PC indicato; null se annullato.</param>
    public VpnDashboardViewModel(Func<bool> signedIn, Func<CancellationToken, Task<IReadOnlyList<VpnDashboardDevice>>> load,
        Func<DateTimeOffset>? now = null, Func<string, string, CancellationToken, Task>? send = null,
        Func<string, Task<string?>>? pickFile = null, IDictionary<string, string>? names = null, Action? saveNames = null,
        Func<string, string, Task<string?>>? askName = null)
    {
        _names = names ?? new Dictionary<string, string>();
        _saveNames = saveNames ?? (() => { });
        _askName = askName;
        RenameCommand = new RelayCommand(() => _ = RenameSelectedAsync(), () => CanRename);
        ResetNameCommand = new RelayCommand(ResetSelectedName, () => Selected is { IsRenamed: true });
        _signedIn = signedIn;
        _load = load;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _send = send;
        _pickFile = pickFile;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !Loading);
        OpenCommand = new RelayCommand(OpenSelected, () => CanOpen);
        SendFileCommand = new RelayCommand(() => _ = SendFileAsync(), () => CanSendFile);
    }

    public RelayCommand SendFileCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand ResetNameCommand { get; }

    /// <summary>Si può dare un nome al PC selezionato (c'è chi chiede il nome).</summary>
    public bool CanRename => _askName is not null && Selected is { Row.Key.Length: > 0 };

    /// <summary>Testo della casella di ricerca: filtra per nome, nome del PC, IP o proprietario.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? ""))
                Fill();
        }
    }

    /// <summary>«6 di 9 PC in linea».</summary>
    public string SummaryText => VpnDashboardModel.Summary(_view);

    /// <summary>«Rinomina…»: chiede il nome da mostrare per il PC selezionato (vuoto = nome originale) e lo salva.</summary>
    public async Task RenameSelectedAsync()
    {
        if (_askName is null || Selected is not { } pc)
            return;
        var name = await _askName(pc.DeviceName, pc.Name);
        if (name is null)
            return;
        ApplyName(pc, name);
    }

    /// <summary>«Nome originale»: il PC torna a chiamarsi come nella VPN.</summary>
    public void ResetSelectedName()
    {
        if (Selected is { } pc)
            ApplyName(pc, "");
    }

    private void ApplyName(VpnDashboardItem pc, string name)
    {
        if (!VpnDashboardModel.SetName(_names, pc.Row, name))
            return;
        _saveNames();
        var address = pc.Address;
        var rows = _view.Rows.Select(r => r.Key == pc.Row.Key
            ? r with { Name = _names.TryGetValue(r.Key, out var n) ? n : r.DeviceName }
            : r).OrderByDescending(r => r.Connected).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _view = _view with { Rows = rows };
        Fill();
        Selected = Rows.FirstOrDefault(r => r.Address == address);
    }

    /// <summary>La plancia offre «Invia file…» (c'è chi invia e chi sceglie il file).</summary>
    public bool CanSendFeature => _send is not null && _pickFile is not null;

    public bool CanSendFile => CanSendFeature && !_sending && Selected is not null;

    /// <summary>Esito dell'invio andato bene (vuoto altrimenti).</summary>
    public string SendInfoText
    {
        get => _sendInfo;
        private set => Set(ref _sendInfo, value);
    }

    /// <summary>Motivo dell'invio non riuscito (vuoto altrimenti).</summary>
    public string? SendErrorText
    {
        get => _sendError;
        private set => Set(ref _sendError, value);
    }

    /// <summary>«Invia file…»: sceglie il file, lo manda al PC selezionato e riporta l'esito (nessuna finestra qui).</summary>
    public async Task SendFileAsync()
    {
        if (_send is null || _pickFile is null || _sending || Selected is not { } pc || _cts.IsCancellationRequested)
            return;
        var to = pc.DeviceName;
        var path = await _pickFile(pc.Name);
        if (string.IsNullOrEmpty(path))
            return;
        var fileName = Path.GetFileName(path);
        _sending = true;
        SendErrorText = null;
        SendInfoText = $"Invio di «{fileName}» a {to}…";
        SendFileCommand.RaiseCanExecuteChanged();
        try
        {
            await _send(to, path, _cts.Token);
            SendInfoText = VpnFileModel.SentText(to, fileName);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            SendInfoText = "";
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                       or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            SendInfoText = "";
            SendErrorText = VpnFileModel.SendError(ex);
        }
        finally
        {
            _sending = false;
            SendFileCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<VpnDashboardItem> Rows { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }

    /// <summary>Il PC da cui l'utente ha chiesto il desktop remoto; la finestra si chiude e chi l'ha aperta lo collega.</summary>
    public VpnDashboardItem? Chosen { get; private set; }

    /// <summary>Scatta quando <see cref="Chosen"/> è stato scelto: la finestra si chiude.</summary>
    public event EventHandler? CloseRequested;

    public VpnDashboardView Current => _view;

    public VpnDashboardItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
                SendFileCommand.RaiseCanExecuteChanged();
                RenameCommand.RaiseCanExecuteChanged();
                ResetNameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOpen => Selected is { } s && VpnDashboardView.CanOpen(s.Row);

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (Set(ref _loading, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Messaggio d'errore (account mancante, server che non risponde…); null se tutto bene.</summary>
    public string? ErrorText => _view.Status is VpnDashboardStatus.Ok or VpnDashboardStatus.Empty ? null : _view.Message;

    /// <summary>Messaggio non d'errore (elenco vuoto).</summary>
    public string InfoText => _view.Status == VpnDashboardStatus.Empty ? _view.Message : "";

    public bool HasInfo => InfoText.Length > 0;

    public void OpenSelected()
    {
        if (Selected is not { } item || !VpnDashboardView.CanOpen(item.Row))
            return;
        Chosen = item;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ferma una lettura in corso (alla chiusura della finestra).</summary>
    public void Cancel() => _cts.Cancel();

    public async Task RefreshAsync()
    {
        if (Loading || _cts.IsCancellationRequested)
            return;
        Loading = true;
        try
        {
            VpnDashboardView view;
            if (!_signedIn())
            {
                view = VpnDashboardModel.NotSignedIn();
            }
            else
            {
                try
                {
                    view = VpnDashboardModel.FromDevices(await _load(_cts.Token), _now(), new Dictionary<string, string>(_names));
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                               or HttpRequestException or IOException)
                {
                    view = VpnDashboardModel.FromError(ex);
                }
            }
            Show(view);
        }
        finally
        {
            Loading = false;
        }
    }

    private void Show(VpnDashboardView view)
    {
        _view = view;
        Fill();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(HasInfo));
        OpenCommand.RaiseCanExecuteChanged();
        SendFileCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Righe da mostrare: quelle della plancia che passano il filtro, mantenendo la selezione.</summary>
    private void Fill()
    {
        var selected = Selected?.Address;
        Rows.Clear();
        foreach (var row in _view.Rows.Where(r => VpnDashboardModel.Matches(r, _query)))
            Rows.Add(new VpnDashboardItem(row));
        Selected = selected is null ? null : Rows.FirstOrDefault(r => r.Address == selected);
        OnPropertyChanged(nameof(SummaryText));
    }
}
