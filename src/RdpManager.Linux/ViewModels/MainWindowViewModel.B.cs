using System.Runtime.CompilerServices;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Sessions;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.ViewModels;

// Blocco B: sincronizzazione, VPN, spazi di lavoro, schede, finestre, tocco, aggiornamenti.
// I comandi sono creati al primo uso (così non serve toccare il costruttore, condiviso con il blocco A);
// il nome delle proprietà è quello legato alle voci di Menus/MainMenu.axaml.
public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, RelayCommand> _bCommands = [];
    private bool _bHooked;
    private IShellUi _ui = new NullShellUi();
    private ShortcutMap _shortcuts = new();
    private bool _isFullScreen, _sidebarVisible = true, _sidebarHover, _sidebarPinned = true, _touchSeen;
    private SessionTabViewModel? _splitLeft, _splitRight;
    private SyncService? _sync;
    private SyncOutcome? _lastSync;
    private bool _syncApplying, _syncWasOffline, _autoSync;
    private Timer? _syncDelay, _syncPoll;
    private readonly AutoUpdatePlanner _updatePlanner = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Le finestre e le operazioni di sistema del blocco B (impostata all'avvio dall'interfaccia).</summary>
    public IShellUi Ui
    {
        get => _ui;
        set => _ui = value ?? new NullShellUi();
    }

    /// <summary>Programmi per tasti e foto verso la sessione.</summary>
    public RemoteInput Input { get; set; } = new();

    /// <summary>Crea il gestore VPN (nelle prove si sostituisce).</summary>
    public Func<LinuxWireGuard> VpnFactory { get; set; } = () => LinuxPlatformInit.Vpn;

    /// <summary>Controllo degli aggiornamenti (nelle prove si sostituisce); di norma interroga GitHub.</summary>
    public Func<CancellationToken, Task<UpdateCheckResult>> UpdateCheck { get; set; } =
        ct => UpdateChecker.CheckAsync(SharedHttp, AppInfo.Version, TimeSpan.FromSeconds(15), ct);

    private static readonly HttpClient SharedHttp = new();

    /// <summary>Percorso del profilo VPN (null = quello predefinito; nelle prove una cartella temporanea).</summary>
    public string? VpnProfilePath { get; set; }

    // --- Avvio e uscita ---

    /// <summary>
    /// Da chiamare una volta all'avvio dall'interfaccia: collega le finestre, riapre le sessioni lasciate aperte,
    /// avvia la sincronizzazione automatica e il controllo degli aggiornamenti.
    /// </summary>
    public void StartBlockB(IShellUi ui, bool background = true)
    {
        Ui = ui;
        EnsureBHooks();
        ReloadShortcuts();
        _sidebarPinned = Document.Settings.SidebarMode == SidebarMode.Pinned;
        _sidebarVisible = _sidebarPinned;
        RaiseLayout();
        ReopenSessions();
        if (!background)
            return;
        _autoSync = true;
        if (SyncService.SignedIn)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSyncAsync(manual: false);
                    if (await VpnViewModel.EnsureAsync(SyncService, VpnFactory(), VpnProfilePath, _shutdown.Token) is { Length: > 0 } line)
                        Notice(line);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    // Un errore dell'avvio automatico non si perde: finisce nel registro e in un avviso.
                    AppLog.Default?.Info("Avvio automatico (sincronizzazione e VPN) non riuscito: " + ex.Message);
                    Notice("Sincronizzazione o VPN non riuscite all'avvio: " + ex.Message);
                }
            });
        _syncPoll = new Timer(_ => _ = RunSyncAsync(manual: false), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _ = CheckUpdatesAsync(manual: false);
    }

    /// <summary>Chiusura del programma: ricorda le sessioni aperte, ferma i timer.</summary>
    public void ShutdownBlockB()
    {
        SaveOpenSessions();
        _shutdown.Cancel();
        _syncDelay?.Dispose();
        _syncPoll?.Dispose();
        _sync?.Dispose();
    }

    private void EnsureBHooks()
    {
        if (_bHooked)
            return;
        _bHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedTab))
                RaiseTabCommands();
        };
        Tabs.CollectionChanged += (_, _) =>
        {
            RaiseTabCommands();
            DropSplitIfTabGone();
        };
    }

    private void RaiseTabCommands()
    {
        foreach (var c in _bCommands.Values)
            c.RaiseCanExecuteChanged();
    }

    private RelayCommand Cmd(Action action, Func<bool>? can = null, [CallerMemberName] string name = "")
    {
        EnsureBHooks();
        if (!_bCommands.TryGetValue(name, out var command))
            _bCommands[name] = command = GateWhenLocked(new RelayCommand(action, can));
        return command;
    }

    private RelayCommand AsyncCmd(Func<Task> action, Func<bool>? can = null, [CallerMemberName] string name = "") =>
        Cmd(() => Run(action), can, name);

    /// <summary>Una riga nella barra di stato (anche da un altro thread).</summary>
    public void ShowNotice(string text) => Notice(text);

    private void Notice(string text) => Ui.Invoke(() => { StatusMessage = text; return 0; });

    private FreeRdpSession? SelectedSession => SelectedTab?.Session as FreeRdpSession;

    private bool HasSelectedSession => SelectedSession is not null;

    // --- Sessione: riconnetti, tasti, foto ---

    /// <summary>Sessione, Riconnetti (#34, anche F5).</summary>
    public RelayCommand ReconnectCommand => Cmd(() => SelectedSession?.Reconnect(), () => HasSelectedSession);

    /// <summary>Sessione, Stacca in una finestra (#36).</summary>
    public RelayCommand DetachTabCommand => Cmd(() => { if (SelectedTab is { } t) DetachRequested?.Invoke(this, t); }, () => HasSelectedSession);

    /// <summary>Sessione, Incolla appunti come digitazione (#31).</summary>
    public RelayCommand PasteAsTypingCommand => AsyncCmd(PasteAsTypingAsync, () => HasSelectedSession);

    /// <summary>Sessione, Invia Ctrl+Alt+Canc (#31).</summary>
    public RelayCommand SendCtrlAltDelCommand => AsyncCmd(async () =>
        Notice(await Input.SendCtrlAltDelAsync() ?? "Ctrl+Alt+Canc inviato alla sessione."), () => HasSelectedSession);

    /// <summary>Sessione, Tastiera su schermo (#31).</summary>
    public RelayCommand ScreenKeyboardCommand => AsyncCmd(async () =>
        Notice(await Input.OpenScreenKeyboardAsync() ?? "Tastiera su schermo aperta."));

    /// <summary>Sessione, Foto della sessione (#31).</summary>
    public RelayCommand SnapshotCommand => AsyncCmd(SnapshotAsync, () => HasSelectedSession);

    public async Task PasteAsTypingAsync()
    {
        if (IsLocked)
            return;
        if (SelectedSession is null)
            return;
        var text = await Ui.GetClipboardTextAsync();
        Notice(await Input.TypeAsync(text ?? "") ?? "Appunti digitati nella sessione.");
    }

    public async Task SnapshotAsync()
    {
        if (IsLocked)
            return;
        if (SelectedSession is not { } session)
            return;
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(folder))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Immagini");
        var (path, error) = await Input.SnapshotAsync(session.SurfaceXid, session.DisplayTitle, folder);
        Notice(path is not null ? $"Foto della sessione salvata in {path}." : error ?? "Foto non riuscita.");
    }

    /// <summary>Azione da un gesto a più dita (#48, #49): comandi della finestra, oppure tasti verso il PC remoto con xdotool.</summary>
    public void RunTouchAction(TouchActionKind kind)
    {
        if (IsLocked)
            return;
        switch (kind)
        {
            case TouchActionKind.NextTab: NextTabCommand.Execute(null); break;
            case TouchActionKind.PreviousTab: PreviousTabCommand.Execute(null); break;
            case TouchActionKind.ShowBar: SidebarVisible = true; break;
            case TouchActionKind.HideBar: SidebarVisible = false; break;
            case TouchActionKind.AltTab: Run(() => SendKeysAsync("alt+Tab")); break;
            case TouchActionKind.ShowDesktop: Run(() => SendKeysAsync("super+d")); break;
            case TouchActionKind.TaskView: Run(() => SendKeysAsync("super+Tab")); break;
            case TouchActionKind.RightClick: Run(() => SendKeysAsync("click:3")); break;
        }
    }

    private async Task SendKeysAsync(string combo) =>
        Notice(await Input.SendKeysAsync(combo) ?? "");

    // --- Schede ---

    /// <summary>Sessione, Scheda successiva (#33).</summary>
    public RelayCommand NextTabCommand => Cmd(() => MoveTab(forward: true), () => Tabs.Count > 1);

    /// <summary>Sessione, Scheda precedente (#33).</summary>
    public RelayCommand PreviousTabCommand => Cmd(() => MoveTab(forward: false), () => Tabs.Count > 1);

    private void MoveTab(bool forward)
    {
        if (Tabs.Count == 0)
            return;
        var connected = Tabs.Select(t => t.IsConnected).ToList();
        if (!connected.Contains(true))
            connected = Tabs.Select(_ => true).ToList(); // nessuna connessa: si scorrono tutte
        var index = SessionCycler.Next(connected, SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab), forward);
        if (index >= 0)
            SelectedTab = Tabs[index];
    }

    /// <summary>Azione da tastiera: passaggio fra sessioni, schermo intero, barra, panoramica, ricerca, vista affiancata, blocco.</summary>
    public bool RunShortcut(ShortcutAction action)
    {
        if (IsLocked)
            return false;
        var connected = Tabs.Select(t => t.IsConnected).ToList();
        if (ShortcutAction.NextSession == action || ShortcutAction.PreviousSession == action || ShortcutMap.SessionNumber(action) is not null)
        {
            if (SessionCycler.Target(action, connected, SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab)) is int i)
                SelectedTab = Tabs[i];
            return true;
        }
        switch (action)
        {
            case ShortcutAction.ToggleFullScreen: FullScreenCommand.Execute(null); break;
            case ShortcutAction.ToggleSidebar: ToggleSidebarCommand.Execute(null); break;
            case ShortcutAction.Overview: OverviewCommand.Execute(null); break;
            case ShortcutAction.ToggleSplitView: SplitViewCommand.Execute(null); break;
            case ShortcutAction.QuickSearch: if (QuickSearchCommand.CanExecute(null)) QuickSearchCommand.Execute(null); break;
            case ShortcutAction.Lock: if (LockCommand.CanExecute(null)) LockCommand.Execute(null); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Una combinazione premuta nella finestra: scorciatoia dell'app o di uno spazio di lavoro. Vero se gestita.</summary>
    public bool TryRunShortcut(KeyCombo combo)
    {
        if (combo.IsEmpty || IsLocked)
            return false;
        if (_shortcuts.TryGetAction(combo, out var action))
            return RunShortcut(action);
        var workspace = Document.Workspaces.FirstOrDefault(w => w.Shortcut.Length > 0
            && KeyCombo.TryParse(w.Shortcut, out var c) && c == combo);
        if (workspace is null)
            return false;
        OpenWorkspace(workspace);
        return true;
    }

    /// <summary>Le scorciatoie in vigore, dalle impostazioni.</summary>
    public ShortcutMap Shortcuts => _shortcuts;

    public void ReloadShortcuts() => _shortcuts = ShortcutMap.FromSettings(Document.Settings.Shortcuts);

    // --- Schermo intero, monitor, barra laterale, vista affiancata ---

    /// <summary>La finestra va a schermo intero o ne esce (la applica <c>MainWindow</c>).</summary>
    public event EventHandler<bool>? FullScreenChanged;

    /// <summary>La finestra va spostata sul monitor successivo (la applica <c>MainWindow</c>).</summary>
    public event EventHandler? MoveToNextMonitorRequested;

    /// <summary>Si chiede di staccare (o riagganciare) la sessione della scheda in una finestra (la apre <c>MainWindow</c>).</summary>
    public event EventHandler<SessionTabViewModel>? DetachRequested;

    /// <summary>Un monitor da usare per la finestra (spazi di lavoro); null = quello attuale.</summary>
    public event EventHandler<string?>? MonitorRequested;

    /// <summary>La disposizione (barra laterale, vista affiancata, schermo intero) è cambiata.</summary>
    public event EventHandler? LayoutChanged;

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(IsFullScreen));
        OnPropertyChanged(nameof(SidebarVisible));
        OnPropertyChanged(nameof(SidebarShown));
        OnPropertyChanged(nameof(SidebarPinned));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(ChromeVisible));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (_isFullScreen == value)
                return;
            _isFullScreen = value;
            RaiseLayout();
            FullScreenChanged?.Invoke(this, value);
        }
    }

    /// <summary>Menu, barra comandi e barra di stato si vedono: no a schermo intero.</summary>
    public bool ChromeVisible => !IsFullScreen;

    /// <summary>Visualizza, Schermo intero (#40, anche F11).</summary>
    public RelayCommand FullScreenCommand => Cmd(() => IsFullScreen = !IsFullScreen);

    /// <summary>Visualizza, Sposta la finestra sul monitor successivo (#41).</summary>
    public RelayCommand MoveToNextMonitorCommand => Cmd(() => MoveToNextMonitorRequested?.Invoke(this, EventArgs.Empty));

    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set { if (_sidebarVisible != value) { _sidebarVisible = value; RaiseLayout(); } }
    }

    /// <summary>Il puntatore è sul bordo sinistro: con la barra non fissa la fa comparire.</summary>
    public bool SidebarHover
    {
        get => _sidebarHover;
        set { if (_sidebarHover != value) { _sidebarHover = value; RaiseLayout(); } }
    }

    public bool SidebarPinned => _sidebarPinned;

    /// <summary>La barra laterale si vede: scelta con il comando, oppure di passaggio con il mouse se non è fissa.</summary>
    public bool SidebarShown => SidebarVisible || (!SidebarPinned && SidebarHover);

    /// <summary>Visualizza, Mostra/nascondi barra laterale (#42).</summary>
    public RelayCommand ToggleSidebarCommand => Cmd(() => SidebarVisible = !SidebarVisible);

    /// <summary>Visualizza, Barra laterale fissa (#42): fissa = sempre visibile; non fissa = a comparsa dal bordo sinistro.</summary>
    public RelayCommand PinSidebarCommand => Cmd(() =>
    {
        _sidebarPinned = !_sidebarPinned;
        Document.Settings.SidebarMode = _sidebarPinned ? SidebarMode.Pinned : SidebarMode.AutoHide;
        _sidebarVisible = _sidebarPinned;
        Save();
        RaiseLayout();
        StatusMessage = _sidebarPinned ? "Barra laterale fissa." : "Barra laterale a comparsa: avvicina il mouse al bordo sinistro.";
    });

    /// <summary>Il PC ha uno schermo touch (lo rileva l'interfaccia all'avvio).</summary>
    public bool HasTouchScreen { get; set; }

    /// <summary>Un dito ha toccato la finestra: con la modalità tocco «Automatica» l'interfaccia si ingrandisce.</summary>
    public void NoteTouchSeen()
    {
        if (_touchSeen)
            return;
        _touchSeen = true;
        OnPropertyChanged(nameof(TouchActive));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Modalità tocco in vigore (#47): impostazione «Tocco», con «Automatica» che guarda lo schermo e i tocchi visti.</summary>
    public bool TouchActive => TouchPolicy.IsActive(Document.Settings.TouchMode, HasTouchScreen || _touchSeen);

    public bool IsSplit => _splitLeft is not null && _splitRight is not null;

    public SessionTabViewModel? SplitLeft => _splitLeft;

    public SessionTabViewModel? SplitRight => _splitRight;

    public double SplitRatio
    {
        get => Document.Settings.SplitRatio;
        set => Document.Settings.SplitRatio = SplitLayout.ClampRatio(value);
    }

    /// <summary>Visualizza, Vista affiancata (#37): la scheda scelta a sinistra e la successiva a destra.</summary>
    public RelayCommand SplitViewCommand => Cmd(ToggleSplit);

    public void ToggleSplit()
    {
        if (IsSplit)
        {
            SetSplit(null, null);
            return;
        }
        if (Tabs.Count < 2)
        {
            StatusMessage = "Per la vista affiancata servono due sessioni aperte.";
            return;
        }
        var left = SelectedTab ?? Tabs[0];
        var right = Tabs[(Tabs.IndexOf(left) + 1) % Tabs.Count];
        SetSplit(left, right);
    }

    public void SetSplit(SessionTabViewModel? left, SessionTabViewModel? right)
    {
        _splitLeft = left;
        _splitRight = left is null ? null : right;
        RaiseLayout();
    }

    private void DropSplitIfTabGone()
    {
        if (IsSplit && (!Tabs.Contains(_splitLeft!) || !Tabs.Contains(_splitRight!)))
            SetSplit(null, null);
    }

    // --- Panoramica ---

    /// <summary>Visualizza, Panoramica delle sessioni (#39).</summary>
    public RelayCommand OverviewCommand => AsyncCmd(async () =>
    {
        if (Tabs.Count == 0)
        {
            StatusMessage = "Nessuna sessione aperta.";
            return;
        }
        if (await Ui.ShowOverviewAsync([.. Tabs], SelectedTab) is { } chosen && Tabs.Contains(chosen))
            SelectedTab = chosen;
    });

    // --- Spazi di lavoro ---

    /// <summary>Spazi di lavoro, Salva lo spazio attuale (#43).</summary>
    public RelayCommand SaveWorkspaceCommand => AsyncCmd(SaveWorkspaceAsync);

    /// <summary>Spazi di lavoro, Gestisci (#43).</summary>
    public RelayCommand ManageWorkspacesCommand => AsyncCmd(async () =>
        await Ui.ShowWorkspacesAsync(new WorkspacesViewModel(Document, Save, OpenWorkspace, _dialogs)));

    /// <summary>Il layout attuale, da salvare in uno spazio di lavoro.</summary>
    public WorkspaceLayout CurrentLayout(string? monitor = null) => new(
        Tabs.Select(t => t.Session.ConnectionId).ToList(),
        SelectedTab?.Session.ConnectionId,
        monitor ?? Document.Settings.PreferredMonitor,
        IsFullScreen,
        IsSplit ? _splitLeft!.Session.ConnectionId : null,
        IsSplit ? _splitRight!.Session.ConnectionId : null,
        SplitRatio);

    /// <summary>Il monitor su cui sta la finestra, per salvarlo negli spazi di lavoro (lo imposta <c>MainWindow</c>).</summary>
    public string? CurrentMonitor { get; set; }

    public async Task SaveWorkspaceAsync()
    {
        if (Tabs.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Spazi di lavoro", "Apri prima le connessioni che vuoi salvare: lo spazio di lavoro ricorda le schede aperte.");
            return;
        }
        var name = await _dialogs.PromptAsync("Salva lo spazio di lavoro", "Nome dello spazio di lavoro", "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var existing = Document.Workspaces.Any(w => w.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (existing && !await _dialogs.ConfirmAsync("Spazi di lavoro", $"Esiste già lo spazio «{name.Trim()}». Aggiornarlo con le schede attuali?"))
            return;
        var ws = Document.SaveWorkspace(name, CurrentLayout(CurrentMonitor));
        if (Save())
            StatusMessage = $"Spazio di lavoro «{ws.Name}» salvato ({ws.Connections.Count} connessioni).";
    }

    /// <summary>Riapre uno spazio di lavoro: apre le connessioni che mancano, sceglie la scheda, la vista affiancata, il monitor e lo schermo intero.</summary>
    public void OpenWorkspace(Workspace workspace)
    {
        var plan = Document.PlanWorkspace(workspace);
        foreach (var c in plan.Open)
            if (!Tabs.Any(t => t.Session.ConnectionId == c.Id))
                Connect(c);
        if (TabFor(plan.Selected) is { } selected)
            SelectedTab = selected;
        if (TabFor(plan.SplitLeft) is { } l && TabFor(plan.SplitRight) is { } r)
        {
            SplitRatio = plan.SplitRatio;
            SetSplit(l, r);
        }
        else
            SetSplit(null, null);
        if (plan.Monitor is not null)
            MonitorRequested?.Invoke(this, plan.Monitor);
        IsFullScreen = plan.FullScreen;
        StatusMessage = plan.Missing > 0
            ? $"Spazio «{workspace.Name}» aperto; {plan.Missing} connessioni non esistono più."
            : $"Spazio «{workspace.Name}» aperto.";
    }

    private SessionTabViewModel? TabFor(RdpConnection? c) => c is null ? null : Tabs.FirstOrDefault(t => t.Session.ConnectionId == c.Id);

    // --- Riapertura delle sessioni all'avvio ---

    /// <summary>Annota le connessioni aperte, nell'ordine delle schede (alla chiusura del programma).</summary>
    public void SaveOpenSessions()
    {
        var ids = Tabs.Select(t => t.Session.ConnectionId).Distinct().ToList();
        if (Document.Settings.OpenSessions.SequenceEqual(ids))
            return;
        Document.Settings.OpenSessions = ids;
        Save();
    }

    /// <summary>
    /// Riapre le sessioni lasciate aperte, se la riapertura è attiva (impostazione «Ricorda le ultime connessioni aperte»).
    /// Restituisce quante ne ha aperte.
    /// </summary>
    public int ReopenSessions()
    {
        var ids = Document.Settings.TakeSessionsToReopen();
        int opened = 0;
        foreach (var id in ids)
            if (Document.Find(id) is { } c && !Tabs.Any(t => t.Session.ConnectionId == id))
            {
                Connect(c);
                opened++;
            }
        if (opened > 0)
            StatusMessage = opened == 1 ? "Riaperta la sessione dell'ultima volta." : $"Riaperte {opened} sessioni dell'ultima volta.";
        return opened;
    }

    // --- Account, sincronizzazione, VPN ---

    /// <summary>Il servizio di sincronizzazione (creato al primo uso).</summary>
    public SyncService SyncService => _sync ??= new SyncService(SyncState.PathFor(_store));

    /// <summary>Strumenti, Account e sincronizzazione (#60, #61, #62).</summary>
    public RelayCommand SyncAccountCommand => AsyncCmd(async () =>
    {
        var account = new AccountViewModel(SyncService, () => RunSyncAsync(manual: true), () => _lastSync, Ui, _dialogs,
            () => VpnViewModel.EnsureAsync(SyncService, VpnFactory(), VpnProfilePath, _shutdown.Token));
        await Ui.ShowAccountAsync(account);
    });

    /// <summary>Strumenti, VPN WireGuard (#63, #64, #65).</summary>
    public RelayCommand VpnCommand => AsyncCmd(async () =>
    {
        var vpn = new VpnViewModel(VpnFactory(), SyncService, Ui, _dialogs, VpnProfilePath);
        await vpn.RefreshStateAsync();
        try { await Ui.ShowVpnAsync(vpn); }
        finally { vpn.Persist(); }
    });

    /// <summary>L'accesso al documento che usa la sincronizzazione (per le prove).</summary>
    internal ISyncDocumentAccess SyncDocumentAccess => new SyncAccess(this);

    private sealed class SyncAccess(MainWindowViewModel vm) : ISyncDocumentAccess
    {
        public ConnectionsDocument Snapshot() => vm.Ui.Invoke(() => SyncMerge.Snapshot(vm.Document));

        public bool Apply(Func<ConnectionsDocument, bool> change) => vm.Ui.Invoke(() =>
        {
            if (!change(vm.Document))
                return false;
            vm._syncApplying = true;
            try
            {
                // Se il salvataggio locale non riesce la sincronizzazione deve fallire: altrimenti la base avanzerebbe
                // senza che i dati siano su disco e il giro dopo il server verrebbe svuotato.
                if (!vm.Save())
                    throw new IOException("Salvataggio locale non riuscito");
                vm.RebuildTree();
            }
            finally
            {
                vm._syncApplying = false;
            }
            return true;
        });
    }

    /// <summary>Dopo ogni salvataggio: se c'è qualcosa da mandare al server, sincronizza fra poco (senza bloccare niente).</summary>
    partial void AfterSave()
    {
        if (!_autoSync || _syncApplying || _shutdown.IsCancellationRequested || _sync is not { SignedIn: true })
            return;
        if (_sync.HasLocalChanges(SyncMerge.Snapshot(Document)))
        {
            _syncDelay?.Dispose();
            _syncDelay = new Timer(_ => _ = RunSyncAsync(manual: false), null, TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Sincronizza ora (a mano) o in automatico. Mai un blocco: l'esito è un avviso discreto.</summary>
    public async Task<SyncOutcome> RunSyncAsync(bool manual)
    {
        if (!SyncService.SignedIn)
            return new SyncOutcome(SyncStatus.NotSignedIn, "Nessun account di sincronizzazione.");
        if (_shutdown.IsCancellationRequested)
            return new SyncOutcome(SyncStatus.Error, "Il programma si sta chiudendo.");
        if (!_canSave)
            return new SyncOutcome(SyncStatus.Error, "Il file dei dati locale non era leggibile: non sincronizzo per non svuotare il server.");
        SyncOutcome outcome;
        try
        {
            outcome = await SyncService.SyncAsync(new SyncAccess(this), _shutdown.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
        {
            return new SyncOutcome(SyncStatus.Error, "Sincronizzazione interrotta: " + ex.Message);
        }
        _lastSync = outcome;
        AppLog.Default?.Info($"Sincronizzazione: {outcome.Status} - {outcome.Message}");
        if (outcome.Status == SyncStatus.Offline)
        {
            // Una volta sola per volta che il server sparisce (VPN spenta): niente avvisi a ogni tentativo.
            if (manual || !_syncWasOffline)
                Notice("Sincronizzazione: server non raggiungibile (VPN spenta?). Lavoro in locale.");
            _syncWasOffline = true;
        }
        else
        {
            _syncWasOffline = false;
            if (manual || outcome.LocalChanged || outcome.Status is SyncStatus.AuthFailed or SyncStatus.Unreadable or SyncStatus.Error)
                Notice("Sincronizzazione: " + outcome.Message);
        }
        return outcome;
    }

    // --- Aiuto: scorciatoie e aggiornamenti ---

    /// <summary>Aiuto, Scorciatoie da tastiera (#50, F1).</summary>
    public RelayCommand ShortcutsHelpCommand => AsyncCmd(() => Ui.ShowTextAsync("Scorciatoie da tastiera", _shortcuts.SummaryText()));

    /// <summary>Aiuto, Controlla aggiornamenti (#58).</summary>
    public RelayCommand CheckUpdatesCommand => AsyncCmd(() => CheckUpdatesAsync(manual: true));

    /// <summary>
    /// Controlla se c'è una versione nuova su GitHub. Su Linux non si installa da soli: si avvisa e si offre il link alla pagina
    /// della release. A mano mostra sempre un esito; in automatico (secondo l'impostazione) solo se c'è una novità.
    /// </summary>
    public async Task CheckUpdatesAsync(bool manual)
    {
        var mode = Document.Settings.UpdateMode;
        if (!manual && (mode == UpdateMode.Off || !_updatePlanner.IsCheckDue(DateTime.UtcNow)))
            return;
        var result = await UpdateCheck(_shutdown.Token);
        if (!manual && _shutdown.IsCancellationRequested)
            return;
        if (result.Status != UpdateCheckStatus.Unavailable)
            _updatePlanner.MarkChecked(DateTime.UtcNow);
        switch (result.Status)
        {
            case UpdateCheckStatus.Unavailable:
                if (manual)
                    Notice("Non riesco a controllare gli aggiornamenti: serve la connessione a Internet.");
                return;
            case UpdateCheckStatus.UpToDate:
                if (manual)
                    Notice($"RdpManager è aggiornato (versione {AppInfo.VersionText}).");
                return;
        }
        var release = result.Release!;
        var line = $"È disponibile la versione {release.Version.ToString(3)}: {release.PageUrl}";
        Notice(line);
        if (!manual && mode != UpdateMode.AutomaticWithConfirmation)
            return; // «Solo avviso»: resta la riga nella barra di stato
        if (await Ui.OfferUpdateAsync(release, AppInfo.VersionText))
            Ui.OpenUrl(release.PageUrl);
    }

    // --- Elenco rapido per l'area di notifica ---

    /// <summary>Connessioni da offrire nel menu dell'area di notifica: aperte, preferite, recenti.</summary>
    public IReadOnlyList<RdpConnection> OpenConnections() =>
        Tabs.Select(t => Document.Find(t.Session.ConnectionId)).OfType<RdpConnection>().ToList();
}
