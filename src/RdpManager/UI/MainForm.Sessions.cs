using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.UI;

// Sessioni, schermo intero dell'app, barra laterale, monitor e scorciatoie.
public sealed partial class MainForm
{
    private readonly Panel _root = new() { Dock = DockStyle.Fill };
    private readonly Panel _sidebar = new() { Dock = DockStyle.Left, Width = 280, BackColor = SystemColors.Window };
    private readonly Splitter _splitter = new() { Dock = DockStyle.Left, Width = 5, MinSize = 180, MinExtra = 300 };
    private readonly Panel _edge = new() { Dock = DockStyle.Left, Width = 6, BackColor = Color.FromArgb(200, 205, 215), Cursor = Cursors.Hand, Visible = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FullScreenSideBar _fsBar = new() { Visible = false };
    private readonly ToolStripButton _pinButton = new("A comparsa") { Alignment = ToolStripItemAlignment.Right, CheckOnClick = false };
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _edgeTimer = new() { Interval = 120 };
    private ShortcutMap _shortcuts = new();

    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;
    private Padding _tabInsets;

    /// <summary>Barra fissa nascosta a mano (Ctrl+Alt+B): resta nascosta finché non la si rimostra.</summary>
    private bool _sidebarHiddenByUser;
    /// <summary>Barra a comparsa attualmente aperta sopra le sessioni.</summary>
    private bool _sidebarRevealed;
    private int _sidebarAwayTicks, _fsBarAwayTicks;

    /// <summary>Barra fissa a schermo intero nascosta a mano (scorciatoia della barra laterale).</summary>
    private bool _fsBarHiddenByUser;
    /// <summary>Barra a schermo intero aperta sopra la sessione (a comparsa o richiamata).</summary>
    private bool _fsBarRevealed;
    /// <summary>Da quando il mouse è sul bordo della barra a comparsa (null = non c'è).</summary>
    private DateTime? _fsEdgeSince;
    private DateTime _fsBarShownUntil;
    private readonly System.Windows.Forms.Timer _thumbTimer = new() { Interval = 2000 };

    private void BuildLayout()
    {
        _sidebar.Controls.Add(_tree);
        _sidebar.Controls.Add(BuildTreeToolbar());
        _sidebar.Controls.Add(_search);
        _sidebar.Controls.Add(BuildSidebarHeader());

        _content.Controls.Add(_tabs);
        _content.Controls.Add(_emptyHint);
        _content.Resize += (_, _) => LayoutOverlays();

        // Ordine di aggiunta = ordine di aggancio dall'esterno verso l'interno, al contrario.
        _root.Controls.Add(_content);
        _root.Controls.Add(_splitter);
        _root.Controls.Add(_sidebar);
        _root.Controls.Add(_edge);
        _root.Controls.Add(_fsBar);
        _root.Resize += (_, _) => LayoutOverlays();

        _tips.SetToolTip(_edge, "Barra laterale");
        _edge.MouseEnter += (_, _) => ShowSidebar(focusSearch: false);
        _edge.Click += (_, _) => ShowSidebar(focusSearch: false);

        _fsBar.SessionClicked += (_, view) => SelectView(view);
        _fsBar.ConnectionClicked += (_, c) => OpenSession(c);
        _fsBar.WorkspaceClicked += (_, ws) => OpenWorkspace(ws);
        _fsBar.ActionClicked += (_, action) => ExecuteShortcut(action);
        _fsBar.SessionActionClicked += (_, action) => ExecuteSessionAction(action);
        _fsBar.QuickTextClicked += async (_, t) =>
        {
            if (CurrentView is { State: SessionState.Connected } v)
                await v.TypeTextAsync(t.TextToType());
        };
        _thumbTimer.Tick += (_, _) => RefreshBarThumbnails();
        _edgeTimer.Tick += (_, _) => OnEdgeTimer();
        _edgeTimer.Start();
    }

    private void ApplySettings()
    {
        _shortcuts = _doc.Settings.GetShortcuts();
        if (AppLog.Default is { } log)
            log.Verbose = _doc.Settings.VerboseLog;
        if (Form.ActiveForm == this || NativeMethods.IsOwnProcessForeground())
            RegisterSessionHotkeys();
        _fullScreenItem.ShortcutKeyDisplayString = $"F11, {_shortcuts[ShortcutAction.ToggleFullScreen]}";
        _sidebarItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.ToggleSidebar].ToString();
        _nextSessionItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.NextSession].ToString();
        _previousSessionItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.PreviousSession].ToString();
        _overviewItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.Overview].ToString();
        _lockItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.Lock].ToString();
        _quickSearchItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.QuickSearch].ToString();
        _splitItem.ShortcutKeyDisplayString = _shortcuts[ShortcutAction.ToggleSplitView].ToString();
        UpdateEmptyHint();
        ApplySidebarLayout();
        if (_fullScreen)
            UpdateFullScreenBounds();
    }

    private void ShowSettings()
    {
        var copy = _doc.Settings.Clone();
        // Le combinazioni premute nella finestra servono a registrare le scorciatoie (anche quella globale).
        _sessionHotkeysSuspended = true;
        UnregisterSessionHotkeys();
        UnregisterGlobalHotkey();
        bool ok;
        try
        {
            using var dlg = new SettingsDialog(copy);
            ok = dlg.ShowDialog(this) == DialogResult.OK;
        }
        finally
        {
            _sessionHotkeysSuspended = false;
        }
        if (!ok)
        {
            RegisterGlobalHotkey();
            return;
        }
        _doc.Settings = copy;
        SaveConnections();
        ApplySettings();
        ApplyTheme();
        ApplySystemIntegration();
    }

    // ---------- sessioni ----------

    /// <summary>Sessione su cui agiscono menu e scorciatoie: quella della scheda scelta, o nella vista affiancata quella col fuoco.</summary>
    internal RdpSessionView? CurrentView =>
        _splitLeft is { } left && _splitRight is { } right
            ? (right.ContainsFocus ? right : left.ContainsFocus ? left : _splitActive ?? left)
            : _tabs.SelectedTab?.Tag as RdpSessionView;

    internal IEnumerable<RdpSessionView> AllViews =>
        _tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).Concat(_detached.Select(d => d.View));

    private List<RdpSessionView> TabViews => _tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).ToList();

    private void ConnectSelected()
    {
        if (_multi.Count > 1)
            ConnectGroup();
        else if (SelectedConnection is { } c)
            OpenSession(c);
    }

    internal void OpenSession(RdpConnection c)
    {
        // Bloccata con il PIN: nessuna sessione nuova (né password lette) finché non si sblocca.
        if (_locked)
            return;
        // Una sessione per connessione: se è già aperta la si porta in primo piano.
        var existing = AllViews.FirstOrDefault(v => v.Connection == c);
        if (existing is not null)
        {
            SelectView(existing);
            if (existing.State == SessionState.Disconnected && !existing.ReconnectPending)
                existing.Connect();
            return;
        }

        var view = new RdpSessionView(c);
        var previous = SessionState.Idle;
        view.StateChanged += (_, _) =>
        {
            OnSessionStateChanged(view, previous);
            previous = view.State;
        };
        view.CloseRequested += (_, _) => CloseSession(view);
        view.DetachToggleRequested += (_, _) => ToggleDetach(view);
        view.FullScreenRequested += (_, _) => { if (!view.IsDetached) ToggleFullScreen(); };
        view.SnapshotRequested += async (_, _) => await SaveSnapshotAsync(view);
        view.AttentionRequired += (_, _) => BringSessionToFront(view);
        view.ResolveForConnect = connection => _doc.WithResolvedCredentials(connection);
        view.ResolveGateway = connection => _doc.ResolveGatewayCredentials(connection);
        view.QuickTexts = () => _doc.QuickTexts;
        view.ConnectBlocked = () => _locked;
        view.ManageQuickTextsRequested += (_, _) => ManageQuickTexts();
        ConfigureNewSession?.Invoke(view);
        AddTab(view);
        view.Connect();
    }

    private void OnSessionStateChanged(RdpSessionView view, SessionState previous)
    {
        RecordHistory(view, previous);
        UpdateSessionTab(view);
        if (view.State == SessionState.Connected && previous != SessionState.Connected)
        {
            _doc.MarkConnected(view.Connection.Id, DateTime.UtcNow);
            _reach[view.Connection.Id] = ReachabilityStatus.Reachable;
            SaveConnections();
            RefreshTree();
        }
        else
        {
            UpdateTreeStates();
        }
        UpdateFullScreenBarSessions();
    }

    /// <summary>Connette tutte le connessioni della cartella (e sottocartelle), ognuna nella sua scheda.</summary>
    internal void ConnectFolder(string folder)
    {
        var list = _doc.ConnectionsInFolder(folder);
        if (list.Count == 0)
            return;
        if (list.Count > 6 &&
            MessageBox.Show(this, $"Aprire {list.Count} sessioni insieme?", "Connetti tutta la cartella",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var c in list)
            OpenSession(c);
    }

    private void SelectView(RdpSessionView view)
    {
        if (view.IsDetached)
        {
            _detached.First(d => d.View == view).Activate();
            return;
        }
        if (IsSplit && !IsSplitView(view))
            LeaveSplit();
        if (!IsSplit && PageOf(view) is { } page)
            _tabs.SelectedTab = page;
        if (Form.ActiveForm != this)
            Activate();
        BeginInvoke(view.FocusSession);
    }

    private void AddTab(RdpSessionView view)
    {
        // Una scheda nuova sotto la vista affiancata resterebbe nascosta: si torna alle schede.
        if (IsSplit)
            LeaveSplit();
        var page = new TabPage { Tag = view };
        page.Controls.Add(view);
        view.ToolbarVisible = !_fullScreen;
        ThemeManager.Apply(view);
        _tabs.TabPages.Add(page);
        _tabs.Visible = true;
        _emptyHint.Visible = false;
        _tabs.SelectedTab = page;
        UpdateSessionTab(view);
    }

    /// <summary>Esito dell'ultima richiesta di primo piano (per le prove automatiche).</summary>
    internal WindowAttention.Result? LastAttentionResult { get; private set; }

    /// <summary>Rende visibile la sessione che ha aperto un avviso/richiesta modale: scheda selezionata, finestra ripristinata e in primo piano.</summary>
    private void BringSessionToFront(RdpSessionView view)
    {
        Form host;
        if (view.IsDetached)
        {
            host = _detached.First(d => d.View == view);
        }
        else
        {
            if (IsSplit && !IsSplitView(view))
                LeaveSplit();
            if (!IsSplitView(view) && PageOf(view) is { } page)
                _tabs.SelectedTab = page;
            host = this;
        }
        LastAttentionResult = WindowAttention.BringToFront(host);
    }

    private void UpdateSessionTab(RdpSessionView view)
    {
        if (PageOf(view) is { } page)
        {
            var c = view.Connection;
            page.Text = c.DisplayName + (c.Label.Length > 0 ? $" [{c.Label}]" : "") + "   ";
            page.ToolTipText = $"{RdpFile.FormatAddress(c.Host, c.Port)}\n{view.StateText}";
            page.ImageIndex = (int)view.State;
        }
        UpdateStatus();
    }

    private void UpdateSessionTitles()
    {
        foreach (var v in AllViews)
            UpdateSessionTab(v);
        UpdateFullScreenBarSessions();
    }

    internal void CloseSession(RdpSessionView view)
    {
        if (view.State == SessionState.Connected &&
            MessageBox.Show(this, $"Disconnettere «{view.Connection.DisplayName}» e chiudere la scheda?", "Chiudi sessione",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (IsSplitView(view))
            LeaveSplit();
        EndHistory(view, "Sessione chiusa in RdpManager");
        if (view.IsDetached)
        {
            var form = _detached.First(d => d.View == view);
            _detached.Remove(form);
            form.ReleaseView();
            form.Close();
        }
        else if (view.Parent is TabPage page)
        {
            int index = _tabs.TabPages.IndexOf(page);
            _tabs.TabPages.Remove(page);
            page.Dispose();
            if (_tabs.TabCount > 0)
                _tabs.SelectedIndex = Math.Min(index, _tabs.TabCount - 1);
        }
        view.Dispose();
        ShowHintIfEmpty();
        UpdateStatus();
        UpdateTreeStates();
        UpdateFullScreenBarSessions();
    }

    internal void ToggleDetach(RdpSessionView view)
    {
        if (view.IsDetached)
        {
            ReattachFromForm(_detached.First(d => d.View == view), closeForm: true);
            return;
        }
        if (IsSplitView(view))
            LeaveSplit();
        if (view.Parent is not TabPage page)
            return;
        page.Controls.Remove(view);
        _tabs.TabPages.Remove(page);
        page.Dispose();
        ShowHintIfEmpty();

        view.SetDetached(true);
        view.ToolbarVisible = true;
        var form = new DetachedSessionForm(view, Icon);
        ThemeManager.Apply(form);
        form.FormClosing += (_, e) =>
        {
            // Chiudere la finestra separata riporta la sessione in una scheda (non la disconnette).
            if (_detached.Contains(form) && e.CloseReason != CloseReason.ApplicationExitCall)
                ReattachFromForm(form, closeForm: false);
        };
        _detached.Add(form);
        form.ShowOn(Screens.Of(this));
        UpdateStatus();
    }

    private void ReattachFromForm(DetachedSessionForm form, bool closeForm)
    {
        _detached.Remove(form);
        form.ReleaseView();
        form.View.SetDetached(false);
        AddTab(form.View);
        if (closeForm)
            form.Close();
        Activate();
    }

    internal void CycleTab(int delta)
    {
        if (_tabs.TabCount < 2)
            return;
        if (IsSplit)
            LeaveSplit();
        _tabs.SelectedIndex = (_tabs.SelectedIndex + delta + _tabs.TabCount) % _tabs.TabCount;
    }

    private void ShowHintIfEmpty()
    {
        bool empty = _tabs.TabCount == 0;
        _tabs.Visible = !empty;
        _emptyHint.Visible = empty;
    }

    private void LaunchMstsc(RdpConnection c)
    {
        try { MstscLauncher.Launch(_doc.WithResolvedCredentials(c)); }
        catch (Exception ex) { MessageBox.Show(this, "Impossibile avviare mstsc.exe: " + ex.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Salva l'immagine della sessione in Immagini\RdpManager e la copia negli appunti.</summary>
    private async Task SaveSnapshotAsync(RdpSessionView view)
    {
        HideFullScreenBarOverlay();
        await Task.Delay(250); // il menu o la barra appena chiusi non devono finire nella foto
        using var bmp = view.CaptureSession();
        if (bmp is null)
        {
            ShowNotice("Foto non riuscita: la sessione non è visibile.");
            return;
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RdpManager");
        var safe = string.Concat(view.Connection.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        try
        {
            Directory.CreateDirectory(dir);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            try { Clipboard.SetImage(bmp); } catch (System.Runtime.InteropServices.ExternalException) { }
            ShowNotice($"Foto salvata in {path} e copiata negli appunti");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "Foto non salvata: " + ex.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------- riapertura all'avvio ----------

    private void ReopenSessions()
    {
        if (_locked)
        {
            _reopenAfterUnlock = true; // dopo lo sblocco
            return;
        }
        bool afterUpdate = _doc.Settings.ReopenOnceAfterUpdate;
        var ids = _doc.Settings.TakeSessionsToReopen();
        if (afterUpdate)
        {
            SaveConnections(); // il segnale «dopo l'aggiornamento» vale una volta sola
            AppLog.Default?.Info($"Riavvio dopo l'aggiornamento a {AppInfo.VersionText}: riapro {ids.Count} sessioni");
            if (ids.Count > 0)
                ShowNotice($"RdpManager aggiornato alla {AppInfo.VersionText}: riapro le sessioni di prima.");
        }
        foreach (var id in ids)
            if (_doc.Find(id) is { } c)
                OpenSession(c);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (HideToTrayInsteadOfClosing(e))
            return;
        var views = AllViews.ToList();
        int connected = views.Count(v => v.State == SessionState.Connected);
        // Con «Aggiorna ora» la conferma è già stata chiesta (e le sessioni si riaprono dopo).
        if (connected > 0 && e.CloseReason == CloseReason.UserClosing && !_closingForUpdate &&
            MessageBox.Show(this, $"Ci sono {connected} sessioni connesse. Disconnetterle e uscire?", "Esci",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        Overview?.Close();
        foreach (var v in views)
            EndHistory(v, "Uscita da RdpManager");
        // Si ricordano le sessioni aperte (nell'ordine delle schede) per riaprirle al prossimo avvio.
        var open = _doc.Settings.ReopenSessionsOnStartup || _closingForUpdate ? views.Select(v => v.Connection.Id).ToList() : [];
        if (_closingForUpdate && open.Count > 0 && !_doc.Settings.ReopenOnceAfterUpdate)
        {
            _doc.Settings.ReopenOnceAfterUpdate = true;
            _doc.Settings.OpenSessions = [];   // forza il salvataggio qui sotto
        }
        if (!open.SequenceEqual(_doc.Settings.OpenSessions))
        {
            _doc.Settings.OpenSessions = open;
            SaveConnections();
        }
        UnregisterSessionHotkeys();
        _edgeTimer.Stop();
        _reachTimer.Stop();
        foreach (var form in _detached.ToList())
        {
            _detached.Remove(form);
            form.ReleaseView();
            form.Close();
        }
        foreach (var v in views)
            v.Dispose();
    }

    // ---------- scorciatoie e hook di tastiera ----------

    internal enum ShortcutRoute { Handled, ToRemote, Normal }

    /// <summary>Decide chi gestisce una combinazione di tasti diretta alla finestra <paramref name="target"/>.</summary>
    internal ShortcutRoute RouteShortcut(Keys keyData, IntPtr target)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
                CycleTab(1);
                return ShortcutRoute.Handled;
            case Keys.Control | Keys.Shift | Keys.Tab:
                CycleTab(-1);
                return ShortcutRoute.Handled;
        }
        // Le scorciatoie configurabili valgono ovunque (senza hook, es. se Windows non lo concede).
        if (_shortcuts.TryGetAction(keyData, out var action))
        {
            ExecuteShortcut(action);
            return ShortcutRoute.Handled;
        }
        // Con il fuoco dentro una sessione remota le altre combinazioni (Ctrl+N, F5, …) vanno al PC remoto.
        return Control.FromChildHandle(target) is RdpClientHost ? ShortcutRoute.ToRemote : ShortcutRoute.Normal;
    }

    /// <summary>
    /// Filtro dei tasti premuti nei controlli di questa finestra, prima della gestione normale di WinForms
    /// (scorciatoie dei menu comprese): Ctrl+Tab e le scorciatoie configurabili si eseguono qui; con il fuoco
    /// nella sessione remota le altre combinazioni vanno dritte al controllo RDP, senza far scattare i menu.
    /// </summary>
    private sealed class ShortcutFilter(MainForm form) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN) || form.IsDisposed)
                return false;
            if (Control.FromChildHandle(m.HWnd)?.FindForm() != form)
                return false;
            var keyData = (Keys)(int)m.WParam & Keys.KeyCode | Control.ModifierKeys;
            switch (form.RouteShortcut(keyData, m.HWnd))
            {
                case ShortcutRoute.Handled:
                    return true;
                case ShortcutRoute.ToRemote:
                    var msg = new NativeMethods.MSG { hwnd = m.HWnd, message = m.Msg, wParam = m.WParam, lParam = m.LParam };
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                    return true;
                default:
                    return false;
            }
        }
    }

    private ShortcutFilter? _shortcutFilter;

    private void InstallShortcutFilter()
    {
        _shortcutFilter = new ShortcutFilter(this);
        Application.AddMessageFilter(_shortcutFilter);
        FormClosed += (_, _) => Application.RemoveMessageFilter(_shortcutFilter);
        Disposed += (_, _) => Application.RemoveMessageFilter(_shortcutFilter);
    }

    /// <summary>Quante scorciatoie sono state eseguite (da tastiera o dai menu), per le prove automatiche.</summary>
    internal int ShortcutsExecuted { get; private set; }

    internal void ExecuteShortcut(ShortcutAction action)
    {
        if (_locked)
            return;
        ShortcutsExecuted++;
        if (action != ShortcutAction.Overview)
            Overview?.Close();
        switch (action)
        {
            case ShortcutAction.ToggleFullScreen:
                ToggleFullScreen();
                return;
            case ShortcutAction.ToggleSidebar:
                ToggleSidebar();
                return;
            case ShortcutAction.Overview:
                ToggleOverview();
                return;
            case ShortcutAction.QuickSearch:
                ShowQuickSearch();
                return;
            case ShortcutAction.ToggleSplitView:
                ToggleSplitView();
                return;
            case ShortcutAction.Lock:
                LockApp();
                return;
            case ShortcutAction.NextSession or ShortcutAction.PreviousSession when IsSplit:
                // Nella vista affiancata si passa da una metà all'altra.
                var other = CurrentView == _splitLeft ? _splitRight : _splitLeft;
                if (other is not null)
                    BeginInvoke(other.FocusSession);
                return;
        }
        var views = TabViews;
        var connected = views.Select(v => v.State == SessionState.Connected).ToList();
        int current = _tabs.SelectedIndex;
        if (SessionCycler.Target(action, connected, current) is int target)
        {
            SelectView(views[target]);
            if (_fullScreen)
                FlashFullScreenBar();
        }
        else if (_fullScreen)
        {
            FlashFullScreenBar(); // nessuna sessione connessa dove andare: la barra mostra perché
        }
    }

    // ---------- scorciatoie dentro la sessione (RegisterHotKey, nessun hook di tastiera) ----------

    /// <summary>Id delle scorciatoie registrate con RegisterHotKey: 0x100 + valore dell'azione.</summary>
    private const int SessionHotkeyBase = 0x100;
    private readonly List<int> _sessionHotkeys = [];
    private bool _sessionHotkeysSuspended;

    /// <summary>Vero se le scorciatoie delle sessioni sono registrate (app in primo piano).</summary>
    internal bool SessionHotkeysRegistered => _sessionHotkeys.Count > 0;

    /// <summary>
    /// Registra le scorciatoie configurabili con RegisterHotKey mentre l'app è in primo piano: così funzionano anche
    /// con il fuoco nella sessione remota (il controllo RDP ha KeyboardHookMode = 0), senza un hook di tastiera
    /// (WH_KEYBOARD_LL, tolto nella 1.3 perché alcuni antivirus lo segnalano). Si tolgono appena l'app perde il
    /// primo piano.
    /// </summary>
    private void RegisterSessionHotkeys()
    {
        UnregisterSessionHotkeys();
        if (_sessionHotkeysSuspended || !IsHandleCreated || IsDisposed)
            return;
        var failed = new List<string>();
        foreach (var (action, combo) in _shortcuts.All)
        {
            if (combo.IsEmpty)
                continue;
            var (mods, vk) = GlobalHotkey.ToNative(combo);
            int id = SessionHotkeyBase + (int)action;
            if (NativeMethods.RegisterHotKey(Handle, id, mods, vk))
                _sessionHotkeys.Add(id);
            else
                failed.Add(combo.ToString());
        }
        RegisterWorkspaceHotkeys(failed);
        if (failed.Count > 0 && !_hotkeyConflictShown)
        {
            _hotkeyConflictShown = true;
            ShowNotice("Scorciatoie già usate da un altro programma: " + string.Join(", ", failed.Distinct()));
        }
    }

    private bool _hotkeyConflictShown;

    private void UnregisterSessionHotkeys()
    {
        if (IsHandleCreated)
            foreach (var id in _sessionHotkeys)
                NativeMethods.UnregisterHotKey(Handle, id);
        _sessionHotkeys.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        // Le scorciatoie registrate esistono solo mentre l'app è in primo piano.
        if (m.Msg == NativeMethods.WM_ACTIVATEAPP)
        {
            if (m.WParam != IntPtr.Zero)
                RegisterSessionHotkeys();
            else
                UnregisterSessionHotkeys();
        }
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = (int)m.WParam;
            // Scorciatoia globale (RegisterHotKey): riporta l'app in primo piano da qualunque programma.
            if (id == HotkeyId)
            {
                RestoreFromTray();
                return;
            }
            if (HandleWorkspaceHotkey(id))
                return;
            if (id >= SessionHotkeyBase && Enum.IsDefined((ShortcutAction)(id - SessionHotkeyBase)))
            {
                // Con un dialogo aperto la finestra principale è disabilitata: niente cambi di sessione alle sue spalle.
                if (NativeMethods.IsWindowEnabled(Handle))
                    ExecuteShortcut((ShortcutAction)(id - SessionHotkeyBase));
                return;
            }
        }
        base.WndProc(ref m);
    }

    // ---------- schermo intero dell'app ----------

    internal bool IsFullScreen => _fullScreen;

    /// <summary>
    /// Schermo intero dell'app: la finestra principale diventa senza bordi e copre il monitor scelto
    /// (tutti i monitor se la sessione corrente lo chiede). Le schede restano, senza intestazioni. In alto non
    /// c'è niente (dalla 1.5): sessioni, connessioni e comandi stanno nella barra a schermo intero, a sinistra,
    /// a destra o in basso, fissa, a comparsa o nascosta come scelto nelle impostazioni.
    /// </summary>
    internal void ToggleFullScreen()
    {
        if (_fullScreen)
            LeaveFullScreen();
        else
            EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_fullScreen)
            return;
        _savedBorder = FormBorderStyle;
        _savedState = WindowState;
        _savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var display = _tabs.DisplayRectangle;
        _tabInsets = new Padding(display.Left, display.Top, _tabs.Width - display.Right, _tabs.Height - display.Bottom);

        _fullScreen = true;
        _fsBarHiddenByUser = false;
        _fsBarRevealed = false;
        _fsEdgeSince = null;
        SuspendLayout();
        _menu.Visible = false;
        _statusStrip.Visible = false;
        foreach (var v in AllViews.Where(v => !v.IsDetached))
            v.ToolbarVisible = false;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        UpdateFullScreenBounds();
        ApplySidebarLayout();
        ResumeLayout();
        LayoutOverlays();
        _fullScreenItem.Checked = true;
        Activate();
        FlashFullScreenBar();
        if (CurrentView is { } cv)
            BeginInvoke(cv.FocusSession);
    }

    private void LeaveFullScreen()
    {
        if (!_fullScreen)
            return;
        _fullScreen = false;
        SuspendLayout();
        _fsBarRevealed = false;
        _fsBar.Visible = false;
        _thumbTimer.Stop();
        _content.Dock = DockStyle.Fill;
        _tabs.Dock = DockStyle.Fill;
        FormBorderStyle = _savedBorder;
        Bounds = MonitorLayout.EnsureVisible(_savedBounds, Screens.All(), _doc.Settings.PreferredMonitor);
        WindowState = _savedState == FormWindowState.Minimized ? FormWindowState.Normal : _savedState;
        _menu.Visible = true;
        _statusStrip.Visible = true;
        foreach (var v in AllViews)
            v.ToolbarVisible = true;
        ApplySidebarLayout();
        ResumeLayout();
        LayoutOverlays();
        _fullScreenItem.Checked = false;
    }

    /// <summary>Monitor dello schermo intero: tutti se la sessione corrente usa tutti i monitor, altrimenti quello scelto.</summary>
    internal Rectangle FullScreenTarget()
    {
        var all = Screens.All();
        if (CurrentView?.Connection.UseAllMonitors == true && all.Count > 1)
            return MonitorLayout.VirtualBounds(all);
        return (MonitorLayout.ChooseScreen(all, _doc.Settings.PreferredMonitor) ?? all[0]).Bounds;
    }

    private void UpdateFullScreenBounds()
    {
        if (!_fullScreen)
            return;
        var target = FullScreenTarget();
        if (Bounds != target)
            Bounds = target;
    }

    /// <summary>
    /// Posizione degli elementi: a schermo intero sessione e barra secondo <see cref="FullScreenBarLayout"/> (con la
    /// barra fissa la sessione prende solo lo spazio che resta), schede senza intestazioni; in finestra la barra
    /// laterale a comparsa sopra le sessioni.
    /// </summary>
    private void LayoutOverlays()
    {
        if (_fullScreen)
        {
            bool shown = FullScreenBarShown;
            var r = FullScreenBarLayout.Compute(_root.ClientRectangle, _doc.Settings.Bar, shown);
            if (_content.Dock != DockStyle.None)
                _content.Dock = DockStyle.None;
            if (_content.Bounds != r.Session)
                _content.Bounds = r.Session;
            if (shown)
            {
                _fsBar.Bounds = r.Bar;
                _fsBar.BringToFront();
            }
            if (_fsBar.Visible != shown)
                _fsBar.Visible = shown;
            // Le intestazioni delle schede finiscono fuori dall'area visibile: resta solo la sessione.
            _tabs.Dock = DockStyle.None;
            _tabs.Bounds = new Rectangle(-_tabInsets.Left, -_tabInsets.Top,
                _content.Width + _tabInsets.Horizontal, _content.Height + _tabInsets.Vertical);
            return;
        }
        if (_tabs.Dock != DockStyle.Fill)
            _tabs.Dock = DockStyle.Fill;
        if (_doc.Settings.SidebarMode == SidebarMode.AutoHide && _sidebarRevealed)
        {
            _sidebar.Bounds = new Rectangle(_edge.Visible ? _edge.Width : 0, 0, _sidebar.Width, _root.Height);
            _sidebar.BringToFront();
        }
    }

    // ---------- barra a schermo intero ----------

    /// <summary>Vero se a schermo intero la barra si vede (fissa non nascosta a mano, oppure aperta sopra la sessione).</summary>
    internal bool FullScreenBarShown =>
        _fullScreen && (_fsBarRevealed || (_doc.Settings.Bar.Mode == BarMode.Fixed && !_fsBarHiddenByUser));

    internal FullScreenSideBar FullScreenBarControl => _fsBar;

    private List<FullScreenSideBar.SessionItem> BarSessions()
    {
        var views = TabViews;
        var connected = views.Select(v => v.State == SessionState.Connected).ToList();
        int selected = _tabs.SelectedIndex;
        var current = CurrentView;
        return views.Select((v, i) =>
        {
            int number = connected.Take(i + 1).Count(x => x);
            return new FullScreenSideBar.SessionItem(v, connected[i] && number <= 9 ? number : null,
                IsSplit ? v == current : i == selected);
        }).ToList();
    }

    private bool CanTypeQuickText => CurrentView is { State: SessionState.Connected };

    /// <summary>Ricostruisce la barra (sezioni, connessioni, spazi di lavoro…) se si vede.</summary>
    private void RebuildFullScreenBar()
    {
        if (!_fullScreen || !_fsBar.Visible)
            return;
        _fsBar.Rebuild(new FullScreenSideBar.Content(
            _doc.Settings.Bar,
            BarSessions(),
            _doc.InDisplayOrder(_doc.Connections.Where(c => c.IsFavorite)).ToList(),
            _doc.InDisplayOrder(_doc.Connections).ToList(),
            _doc.Workspaces,
            _doc.QuickTexts,
            _shortcuts,
            CanTypeQuickText,
            Screen.AllScreens.Length > 1));
        bool thumbs = _doc.Settings.Bar.SessionStyle == SessionListStyle.Thumbnails
                      && _doc.Settings.Bar.VisibleSections.Contains(BarSection.ConnectedSessions);
        if (thumbs)
        {
            _thumbTimer.Start();
            RefreshBarThumbnails();
        }
        else
        {
            _thumbTimer.Stop();
        }
    }

    /// <summary>Per le prove automatiche: se impostata, riceve le azioni sulla sessione invece di eseguirle.</summary>
    internal Action<SessionBarAction>? SessionActionForTests { get; set; }

    /// <summary>Azioni sulla sessione della barra (le funzioni della 1.1 della barra in alto), sulla sessione attiva.</summary>
    internal void ExecuteSessionAction(SessionBarAction action)
    {
        if (_locked)
            return;
        if (SessionActionForTests is { } test)
        {
            test(action);
            return;
        }
        switch (action)
        {
            case SessionBarAction.SendCtrlAltDel:
                CurrentView?.SendCtrlAltDel();
                break;
            case SessionBarAction.Snapshot when CurrentView is { } v:
                _ = SaveSnapshotAsync(v);
                break;
            case SessionBarAction.NextMonitor:
                MoveToNextMonitor();
                break;
        }
    }

    /// <summary>Aggiorna solo le sessioni della barra (stato, numeri, scheda scelta).</summary>
    private void UpdateFullScreenBarSessions()
    {
        if (_fullScreen && _fsBar.Visible)
            _fsBar.UpdateSessions(BarSessions(), CanTypeQuickText);
    }

    /// <summary>
    /// Miniature: si fotografa (PrintWindow) solo la sessione visibile, così non si cambia scheda alle spalle
    /// dell'utente; le altre tengono l'ultima immagine di quando erano visibili.
    /// </summary>
    private void RefreshBarThumbnails()
    {
        if (!_fullScreen || !_fsBar.Visible || _locked)
        {
            _thumbTimer.Stop();
            return;
        }
        var visible = IsSplit ? new[] { _splitLeft, _splitRight } : new[] { _tabs.SelectedTab?.Tag as RdpSessionView };
        foreach (var v in visible.OfType<RdpSessionView>().Where(v => v.State == SessionState.Connected))
        {
            using var full = v.CaptureSession();
            if (full is null)
                continue;
            int w = Math.Min(full.Width, 480);
            int h = Math.Max(1, full.Height * w / Math.Max(1, full.Width));
            var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, w, h);
            }
            _fsBar.UpdateThumbnail(v, small);
        }
    }

    /// <summary>Scorciatoia della barra laterale a schermo intero: fissa → la nasconde o la rimostra; altrimenti la apre o la chiude.</summary>
    private void ToggleFullScreenBar()
    {
        if (_doc.Settings.Bar.Mode == BarMode.Fixed && !_fsBarRevealed)
        {
            _fsBarHiddenByUser = !_fsBarHiddenByUser;
            LayoutOverlays();
            RebuildFullScreenBar();
        }
        else if (_fsBarRevealed)
        {
            HideFullScreenBarOverlay();
        }
        else
        {
            RevealFullScreenBar();
            if (_fsBar.SearchBox is { } box)
            {
                if (Form.ActiveForm != this)
                    Activate();
                box.Focus();
            }
            return;
        }
        if (CurrentView is { } v)
            BeginInvoke(v.FocusSession);
    }

    /// <summary>Apre la barra sopra la sessione (a comparsa o nascosta).</summary>
    internal void RevealFullScreenBar()
    {
        if (!_fullScreen || FullScreenBarShown)
            return;
        _fsBarRevealed = true;
        _fsBarAwayTicks = 0;
        LayoutOverlays();
        RebuildFullScreenBar();
    }

    private void HideFullScreenBarOverlay()
    {
        _fsEdgeSince = null;
        if (!_fsBarRevealed)
            return;
        _fsBarRevealed = false;
        _fsBarShownUntil = DateTime.MinValue;
        LayoutOverlays();
    }

    /// <summary>Barra a comparsa: si apre per un attimo, come conferma dopo un cambio di sessione da tastiera.</summary>
    private void FlashFullScreenBar()
    {
        if (!_fullScreen)
            return;
        if (FullScreenBarShown)
        {
            if (_fsBar.Visible && _fsBar.ShownSections.Count == 0)
                RebuildFullScreenBar();
            else
                UpdateFullScreenBarSessions();
        }
        if (_doc.Settings.Bar.Mode != BarMode.AutoReveal || _fsBarRevealed)
        {
            if (FullScreenBarShown && _fsBar.ShownSections.Count == 0)
                RebuildFullScreenBar();
            return;
        }
        RevealFullScreenBar();
        _fsBarShownUntil = DateTime.UtcNow.AddSeconds(1.5);
    }

    /// <summary>
    /// Il mouse sopra il controllo RDP non genera eventi per WinForms: la posizione si controlla a intervalli.
    /// A schermo intero: bordo della barra a comparsa (per il ritardo scelto) → si apre; fuori dalla barra → si
    /// richiude. Il bordo in alto non fa niente. In finestra: fuori dalla barra laterale a comparsa → si richiude.
    /// </summary>
    private void OnEdgeTimer()
    {
        if (IsDisposed || WindowState == FormWindowState.Minimized)
            return;
        // Metà della vista affiancata usata per ultima: il clic dentro il controllo RDP non arriva a WinForms.
        if (_splitLeft is { } l && _splitRight is { } r)
            _splitActive = r.ContainsFocus ? r : l.ContainsFocus ? l : _splitActive;
        var p = Cursor.Position;
        bool active = Form.ActiveForm == this;

        if (_fullScreen)
        {
            var bar = _doc.Settings.Bar;
            if (_fsBarRevealed)
            {
                var area = _fsBar.RectangleToScreen(_fsBar.ClientRectangle);
                area.Inflate(16, 16);
                bool inside = area.Contains(p) || _fsBar.IsSearching || _fsBar.ContainsFocus && Form.ActiveForm == this
                              || DateTime.UtcNow < _fsBarShownUntil;
                _fsBarAwayTicks = inside ? 0 : _fsBarAwayTicks + 1;
                if (_fsBarAwayTicks >= 5)
                    HideFullScreenBarOverlay();
            }
            else if (bar.Mode == BarMode.AutoReveal && !_locked)
            {
                var screen = _root.RectangleToScreen(_root.ClientRectangle);
                if (active && FullScreenBarLayout.IsOnRevealEdge(p, screen, bar.Position))
                {
                    _fsEdgeSince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - _fsEdgeSince.Value).TotalMilliseconds >= bar.RevealDelayMs)
                        RevealFullScreenBar();
                }
                else
                {
                    _fsEdgeSince = null;
                }
            }
            return;
        }

        if (_doc.Settings.SidebarMode == SidebarMode.AutoHide && _sidebarRevealed)
        {
            var area = _sidebar.RectangleToScreen(_sidebar.ClientRectangle);
            area.Inflate(12, 0);
            bool menuOpen = _treeMenu.Visible;
            bool inside = area.Contains(p) || menuOpen || (_sidebar.ContainsFocus && _search.Focused && _search.TextLength > 0);
            _sidebarAwayTicks = inside ? 0 : _sidebarAwayTicks + 1;
            if (_sidebarAwayTicks >= 5)
                HideRevealedSidebar();
        }
    }

    // ---------- barra laterale ----------

    internal bool SidebarShown => _sidebar.Visible;

    private void SetSidebarMode(SidebarMode mode)
    {
        _doc.Settings.SidebarMode = mode;
        _sidebarHiddenByUser = false;
        _sidebarRevealed = false;
        SaveConnections();
        ApplySidebarLayout();
    }

    private void ApplySidebarLayout()
    {
        bool pinned = _doc.Settings.SidebarMode == SidebarMode.Pinned;
        SuspendLayout();
        if (_fullScreen)
        {
            // A schermo intero vale la barra a schermo intero: la barra laterale della finestra si toglie.
            _edge.Visible = false;
            _splitter.Visible = false;
            _sidebar.Visible = false;
            _sidebarRevealed = false;
        }
        else if (pinned)
        {
            _edge.Visible = false;
            _sidebar.Dock = DockStyle.Left;
            _sidebar.Visible = !_sidebarHiddenByUser;
            _splitter.Visible = _sidebar.Visible;
            // Ordine nell'elenco dei controlli = ordine di aggancio (vedi BuildLayout).
            _root.Controls.SetChildIndex(_content, 0);
            _root.Controls.SetChildIndex(_splitter, 1);
            _root.Controls.SetChildIndex(_sidebar, 2);
            _root.Controls.SetChildIndex(_edge, 3);
            _sidebar.BorderStyle = BorderStyle.None;
        }
        else
        {
            _splitter.Visible = false;
            _edge.Visible = true;
            _sidebar.Dock = DockStyle.None;
            _sidebar.BorderStyle = BorderStyle.FixedSingle;
            _sidebar.Visible = _sidebarRevealed;
        }
        ResumeLayout();
        LayoutOverlays();
        RebuildFullScreenBar();
        _pinButton.Text = pinned ? "A comparsa" : "Fissa";
        _pinButton.ToolTipText = pinned
            ? "Barra a comparsa: si nasconde e riappare portando il mouse sul bordo sinistro"
            : "Fissa la barra laterale accanto alle sessioni";
        _pinItem.Checked = pinned;
    }

    /// <summary>Ctrl+Alt+B: barra fissa → la nasconde o la rimostra; barra a comparsa → la apre o la chiude.</summary>
    internal void ToggleSidebar()
    {
        if (_fullScreen)
        {
            ToggleFullScreenBar();
            return;
        }
        if (SidebarShown)
        {
            if (_doc.Settings.SidebarMode == SidebarMode.Pinned)
            {
                _sidebarHiddenByUser = true;
                ApplySidebarLayout();
            }
            else
            {
                HideRevealedSidebar();
            }
            if (CurrentView is { } v)
                BeginInvoke(v.FocusSession);
        }
        else
        {
            ShowSidebar(focusSearch: true);
        }
    }

    private void ShowSidebar(bool focusSearch)
    {
        if (_fullScreen)
        {
            RevealFullScreenBar();
            return;
        }
        if (_doc.Settings.SidebarMode == SidebarMode.Pinned)
        {
            _sidebarHiddenByUser = false;
            ApplySidebarLayout();
        }
        else if (!_sidebarRevealed)
        {
            _sidebarRevealed = true;
            _sidebarAwayTicks = 0;
            _sidebar.Visible = true;
            LayoutOverlays();
        }
        if (focusSearch)
        {
            if (Form.ActiveForm != this)
                Activate();
            _tree.Focus();
        }
    }

    private void HideRevealedSidebar()
    {
        if (!_sidebarRevealed)
            return;
        _sidebarRevealed = false;
        _sidebar.Visible = false;
    }

    // ---------- monitor ----------

    private void FillMonitorMenu()
    {
        _monitorMenu.DropDownItems.Clear();
        var all = Screens.All();
        var chosen = MonitorLayout.ChooseScreen(all, _doc.Settings.PreferredMonitor);
        foreach (var s in MonitorLayout.Ordered(all))
        {
            var item = new ToolStripMenuItem(Screens.Describe(s, all)) { Checked = s.DeviceName == chosen?.DeviceName };
            item.Click += (_, _) => SetPreferredMonitor(s);
            _monitorMenu.DropDownItems.Add(item);
        }
    }

    private void SetPreferredMonitor(ScreenInfo screen)
    {
        _doc.Settings.PreferredMonitor = screen.DeviceName;
        SaveConnections();
        if (_fullScreen)
            UpdateFullScreenBounds();
        else
            Screens.MoveForm(this, screen);
    }

    /// <summary>Finestra (o schermo intero) sul monitor successivo; il monitor scelto viene ricordato.</summary>
    private void MoveToNextMonitor()
    {
        var all = Screens.All();
        if (all.Count < 2)
        {
            ShowNotice("C'è un solo monitor.");
            return;
        }
        var current = MonitorLayout.ScreenOf(all, Bounds);
        if (MonitorLayout.NextScreen(all, current) is { } next)
            SetPreferredMonitor(next);
        UpdateFullScreenBarSessions();
    }

    /// <summary>All'avvio la finestra si apre sul monitor scelto (se è collegato).</summary>
    private void PlaceOnPreferredMonitor()
    {
        if (_doc.Settings.PreferredMonitor is null)
            return;
        var all = Screens.All();
        var target = MonitorLayout.ChooseScreen(all, _doc.Settings.PreferredMonitor);
        if (target is not null && MonitorLayout.ScreenOf(all, Bounds)?.DeviceName != target.DeviceName)
            Bounds = MonitorLayout.Center(Size, target.WorkingArea);
    }

    // ---------- accesso per le prove automatiche ----------

    /// <summary>Chiamata su ogni nuova sessione prima della connessione (le prove la usano per impostare opzioni).</summary>
    internal Action<RdpSessionView>? ConfigureNewSession { get; set; }
    internal TabControl Tabs => _tabs;
    internal TreeView Tree => _tree;
    internal TextBox SearchBox => _search;
    internal ConnectionsDocument Document => _doc;
    internal IReadOnlyList<DetachedSessionForm> DetachedForms => _detached;
    internal Control Sidebar => _sidebar;

    internal void SetSidebarModeForTests(SidebarMode mode) => SetSidebarMode(mode);

    /// <summary>Come dopo «OK» nelle impostazioni, con solo la barra a schermo intero cambiata.</summary>
    internal void ApplyFullScreenBarForTests(FullScreenBarSettings bar)
    {
        bar.Normalize();
        _doc.Settings.FullScreenBar = bar;
        _fsBarHiddenByUser = false;
        _fsBarRevealed = false;
        ApplySettings();
    }

    internal void HideFullScreenBarOverlayForTests() => HideFullScreenBarOverlay();
    internal void RefreshTreeForTests() => RefreshTree();
    internal void ShowSidebarForTests() => ShowSidebar(focusSearch: false);
}
