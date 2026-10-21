using System.ComponentModel;
using System.Diagnostics;
using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.UI;

// Funzioni 1.2: Panoramica, ricerca rapida, vista affiancata, area di notifica e scorciatoia globale, tema,
// testi rapidi, profili di credenziali, cronologia, aggiornamento con un clic, importazione da mstsc.
public sealed partial class MainForm
{
    private readonly bool _systemIntegration;

    private void InitV12()
    {
        InstallShortcutFilter();
        InitHistory();
        _split.SplitterMoved += (_, _) => OnSplitterMoved();
        _split.Panel1.Padding = _split.Panel2.Padding = new Padding(0);
        _content.Controls.Add(_split);
        _overviewTimer.Tick += async (_, _) => await RefreshOverviewAsync();
        _trayMenu.Opening += (_, e) => { FillTrayMenu(); e.Cancel = false; };
        FormClosed += OnFormClosedV12;
        HandleCreated += (_, _) => RegisterGlobalHotkey();
        HandleDestroyed += (_, _) => { UnregisterGlobalHotkey(); UnregisterSessionHotkeys(); };
        ApplyTheme();
        ApplySystemIntegration();
    }

    // ---------- tema ----------

    private void ApplyTheme()
    {
        ThemeManager.SetTheme(_doc.Settings.Theme);
        ThemeManager.Exclude(_switchBar);
        ThemeManager.Apply(this, _treeMenu, _tabMenu, _trayMenu);
        foreach (var d in _detached)
            ThemeManager.Apply(d);
        _split.BackColor = ThemeManager.Current.Border;
        foreach (var v in AllViews)
            v.RefreshStateColors();
        _tree.Invalidate();
    }

    internal void ApplyThemeForTests() => ApplyTheme();

    // ---------- Panoramica ----------

    private OverviewForm? _overview;
    private readonly System.Windows.Forms.Timer _overviewTimer = new() { Interval = 700 };
    private int _overviewRound;
    private bool _overviewCapturing;

    /// <summary>Vero mentre la panoramica seleziona di nascosto una scheda per fotografarla: niente effetti collaterali.</summary>
    private bool _silentTabSwitch;

    /// <summary>Scheda da rimettere se la panoramica si chiude mentre ne sta fotografando un'altra.</summary>
    private TabPage? _captureRestoreTab;

    /// <summary>Vero mentre la panoramica aggiorna le miniature (per le prove automatiche).</summary>
    internal bool OverviewBusy => _overviewCapturing;

    internal OverviewForm? Overview => _overview is { IsDisposed: false } o ? o : null;

    internal void ToggleOverview()
    {
        if (Overview is { } open)
        {
            open.Close();
            return;
        }
        ShowOverview();
    }

    private void ShowOverview()
    {
        HideSwitchBar();
        var views = AllViews.Where(v => v.State == SessionState.Connected).ToList();
        var form = new OverviewForm();
        form.SetSessions(views, CurrentView);
        form.Bounds = Bounds;
        form.Icon = Icon;
        form.SessionChosen += (_, v) => SelectView(v);
        form.FormClosed += (_, _) =>
        {
            _overviewTimer.Stop();
            if (ReferenceEquals(_overview, form))
                _overview = null;
            // Chiusa durante una cattura: la scheda scelta torna subito quella di prima (poi, se l'utente ha
            // scelto una miniatura, SessionChosen porta lì).
            if (_captureRestoreTab is not null || _captureHidSplit)
                RestoreAfterCapture(null);
            BeginInvoke(form.Dispose);
            if (CurrentView is { } cv && Form.ActiveForm is null or MainForm)
                BeginInvoke(cv.FocusSession);
        };
        _overview = form;
        form.Show(this);
        _overviewRound = 0;
        _overviewTimer.Start();
        _ = RefreshOverviewAsync(all: true);
    }

    /// <summary>
    /// Aggiorna le miniature: quella della sessione visibile a ogni giro, le altre una alla volta a rotazione
    /// (la scheda si seleziona per un attimo sotto la panoramica, che la copre). Con <paramref name="all"/> tutte subito.
    /// </summary>
    private async Task RefreshOverviewAsync(bool all = false)
    {
        if (Overview is not { } overview)
            return;
        if (_overviewCapturing)
        {
            // Panoramica (ri)aperta durante un giro: appena finisce, uno completo per quella nuova.
            _overviewRefreshAllPending |= all;
            return;
        }
        _overviewCapturing = true;
        try
        {
            var views = overview.Tiles.Select(t => t.View).Where(v => !v.IsDisposed).ToList();
            var current = CurrentView;
            if (current is not null && views.Contains(current))
                overview.UpdateThumbnail(current, CaptureForOverview(overview, current));
            var others = views.Where(v => v != current).ToList();
            if (others.Count == 0)
                return;
            var batch = all ? others : [others[_overviewRound++ % others.Count]];
            foreach (var v in batch)
            {
                if (!ReferenceEquals(Overview, overview))
                    return;
                var bmp = await CaptureHiddenAsync(overview, v);
                if (ReferenceEquals(Overview, overview))
                    overview.UpdateThumbnail(v, bmp);
                else
                    bmp?.Dispose();
            }
        }
        finally
        {
            _overviewCapturing = false;
            if (_overviewRefreshAllPending && Overview is not null)
            {
                _overviewRefreshAllPending = false;
                BeginInvoke(() => _ = RefreshOverviewAsync(all: true));
            }
        }
    }

    private bool _overviewRefreshAllPending;

    /// <summary>Copia dello schermo se la panoramica ne è esclusa (la più fedele), altrimenti PrintWindow.</summary>
    private static Bitmap? CaptureForOverview(OverviewForm overview, RdpSessionView view) =>
        overview.ExcludedFromCapture ? view.CaptureFromScreen() : view.CaptureSession();

    private async Task<Bitmap?> CaptureHiddenAsync(OverviewForm overview, RdpSessionView view)
    {
        // Finestra staccata: può stare ovunque, anche sotto altre app; PrintWindow non dipende da cosa la copre.
        if (view.IsDetached)
            return view.CaptureSession();
        if (IsSplitView(view))
            return CaptureForOverview(overview, view);
        if (PageOf(view) is not { } page)
            return null;
        var previous = _tabs.SelectedTab;
        _captureRestoreTab = previous;
        _captureHidSplit = IsSplit;
        _silentTabSwitch = true;
        try
        {
            _tabs.SelectedTab = page;
            if (_captureHidSplit)
                _split.Visible = false; // sotto la panoramica: la scheda fotografata deve essere quella sullo schermo
        }
        finally
        {
            _silentTabSwitch = false;
        }
        await Task.Delay(150); // il controllo RDP ridisegna la sessione appena torna visibile
        // Panoramica chiusa nel frattempo: la scheda è già stata rimessa (o l'utente ne ha scelta un'altra).
        if (!ReferenceEquals(Overview, overview) || _captureRestoreTab is null)
            return null;
        var bmp = view.IsDisposed ? null : CaptureForOverview(overview, view);
        RestoreAfterCapture(page);
        return bmp;
    }

    private bool _captureHidSplit;

    /// <summary>Rimette la scheda scelta (se nel frattempo non è cambiata) e la vista affiancata nascosta per la cattura.</summary>
    private void RestoreAfterCapture(TabPage? captured)
    {
        var restore = _captureRestoreTab;
        _captureRestoreTab = null;
        _silentTabSwitch = true;
        try
        {
            if (restore is not null && _tabs.TabPages.Contains(restore) && (captured is null || _tabs.SelectedTab == captured))
                _tabs.SelectedTab = restore;
            if (_captureHidSplit && IsSplit)
            {
                _split.Visible = true;
                _split.BringToFront();
            }
            _captureHidSplit = false;
        }
        finally
        {
            _silentTabSwitch = false;
        }
    }

    // ---------- ricerca rapida ----------

    /// <summary>Chiamata quando la ricerca rapida è aperta (le prove automatiche la usano per scrivere e scegliere).</summary>
    internal Action<QuickSearchForm>? QuickSearchShown { get; set; }

    internal void ShowQuickSearch()
    {
        Overview?.Close();
        HideSwitchBar();
        var open = AllViews.Select(v => v.Connection.Id).ToList();
        using var dlg = new QuickSearchForm(_doc.Connections, open);
        dlg.PlaceOver(Bounds);
        ThemeManager.Apply(dlg);
        dlg.Shown += (_, _) => QuickSearchShown?.Invoke(dlg);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Chosen is { } chosen)
            OpenSession(chosen.Connection);
        else if (CurrentView is { } v)
            BeginInvoke(v.FocusSession);
    }

    // ---------- vista affiancata ----------

    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Visible = false, SplitterWidth = 6, FixedPanel = FixedPanel.None };
    private RdpSessionView? _splitLeft, _splitRight;

    /// <summary>Metà della vista affiancata usata per ultima (il fuoco può essere altrove, es. nella barra laterale).</summary>
    private RdpSessionView? _splitActive;

    internal bool IsSplit => _splitLeft is not null;
    internal (RdpSessionView? Left, RdpSessionView? Right) SplitViews => (_splitLeft, _splitRight);
    internal SplitContainer SplitControl => _split;

    private bool IsSplitView(RdpSessionView view) => view == _splitLeft || view == _splitRight;

    private TabPage? PageOf(RdpSessionView view) =>
        _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Tag == view);

    /// <summary>Ctrl+Alt+S: affianca la sessione corrente alla successiva (connessa, se c'è), oppure torna alle schede.</summary>
    internal void ToggleSplitView()
    {
        if (IsSplit)
        {
            LeaveSplit();
            return;
        }
        var views = TabViews;
        if (CurrentView is not { } left || views.Count < 2)
        {
            ShowNotice("Per la vista affiancata servono almeno due sessioni aperte nelle schede.");
            return;
        }
        var connected = views.Select(v => v.State == SessionState.Connected && v != left).ToList();
        int index = SessionCycler.Next(connected, views.IndexOf(left));
        var right = index >= 0 ? views[index] : views.First(v => v != left);
        EnterSplit(left, right);
    }

    internal void EnterSplit(RdpSessionView left, RdpSessionView right)
    {
        if (IsSplit)
            LeaveSplit();
        if (left == right || left.IsDetached || right.IsDetached || PageOf(left) is not { } leftPage || PageOf(right) is not { } rightPage)
            return;
        Overview?.Close();
        SuspendLayout();
        leftPage.Controls.Remove(left);
        rightPage.Controls.Remove(right);
        _split.Panel1.Controls.Add(left);
        _split.Panel2.Controls.Add(right);
        _splitLeft = left;
        _splitRight = right;
        _splitActive = left;
        left.Enter += OnSplitViewEnter;
        right.Enter += OnSplitViewEnter;
        left.ToolbarVisible = right.ToolbarVisible = true;
        _split.Visible = true;
        _split.BringToFront();
        if (_switchBar.Visible)
            _switchBar.BringToFront();
        ResumeLayout();
        _split.SplitterDistance = SplitLayout.SplitterDistance(_split.Width, _doc.Settings.SplitRatio, _split.SplitterWidth);
        _splitItem.Checked = true;
        UpdateStatus();
        BeginInvoke(left.FocusSession);
    }

    internal void LeaveSplit()
    {
        if (_splitLeft is not { } left || _splitRight is not { } right)
            return;
        var focused = right.ContainsFocus ? right : left.ContainsFocus ? left : _splitActive ?? left;
        left.Enter -= OnSplitViewEnter;
        right.Enter -= OnSplitViewEnter;
        SuspendLayout();
        _split.Visible = false;
        foreach (var v in new[] { left, right })
        {
            v.Parent?.Controls.Remove(v);
            if (PageOf(v) is { } page && !v.IsDisposed)
                page.Controls.Add(v);
            v.ToolbarVisible = !_fullScreen;
        }
        _splitLeft = _splitRight = _splitActive = null;
        ResumeLayout();
        _splitItem.Checked = false;
        if (PageOf(focused) is { } p)
            _tabs.SelectedTab = p;
        UpdateStatus();
        if (!focused.IsDisposed)
            BeginInvoke(focused.FocusSession);
    }

    private void OnSplitViewEnter(object? sender, EventArgs e)
    {
        if (sender is RdpSessionView v && IsSplitView(v))
            _splitActive = v;
    }

    private void OnSplitterMoved()
    {
        if (!IsSplit || _split.Width <= 0)
            return;
        var ratio = SplitLayout.RatioFromDistance(_split.SplitterDistance, _split.Width, _split.SplitterWidth);
        if (Math.Abs(ratio - _doc.Settings.SplitRatio) < 0.005)
            return;
        _doc.Settings.SplitRatio = ratio;
        SaveConnections();
    }

    private void FillSplitMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var current = CurrentView;
        foreach (var v in TabViews.Where(v => v != current))
        {
            var target = v;
            menu.DropDownItems.Add(v.Connection.DisplayName.Replace("&", "&&"), null, (_, _) =>
            {
                if (CurrentView is { } c && c != target)
                    EnterSplit(c, target);
            });
        }
        if (menu.DropDownItems.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("(serve un'altra sessione aperta)") { Enabled = false });
    }

    // ---------- testi rapidi ----------

    private void ManageQuickTexts()
    {
        using var dlg = new QuickTextsDialog(_doc);
        dlg.ShowDialog(this);
        if (dlg.Changed)
            SaveConnections();
    }

    private void ShowQuickTextsMenuAtCursor()
    {
        if (CurrentView is not { } v)
            return;
        var holder = new ToolStripMenuItem();
        v.FillQuickTextsMenu(holder);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(holder.DropDownItems.Cast<ToolStripItem>().ToArray());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    // ---------- profili di credenziali ----------

    private void ManageProfiles()
    {
        using var dlg = new CredentialProfilesDialog(_doc);
        dlg.ShowDialog(this);
        if (dlg.Changed)
        {
            SaveConnections();
            UpdateTreeStates();
        }
    }

    // ---------- cronologia ----------

    private HistoryStore _historyStore = null!;
    private ConnectionHistory _history = new();
    private readonly Dictionary<RdpSessionView, HistoryEntry> _openHistory = [];

    internal ConnectionHistory History => _history;

    private void InitHistory()
    {
        _historyStore = HistoryStore.Beside(_store.FilePath);
        _history = _historyStore.Load();
        if (_history.CloseInterrupted() > 0)
            SaveHistory();
    }

    private void SaveHistory()
    {
        try
        {
            _historyStore.Save(_history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice("Cronologia non salvata: " + ex.Message);
        }
    }

    /// <summary>Annota l'inizio (passaggio a «Connessa») e la fine (uscita da «Connessa») di una sessione.</summary>
    private void RecordHistory(RdpSessionView view, SessionState previous)
    {
        if (view.State == SessionState.Connected && previous != SessionState.Connected)
        {
            if (!_doc.Settings.HistoryEnabled)
                return;
            _openHistory[view] = _history.Start(view.Connection, DateTime.UtcNow);
            SaveHistory();
        }
        else if (previous == SessionState.Connected && view.State != SessionState.Connected)
        {
            // Il testo di stato ha già il motivo nella forma migliore (anche la descrizione del controllo RDP).
            const string prefix = "Disconnessa: ";
            var reason = view.StateText.StartsWith(prefix, StringComparison.Ordinal) ? view.StateText[prefix.Length..] : view.StateText;
            EndHistory(view, reason, view.State == SessionState.Disconnected ? view.LastDisconnectReason : null);
        }
    }

    private void EndHistory(RdpSessionView view, string reason, int? code = null)
    {
        if (!_openHistory.Remove(view, out var entry))
            return;
        _history.End(entry, DateTime.UtcNow, reason, code);
        SaveHistory();
    }

    private void ShowHistory(Guid? connectionId)
    {
        using var dlg = new HistoryDialog(_history, _doc, connectionId);
        dlg.ShowDialog(this);
        if (dlg.Changed)
        {
            // Le sessioni ancora aperte restano annotate anche se la loro riga è stata tolta.
            foreach (var (_, entry) in _openHistory)
                if (!_history.Entries.Contains(entry))
                    _history.Entries.Add(entry);
            SaveHistory();
        }
    }

    // ---------- area di notifica e scorciatoia globale ----------

    private NotifyIcon? _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    internal const int HotkeyId = 0x5244;
    private bool _hotkeyRegistered;
    private bool _exitRequested;
    private bool _trayHintShown;

    internal NotifyIcon? TrayIcon => _tray;
    internal ContextMenuStrip TrayMenu => _trayMenu;
    internal bool GlobalHotkeyRegistered => _hotkeyRegistered;

    private void ApplySystemIntegration()
    {
        if (!_systemIntegration)
            return;
        if (_doc.Settings.ShowTrayIcon && _tray is null)
        {
            _tray = new NotifyIcon { Icon = Icon, Text = "RdpManager", ContextMenuStrip = _trayMenu, Visible = true };
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        }
        else if (!_doc.Settings.ShowTrayIcon && _tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
            if (!Visible)
                RestoreFromTray();
        }
        RegisterGlobalHotkey();
    }

    private void RegisterGlobalHotkey()
    {
        UnregisterGlobalHotkey();
        if (!_systemIntegration || !IsHandleCreated)
            return;
        var combo = GlobalHotkey.FromSettings(_doc.Settings.GlobalHotkey);
        if (combo.IsEmpty)
            return;
        var (mods, vk) = GlobalHotkey.ToNative(combo);
        _hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, mods, vk);
        if (!_hotkeyRegistered)
            ShowNotice($"La scorciatoia globale {combo} è già usata da un altro programma: cambiala in File › Impostazioni.");
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_hotkeyRegistered)
            return;
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    /// <summary>Riporta la finestra principale in primo piano (da area di notifica o scorciatoia globale).</summary>
    internal void RestoreFromTray()
    {
        // Bloccata: va davanti la schermata del PIN, non la finestra.
        if (_locked)
        {
            ShowLockForm();
            return;
        }
        if (!Visible)
            Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        // Con un dialogo aperto (finestra principale disabilitata) va davanti il dialogo, non la sessione dietro.
        if (!Enabled && Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Modal && f.Visible) is { } modal)
        {
            WindowAttention.BringToFront(modal);
            return;
        }
        WindowAttention.BringToFront(this);
        if (CurrentView is { } v)
            BeginInvoke(v.FocusSession);
        else
            BeginInvoke(FocusSearch);
    }

    internal void FillTrayMenu()
    {
        foreach (var item in _trayMenu.Items.Cast<ToolStripItem>().ToList())
            item.Dispose();
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add(new ToolStripMenuItem("Mostra RdpManager", null, (_, _) => RestoreFromTray()) { Font = new Font(_trayMenu.Font, FontStyle.Bold) });
        _trayMenu.Items.Add("Ricerca rapida…", null, (_, _) =>
        {
            RestoreFromTray();
            if (Enabled)
                BeginInvoke(ShowQuickSearch);
        });
        _trayMenu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem ConnItem(RdpConnection c)
        {
            bool open = AllViews.Any(v => v.Connection == c);
            var item = new ToolStripMenuItem((c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "")).Replace("&", "&&"))
            {
                Checked = open, ToolTipText = RdpFile.FormatAddress(c.Host, c.Port),
            };
            item.Click += (_, _) =>
            {
                RestoreFromTray();
                if (Enabled && !_locked)
                    OpenSession(c);
            };
            return item;
        }

        var open = AllViews.Select(v => v.Connection).ToList();
        if (open.Count > 0)
        {
            var sessions = new ToolStripMenuItem("Sessioni aperte");
            sessions.DropDownItems.AddRange(open.Select(ConnItem).ToArray<ToolStripItem>());
            _trayMenu.Items.Add(sessions);
        }
        var favorites = _doc.Favorites();
        if (favorites.Count > 0)
        {
            var fav = new ToolStripMenuItem("Preferiti");
            fav.DropDownItems.AddRange(favorites.Select(ConnItem).ToArray<ToolStripItem>());
            _trayMenu.Items.Add(fav);
        }
        var recent = _doc.Recent(RecentCount);
        if (recent.Count > 0)
        {
            var rec = new ToolStripMenuItem("Recenti");
            rec.DropDownItems.AddRange(recent.Select(ConnItem).ToArray<ToolStripItem>());
            _trayMenu.Items.Add(rec);
        }

        // Tutte le connessioni, con le cartelle come sottomenu.
        var all = new ToolStripMenuItem("Tutte le connessioni");
        var folders = new Dictionary<string, ToolStripItemCollection>(StringComparer.CurrentCultureIgnoreCase) { [""] = all.DropDownItems };
        ToolStripItemCollection Folder(string path)
        {
            if (folders.TryGetValue(path, out var items))
                return items;
            int slash = path.LastIndexOf('/');
            var parent = Folder(slash < 0 ? "" : path[..slash]);
            var node = new ToolStripMenuItem(path[(slash + 1)..].Replace("&", "&&"));
            parent.Add(node);
            return folders[path] = node.DropDownItems;
        }
        foreach (var g in _doc.AllGroups())
            Folder(g);
        foreach (var c in _doc.Connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Folder(c.Group).Add(ConnItem(c));
        if (_doc.Connections.Count == 0)
            all.DropDownItems.Add(new ToolStripMenuItem("(nessuna connessione)") { Enabled = false });
        _trayMenu.Items.Add(all);

        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Esci", null, (_, _) =>
        {
            if (Enabled)
                ExitApp();
            else
                RestoreFromTray(); // prima va chiuso il dialogo aperto
        });
        ThemeManager.Apply(this, _trayMenu);
    }

    /// <summary>Esce davvero (anche con «Chiudendo resta nell'area di notifica»).</summary>
    internal void ExitApp()
    {
        _exitRequested = true;
        if (!Visible)
            Show();
        Close();
        _exitRequested = false; // chiusura annullata (es. sessioni connesse): si torna come prima
    }

    /// <summary>Con «resta nell'area di notifica» la chiusura nasconde la finestra; vero se l'ha fatto.</summary>
    private bool HideToTrayInsteadOfClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing || _exitRequested || !_doc.Settings.CloseToTray || _tray is null)
            return false;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "RdpManager è ancora aperto",
                "Le sessioni restano connesse. Clic sull'icona per riaprire, «Esci» nel suo menu per chiudere.", ToolTipIcon.Info);
        }
        return true;
    }

    private void OnFormClosedV12(object? sender, FormClosedEventArgs e)
    {
        UnregisterGlobalHotkey();
        UnregisterSessionHotkeys();
        _overviewTimer.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        // «Aggiorna ora» (con riavvio) o «Alla chiusura» (senza): solo un installer ancora verificato.
        if (_installNowRequested || _updates.InstallOnExit)
            StartVerifiedInstaller(relaunch: _installNowRequested);
    }

    // ---------- aggiornamento automatico con conferma ----------

    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly AutoUpdatePlanner _updates = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60 * 60 * 1000 };
    private UpdateOfferForm? _updateOffer;
    private bool _updating, _installNowRequested, _closingForUpdate;

    internal AutoUpdatePlanner Updates => _updates;
    internal UpdateOfferForm? UpdateOffer => _updateOffer is { IsDisposed: false } f ? f : null;

    /// <summary>Avvio dell'installer (le prove automatiche lo sostituiscono per non eseguire nulla).</summary>
    internal Action<ProcessStartInfo> StartInstaller { get; set; } = psi => Process.Start(psi);

    /// <summary>Per le prove: forza installata (false) o portatile (true); null = come è davvero.</summary>
    internal bool? PortableOverride { get; set; }

    /// <summary>Per le prove: risposta alla conferma di «Aggiorna ora» con sessioni aperte (null = chiede davvero).</summary>
    internal Func<string, bool>? ConfirmOverride { get; set; }

    /// <summary>Cartella degli aggiornamenti (le prove ne usano una temporanea).</summary>
    internal string UpdateFolder { get; set; } = UpdateDownloader.DefaultFolder;

    private bool IsPortable => PortableOverride ?? InstallLocation.IsPortable(Application.ExecutablePath);

    private void InitAutoUpdate(bool checkOnStartup)
    {
        if (!checkOnStartup)
            return;
        // All'avvio e poi ogni 24 ore (il timer guarda ogni ora se il giorno è passato).
        _updateTimer.Tick += async (_, _) =>
        {
            if (_updates.IsCheckDue(DateTime.UtcNow))
                await CheckForUpdatesAsync(manual: false);
            else if (_doc.Settings.UpdateMode == UpdateMode.AutomaticWithConfirmation && _updates.ShouldOffer(DateTime.UtcNow))
                ShowUpdateOffer();
        };
        _updateTimer.Start();
        FormClosed += (_, _) => _updateTimer.Stop();
    }

    /// <summary>
    /// Dopo aver trovato una versione più nuova: versione installata e modalità automatica (o richiesta a mano) →
    /// scarica in %LOCALAPPDATA%\RdpManager\updates, verifica lo SHA256 e, solo se coincide, propone l'aggiornamento.
    /// </summary>
    private async Task PrepareUpdateAsync(ReleaseInfo release, bool manual)
    {
        if (IsPortable)
        {
            // La versione portatile non si aggiorna da sola: si apre la pagina della release.
            if (manual && MessageBox.Show(this, $"È disponibile RdpManager {release.Version.ToString(3)} (in uso: {AppInfo.VersionText}).\n\n" +
                        "Questa è la versione portatile, che non si aggiorna da sola: aprire la pagina della release?",
                    "Aggiornamenti", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(release.PageUrl);
            return;
        }
        if (_updates.ShouldDownload(release, AppInfo.Version))
        {
            if (_updating)
                return;
            _updating = true;
            UpdateDownloadResult result;
            try
            {
                AppLog.Default?.Info($"Aggiornamento {release.Tag}: scaricamento e verifica");
                if (manual)
                    ShowNotice("Scaricamento dell'aggiornamento…");
                var progress = new Progress<double>(p => { if (manual) { _notice.Text = $"Scaricamento dell'aggiornamento: {p:P0}"; _notice.Visible = true; } });
                result = await UpdateDownloader.DownloadAndVerifyAsync(DownloadHttp, release, UpdateFolder, progress);
            }
            catch (Exception ex)
            {
                // Qualunque imprevisto (nome di file strano, errore del sistema): niente avvio e niente crash.
                result = new UpdateDownloadResult(UpdateDownloadStatus.Failed, null, "Scaricamento non riuscito: " + ex.Message);
            }
            finally
            {
                _updating = false;
            }
            if (IsDisposed)
                return;
            if (!_updates.Accept(release, result))
            {
                AppLog.Default?.Warning($"Aggiornamento {release.Tag} non verificato: {result.Status}");
                if (manual)
                    MessageBox.Show(this, result.Message + "\n\nNon è stato avviato nulla. Puoi scaricare il Setup dalla pagina della release.",
                        "Aggiornamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AppLog.Default?.Info($"Aggiornamento {release.Tag} verificato con SHA256");
        }
        if (_updates.Ready is { } ready)
            UpdateDownloader.CleanFolder(Path.GetDirectoryName(ready.InstallerPath)!, keep: ready.InstallerPath);
        if (manual || _updates.ShouldOffer(DateTime.UtcNow))
            ShowUpdateOffer();
    }

    /// <summary>Avviso non modale con le novità e le tre scelte.</summary>
    internal void ShowUpdateOffer()
    {
        // Da bloccata niente avvisi: si ripropone dopo lo sblocco (timer orario o menu).
        if (_updates.Ready is not { } ready || IsDisposed || _locked)
            return;
        if (UpdateOffer is { } open)
        {
            open.Activate();
            return;
        }
        var form = new UpdateOfferForm(ready, AppInfo.VersionText);
        form.PlaceOver(Bounds);
        form.Chosen += (_, choice) => OnUpdateChoice(choice);
        form.PageRequested += (_, _) => OpenUrl(ready.Release.PageUrl);
        form.FormClosed += (_, _) => BeginInvoke(form.Dispose);
        _updateOffer = form;
        if (Visible)
            form.Show(this);
        else
            form.Show();
    }

    private void OnUpdateChoice(UpdateChoice choice)
    {
        if (_updates.Ready is not { } ready)
            return;
        _updates.Choose(choice, DateTime.UtcNow);
        AppLog.Default?.Info($"Aggiornamento {ready.Release.Tag}: scelta {choice}");
        switch (choice)
        {
            case UpdateChoice.Later:
                ShowNotice($"Aggiornamento a {ready.Release.Version.ToString(3)} rimandato: te lo ripropongo tra 24 ore o al prossimo avvio.");
                break;
            case UpdateChoice.OnExit:
                ShowNotice($"RdpManager {ready.Release.Version.ToString(3)} si installerà quando chiudi l'app.");
                break;
            case UpdateChoice.Now:
                BeginInvoke(InstallNow);
                break;
        }
    }

    /// <summary>
    /// «Aggiorna ora»: con sessioni aperte chiede conferma (verranno chiuse e riaperte), salva le sessioni per la
    /// riapertura e chiude l'app; l'installer silenzioso parte alla chiusura e riapre RdpManager (/RELAUNCH=1).
    /// </summary>
    internal void InstallNow()
    {
        int open = AllViews.Count();
        var question = $"Le {open} sessioni aperte verranno chiuse e, dopo l'aggiornamento, riaperte.\n\nAggiornare ora RdpManager?";
        if (open > 0 && !(ConfirmOverride?.Invoke(question)
                          ?? MessageBox.Show(this, question, "Aggiornamento", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes))
            return;
        if (_updates.VerifiedInstallerToRun() is null)
        {
            MessageBox.Show(this, "L'installer scaricato non corrisponde più allo SHA256 verificato ed è stato cancellato: non viene avviato nulla.",
                "Aggiornamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _installNowRequested = _closingForUpdate = true;
        ExitApp();
        if (!IsDisposed && Visible)
        {
            _installNowRequested = _closingForUpdate = false; // uscita annullata: non si installa
            ShowNotice("Aggiornamento annullato.");
        }
    }

    /// <summary>Ricontrolla lo SHA256 e avvia l'installer silenzioso; un file non verificato non si esegue mai.</summary>
    private void StartVerifiedInstaller(bool relaunch)
    {
        var installer = _updates.VerifiedInstallerToRun();
        if (installer is null)
        {
            AppLog.Default?.Warning("Installer non più verificato alla chiusura: non avviato");
            return;
        }
        try
        {
            AppLog.Default?.Info($"Avvio dell'installer silenzioso (riapertura: {(relaunch ? "sì" : "no")})");
            StartInstaller(new ProcessStartInfo(installer)
            {
                Arguments = InstallLocation.SilentArguments(relaunch),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Default?.Error("Avvio dell'installer non riuscito", ex);
            MessageBox.Show("Impossibile avviare l'installer: " + ex.Message, "Aggiornamento", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- esportazione di un file .rdp ----------

    /// <summary>
    /// Salva la connessione come file .rdp per mstsc o altri client (con il gateway e l'utente del profilo, se ci sono).
    /// Senza password: una password DPAPI nel file varrebbe solo per questo utente Windows.
    /// </summary>
    private void ExportRdpFile(RdpConnection c)
    {
        var safe = string.Concat(c.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        using var save = new SaveFileDialog
        {
            Title = "Esporta come file .rdp", Filter = "Connessioni Desktop remoto (*.rdp)|*.rdp",
            FileName = safe + ".rdp", DefaultExt = "rdp", AddExtension = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            RdpFile.Save(save.FileName, _doc.WithResolvedCredentials(c), includePassword: false);
            ShowNotice($"«{c.DisplayName}» esportata in {Path.GetFileName(save.FileName)} (senza password)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Esportazione non riuscita: " + ex.Message, "Esporta", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- importazione dalla cronologia di mstsc ----------

    private void ImportMstscHistory()
    {
        IReadOnlyList<MstscHistoryItem> items;
        try
        {
            items = MstscHistory.ReadFromRegistry();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(this, "Impossibile leggere la cronologia di mstsc: " + ex.Message, "Importa", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Il client Desktop remoto di Windows non ha PC nella cronologia di questo utente.", "Importa",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new MstscImportDialog(items, _doc);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var added = MstscHistory.ImportInto(_doc, dlg.CheckedItems);
        SaveConnections();
        RefreshTree(added.LastOrDefault());
        ShowNotice(added.Count == 0 ? "Nessuna connessione nuova da importare." : $"Importate {added.Count} connessioni in «{MstscHistory.DefaultFolder}».");
        if (_backgroundChecks && added.Count > 0)
            _ = CheckReachabilityAsync(added);
    }
}
