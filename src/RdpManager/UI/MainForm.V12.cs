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
        InitHistory();
        _split.SplitterMoved += (_, _) => OnSplitterMoved();
        _split.Panel1.Padding = _split.Panel2.Padding = new Padding(0);
        _content.Controls.Add(_split);
        _overviewTimer.Tick += async (_, _) => await RefreshOverviewAsync();
        _trayMenu.Opening += (_, e) => { FillTrayMenu(); e.Cancel = false; };
        FormClosed += OnFormClosedV12;
        HandleCreated += (_, _) => RegisterGlobalHotkey();
        HandleDestroyed += (_, _) => UnregisterGlobalHotkey();
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
            if (_captureRestoreTab is { } restore && _tabs.TabPages.Contains(restore))
            {
                _silentTabSwitch = true;
                _tabs.SelectedTab = restore;
                _silentTabSwitch = false;
            }
            _captureRestoreTab = null;
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
        if (Overview is not { } overview || _overviewCapturing)
            return;
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
                if (Overview is null)
                    return;
                overview.UpdateThumbnail(v, await CaptureHiddenAsync(v));
            }
        }
        finally
        {
            _overviewCapturing = false;
        }
    }

    /// <summary>Copia dello schermo se la panoramica ne è esclusa (la più fedele), altrimenti PrintWindow.</summary>
    private static Bitmap? CaptureForOverview(OverviewForm overview, RdpSessionView view) =>
        overview.ExcludedFromCapture ? view.CaptureFromScreen() : view.CaptureSession();

    private async Task<Bitmap?> CaptureHiddenAsync(RdpSessionView view)
    {
        if (view.IsDetached || IsSplitView(view))
            return Overview is { } o ? CaptureForOverview(o, view) : null;
        if (PageOf(view) is not { } page)
            return null;
        var previous = _tabs.SelectedTab;
        _captureRestoreTab = previous;
        _silentTabSwitch = true;
        try
        {
            _tabs.SelectedTab = page;
        }
        finally
        {
            _silentTabSwitch = false;
        }
        await Task.Delay(150); // il controllo RDP ridisegna la sessione appena torna visibile
        // Panoramica chiusa nel frattempo: la scheda è già stata rimessa (o l'utente ne ha scelta un'altra).
        if (Overview is not { } overview || _captureRestoreTab is null)
            return null;
        var bmp = view.IsDisposed ? null : CaptureForOverview(overview, view);
        _captureRestoreTab = null;
        if (previous is not null && _tabs.TabPages.Contains(previous) && _tabs.SelectedTab == page)
        {
            _silentTabSwitch = true;
            _tabs.SelectedTab = previous;
            _silentTabSwitch = false;
        }
        return bmp;
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
        var focused = right.ContainsFocus ? right : left;
        SuspendLayout();
        _split.Visible = false;
        foreach (var v in new[] { left, right })
        {
            v.Parent?.Controls.Remove(v);
            if (PageOf(v) is { } page && !v.IsDisposed)
                page.Controls.Add(v);
            v.ToolbarVisible = !_fullScreen;
        }
        _splitLeft = _splitRight = null;
        ResumeLayout();
        _splitItem.Checked = false;
        if (PageOf(focused) is { } p)
            _tabs.SelectedTab = p;
        UpdateStatus();
        if (!focused.IsDisposed)
            BeginInvoke(focused.FocusSession);
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
        if (!Visible)
            Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
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
        _trayMenu.Items.Add("Ricerca rapida…", null, (_, _) => { RestoreFromTray(); BeginInvoke(ShowQuickSearch); });
        _trayMenu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem ConnItem(RdpConnection c)
        {
            bool open = AllViews.Any(v => v.Connection == c);
            var item = new ToolStripMenuItem((c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "")).Replace("&", "&&"))
            {
                Checked = open, ToolTipText = RdpFile.FormatAddress(c.Host, c.Port),
            };
            item.Click += (_, _) => { RestoreFromTray(); OpenSession(c); };
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
        _trayMenu.Items.Add("Esci", null, (_, _) => ExitApp());
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
        _overviewTimer.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        if (_pendingInstaller is { } installer)
        {
            try
            {
                Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show("Impossibile avviare l'installer: " + ex.Message, "Aggiornamento", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ---------- aggiornamento con un clic ----------

    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };
    private string? _pendingInstaller;
    private bool _updating;

    /// <summary>Propone l'aggiornamento: scaricarlo e verificarlo, oppure aprire la pagina della release.</summary>
    private async Task OfferUpdateAsync(ReleaseInfo release)
    {
        var page = new TaskDialogPage
        {
            Caption = "Aggiornamenti",
            Heading = $"È disponibile RdpManager {release.Version.ToString(3)}",
            Text = $"In uso: {AppInfo.VersionText}.\n\n«Aggiorna ora» scarica l'installer della release in una cartella temporanea, " +
                   $"ne controlla lo SHA256 con {Sha256Sums.FileName} della stessa release e lo avvia solo dopo la tua conferma.",
            Icon = TaskDialogIcon.Information,
        };
        var update = new TaskDialogButton("Aggiorna ora");
        var open = new TaskDialogButton("Apri la pagina");
        page.Buttons.Add(update);
        page.Buttons.Add(open);
        page.Buttons.Add(TaskDialogButton.Cancel);
        var answer = TaskDialog.ShowDialog(this, page);
        if (answer == open)
            OpenUrl(release.PageUrl);
        else if (answer == update)
            await DownloadAndInstallAsync(release);
    }

    private async Task DownloadAndInstallAsync(ReleaseInfo release)
    {
        if (_updating)
            return;
        _updating = true;
        UpdateDownloadResult result;
        try
        {
            ShowNotice("Scaricamento dell'aggiornamento…");
            var progress = new Progress<double>(p => { _notice.Text = $"Scaricamento dell'aggiornamento: {p:P0}"; _notice.Visible = true; });
            result = await UpdateDownloader.DownloadAndVerifyAsync(DownloadHttp, release, progress: progress);
        }
        finally
        {
            _updating = false;
        }
        if (IsDisposed)
            return;
        if (!result.IsVerified)
        {
            ShowNotice("Aggiornamento non riuscito");
            MessageBox.Show(this, result.Message + "\n\nNon è stato avviato nulla. Puoi scaricare il Setup dalla pagina della release.",
                "Aggiornamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ShowNotice(result.Message);
        if (MessageBox.Show(this, result.Message + "\n\nAvviare l'installazione ora? RdpManager si chiude (le sessioni connesse " +
                                  "vengono disconnesse) e l'installer lo aggiorna; connessioni e impostazioni restano.",
                "Aggiornamento", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _pendingInstaller = result.InstallerPath;
        ExitApp();
        if (!IsDisposed && Visible)
        {
            // Uscita annullata: l'installer non parte.
            _pendingInstaller = null;
            ShowNotice("Aggiornamento rimandato: l'installer verificato resta in " + Path.GetDirectoryName(result.InstallerPath));
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
