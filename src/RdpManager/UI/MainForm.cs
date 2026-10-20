using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.UI;

public sealed partial class MainForm : Form
{
    private sealed record FolderTag(string Path);

    /// <summary>Nodo delle sezioni rapide (Preferiti, Recenti): rimanda alla stessa connessione dell'elenco.</summary>
    private sealed record QuickTag(RdpConnection Connection);

    private sealed record SectionTag(string Name);

    private const int ImgFolder = 0, ImgComputer = 1, ImgStar = 2, ImgClock = 3;
    private const int RecentCount = 8;

    private readonly ConnectionStore _store;
    private ConnectionsDocument _doc = new();

    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Cerca (Ctrl+F)…" };
    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true, ShowNodeToolTips = true,
        BorderStyle = BorderStyle.None, ItemHeight = 22, Sorted = false,
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true, Visible = false };
    private readonly Label _emptyHint = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText,
    };
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _notice = new() { Visible = false };
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 8000 };
    private readonly ImageList _stateImages = new() { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ImageList _treeImages = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ContextMenuStrip _treeMenu = new();
    private readonly ContextMenuStrip _tabMenu = new();
    private readonly List<DetachedSessionForm> _detached = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<Guid, ReachabilityStatus> _reach = [];
    private readonly bool _backgroundChecks;

    private static readonly HttpClient Http = new();
    private readonly ToolStripStatusLabel _updateLabel = new() { IsLink = true, Visible = false };
    private ReleaseInfo? _availableUpdate;

    private static readonly string DefaultTitle = $"RdpManager {AppInfo.VersionText} - Desktop remoti";

    public MainForm() : this(new ConnectionStore(), checkUpdatesOnStartup: true, backgroundChecks: true) { }

    /// <summary>
    /// Con un archivio diverso da quello predefinito (prove automatiche: niente controlli in rete in background).
    /// <paramref name="systemIntegration"/>: icona nell'area di notifica e scorciatoia globale (predefinito: come
    /// <paramref name="backgroundChecks"/>, quindi spente nelle prove che non le chiedono).
    /// </summary>
    internal MainForm(ConnectionStore store, bool checkUpdatesOnStartup = false, bool backgroundChecks = false, bool? systemIntegration = null)
    {
        _store = store;
        _backgroundChecks = backgroundChecks;
        _systemIntegration = systemIntegration ?? backgroundChecks;
        Text = DefaultTitle;
        Size = new Size(1400, 900);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = CreateAppIcon();

        BuildImages();
        _tree.ImageList = _treeImages;
        _tabs.ImageList = _stateImages;
        _hook = new KeyboardHook(OnHookKeyDown);

        BuildLayout();
        _statusStrip.Items.Add(_status);
        _statusStrip.Items.Add(_notice);
        _statusStrip.Items.Add(_updateLabel);
        _updateLabel.Click += async (_, _) => { if (_availableUpdate is { } r) await OfferUpdateAsync(r); };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice.Visible = false; };
        if (checkUpdatesOnStartup)
            Shown += async (_, _) => await CheckForUpdatesAsync(manual: false);

        Controls.Add(_root);
        Controls.Add(_statusStrip);
        BuildMenu();
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        BuildContextMenus();
        WireEvents();
        LoadConnections();
        ApplySettings();
        InitV12();
        MstscLauncher.CleanupOldFiles();

        Load += (_, _) => PlaceOnPreferredMonitor();
        Shown += (_, _) => ReopenSessions();
        if (_backgroundChecks)
        {
            Shown += async (_, _) => await CheckReachabilityAsync(_doc.Connections.ToList());
            _reachTimer.Tick += async (_, _) => await CheckReachabilityAsync(_doc.Connections.ToList());
            _reachTimer.Start();
        }
    }

    private readonly System.Windows.Forms.Timer _reachTimer = new() { Interval = 60_000 };

    // ---------- costruzione interfaccia ----------

    private ToolStripMenuItem _fullScreenItem = null!, _pinItem = null!, _sidebarItem = null!, _monitorMenu = null!;
    private ToolStripMenuItem _nextSessionItem = null!, _previousSessionItem = null!;
    private ToolStripMenuItem _overviewItem = null!, _quickSearchItem = null!, _splitItem = null!;

    private void BuildMenu()
    {
        static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick, string? keyText = null) =>
            new(text, null, onClick) { ShortcutKeys = keys, ShortcutKeyDisplayString = keyText };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&Importa file .rdp…", Keys.Control | Keys.I, (_, _) => ImportRdpFiles()));
        file.DropDownItems.Add(Item("Importa connessioni (.rdp&m)…", Keys.None, (_, _) => ImportRdpm()));
        file.DropDownItems.Add(Item("&Esporta connessioni (.rdpm)…", Keys.None, (_, _) => ExportRdpm()));
        file.DropDownItems.Add(Item("Importa dalla cronologia di ms&tsc…", Keys.None, (_, _) => ImportMstscHistory()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Impo&stazioni…", Keys.Control | Keys.Oemcomma, (_, _) => ShowSettings(), "Ctrl+,"));
        file.DropDownItems.Add(Item("Apri &cartella dei dati", Keys.None, (_, _) => OpenDataFolder()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&sci", Keys.Alt | Keys.F4, (_, _) => ExitApp()));

        var conn = new ToolStripMenuItem("&Connessioni");
        conn.DropDownItems.Add(Item("&Nuova connessione…", Keys.Control | Keys.N, (_, _) => NewConnection()));
        conn.DropDownItems.Add(Item("Nuova &cartella…", Keys.Control | Keys.Shift | Keys.N, (_, _) => NewFolder()));
        conn.DropDownItems.Add(Item("&Modifica…", Keys.Control | Keys.E, (_, _) => EditSelected()));
        conn.DropDownItems.Add(Item("&Duplica", Keys.Control | Keys.D, (_, _) => DuplicateSelected()));
        conn.DropDownItems.Add(Item("&Elimina", Keys.None, (_, _) => DeleteSelected(), "Canc"));
        conn.DropDownItems.Add(new ToolStripSeparator());
        conn.DropDownItems.Add(Item("C&onnetti", Keys.None, (_, _) => ConnectSelected(), "Invio"));
        conn.DropDownItems.Add(Item("Connetti &tutta la cartella", Keys.None, (_, _) => ConnectFolder(SelectedFolderPath)));
        conn.DropDownItems.Add(Item("Apri con mst&sc", Keys.None, (_, _) => { if (SelectedConnection is { } c) LaunchMstsc(c); }));
        conn.DropDownItems.Add(Item("&Accendi (Wake-on-LAN)", Keys.None, async (_, _) => { if (SelectedConnection is { } c) await WakeAsync(c); }));
        conn.DropDownItems.Add(Item("&Preferita", Keys.Control | Keys.Shift | Keys.F, (_, _) => ToggleFavorite()));
        conn.DropDownItems.Add(Item("&Verifica raggiungibilità", Keys.None, async (_, _) => await CheckReachabilityAsync(_doc.Connections.ToList(), announce: true)));
        conn.DropDownItems.Add(new ToolStripSeparator());
        conn.DropDownItems.Add(Item("Pro&fili di credenziali…", Keys.None, (_, _) => ManageProfiles()));
        conn.DropDownItems.Add(Item("Cronolo&gia…", Keys.Control | Keys.H, (_, _) => ShowHistory(null)));
        conn.DropDownItems.Add(new ToolStripSeparator());
        conn.DropDownItems.Add(Item("Cerca", Keys.Control | Keys.F, (_, _) => FocusSearch()));

        var session = new ToolStripMenuItem("&Sessione");
        session.DropDownItems.Add(Item("&Riconnetti", Keys.F5, (_, _) => CurrentView?.Connect()));
        session.DropDownItems.Add(Item("S&tacca in una finestra", Keys.Control | Keys.Shift | Keys.D, (_, _) => { if (CurrentView is { } v) ToggleDetach(v); }));
        session.DropDownItems.Add(Item("Apri con &mstsc", Keys.None, (_, _) => CurrentView?.OpenWithMstsc()));
        session.DropDownItems.Add(new ToolStripSeparator());
        session.DropDownItems.Add(Item("&Incolla appunti come digitazione", Keys.None, async (_, _) => { if (CurrentView is { } v) await v.PasteClipboardAsTypingAsync(); }));
        session.DropDownItems.Add(Item("Invia Ctrl+Alt+&Canc", Keys.None, (_, _) => CurrentView?.SendCtrlAltDel()));
        session.DropDownItems.Add(Item("&Foto della sessione", Keys.None, async (_, _) => { if (CurrentView is { } v) await SaveSnapshotAsync(v); }));
        var quickTexts = new ToolStripMenuItem("&Testi rapidi");
        quickTexts.DropDownItems.Add("(caricamento)");
        quickTexts.DropDownOpening += (_, _) =>
        {
            if (CurrentView is { } v)
            {
                v.FillQuickTextsMenu(quickTexts);
                return;
            }
            quickTexts.DropDownItems.Clear();
            quickTexts.DropDownItems.Add(new ToolStripMenuItem("(nessuna sessione aperta)") { Enabled = false });
            quickTexts.DropDownItems.Add("Gestisci testi rapidi…", null, (_, _) => ManageQuickTexts());
        };
        session.DropDownItems.Add(quickTexts);
        session.DropDownItems.Add(new ToolStripSeparator());
        session.DropDownItems.Add(Item("C&hiudi scheda", Keys.Control | Keys.F4, (_, _) => { if (CurrentView is { } v) CloseSession(v); }));
        session.DropDownItems.Add(new ToolStripSeparator());
        session.DropDownItems.Add(Item("Scheda s&uccessiva", Keys.None, (_, _) => CycleTab(1), "Ctrl+Tab"));
        session.DropDownItems.Add(Item("Scheda &precedente", Keys.None, (_, _) => CycleTab(-1), "Ctrl+Shift+Tab"));
        _nextSessionItem = Item("Sessione connessa successiva", Keys.None, (_, _) => ExecuteShortcut(ShortcutAction.NextSession));
        _previousSessionItem = Item("Sessione connessa precedente", Keys.None, (_, _) => ExecuteShortcut(ShortcutAction.PreviousSession));
        session.DropDownItems.Add(_nextSessionItem);
        session.DropDownItems.Add(_previousSessionItem);

        var view = new ToolStripMenuItem("&Visualizza");
        _fullScreenItem = Item("&Schermo intero", Keys.F11, (_, _) => ToggleFullScreen());
        _sidebarItem = Item("&Mostra/nascondi barra laterale", Keys.None, (_, _) => ToggleSidebar());
        _pinItem = Item("Barra laterale &fissa", Keys.None, (_, _) => SetSidebarMode(_doc.Settings.SidebarMode == SidebarMode.Pinned ? SidebarMode.AutoHide : SidebarMode.Pinned));
        _monitorMenu = new ToolStripMenuItem("M&onitor per lo schermo intero");
        _monitorMenu.DropDownOpening += (_, _) => FillMonitorMenu();
        _monitorMenu.DropDownItems.Add("(caricamento)");
        _overviewItem = Item("&Panoramica delle sessioni", Keys.None, (_, _) => ToggleOverview());
        _quickSearchItem = Item("&Ricerca rapida…", Keys.None, (_, _) => ShowQuickSearch());
        _splitItem = Item("Vista &affiancata", Keys.None, (_, _) => ToggleSplitView());
        var splitWith = new ToolStripMenuItem("Affianca la sessione corrente a");
        splitWith.DropDownItems.Add("(caricamento)");
        splitWith.DropDownOpening += (_, _) => FillSplitMenu(splitWith);
        view.DropDownItems.Add(_fullScreenItem);
        view.DropDownItems.Add(_overviewItem);
        view.DropDownItems.Add(_quickSearchItem);
        view.DropDownItems.Add(_splitItem);
        view.DropDownItems.Add(splitWith);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_sidebarItem);
        view.DropDownItems.Add(_pinItem);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_monitorMenu);
        view.DropDownItems.Add(Item("Sposta la finestra sul monitor &successivo", Keys.None, (_, _) => MoveToNextMonitor()));

        var help = new ToolStripMenuItem("A&iuto");
        help.DropDownItems.Add(Item("&Scorciatoie da tastiera", Keys.None, (_, _) => ShowShortcutHelp()));
        help.DropDownItems.Add(Item("Controlla &aggiornamenti", Keys.None, async (_, _) => await CheckForUpdatesAsync(manual: true)));
        help.DropDownItems.Add(Item("Pagina del &progetto su GitHub", Keys.None, (_, _) => OpenUrl(AppInfo.RepositoryUrl)));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("&Informazioni", Keys.None, (_, _) => ShowAbout()));

        _menu.Items.AddRange([file, conn, session, view, help]);
    }

    private ToolStrip BuildTreeToolbar()
    {
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        bar.Items.Add(new ToolStripButton("Nuova", null, (_, _) => NewConnection()) { ToolTipText = "Nuova connessione (Ctrl+N)" });
        bar.Items.Add(new ToolStripButton("Cartella", null, (_, _) => NewFolder()) { ToolTipText = "Nuova cartella (Ctrl+Shift+N)" });
        bar.Items.Add(new ToolStripButton("Modifica", null, (_, _) => EditSelected()) { ToolTipText = "Modifica (Ctrl+E)" });
        bar.Items.Add(new ToolStripButton("Duplica", null, (_, _) => DuplicateSelected()) { ToolTipText = "Duplica (Ctrl+D)" });
        bar.Items.Add(new ToolStripButton("Elimina", null, (_, _) => DeleteSelected()) { ToolTipText = "Elimina (Canc)" });
        return bar;
    }

    /// <summary>Intestazione della barra laterale: titolo e pulsante per fissarla o renderla a comparsa.</summary>
    private ToolStrip BuildSidebarHeader()
    {
        var header = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, RenderMode = ToolStripRenderMode.System };
        header.Items.Add(new ToolStripLabel("Connessioni") { Font = new Font(Font, FontStyle.Bold) });
        _pinButton.Click += (_, _) => SetSidebarMode(_doc.Settings.SidebarMode == SidebarMode.Pinned ? SidebarMode.AutoHide : SidebarMode.Pinned);
        header.Items.Add(_pinButton);
        return header;
    }

    private ToolStripMenuItem _miConnect = null!, _miConnectFolder = null!, _miMstsc = null!, _miWake = null!,
        _miCheck = null!, _miFavorite = null!, _miEdit = null!, _miDuplicate = null!, _miDelete = null!, _miHistory = null!;

    private void BuildContextMenus()
    {
        ToolStripMenuItem Add(string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            _treeMenu.Items.Add(item);
            return item;
        }
        _miConnect = Add("Connetti", (_, _) => ConnectSelected());
        _miConnectFolder = Add("Connetti tutta la cartella", (_, _) => ConnectFolder(SelectedFolderPath));
        _miMstsc = Add("Apri con mstsc", (_, _) => { if (SelectedConnection is { } c) LaunchMstsc(c); });
        _miWake = Add("Accendi (Wake-on-LAN)", async (_, _) => { if (SelectedConnection is { } c) await WakeAsync(c); });
        _miCheck = Add("Verifica raggiungibilità", async (_, _) =>
        {
            var list = SelectedConnection is { } c ? [c] : _doc.ConnectionsInFolder(SelectedFolderPath).ToList();
            await CheckReachabilityAsync(list, announce: true);
        });
        _treeMenu.Items.Add(new ToolStripSeparator());
        _miFavorite = Add("Preferita ★", (_, _) => ToggleFavorite());
        _miHistory = Add("Cronologia…", (_, _) => { if (SelectedConnection is { } c) ShowHistory(c.Id); });
        _treeMenu.Items.Add(new ToolStripSeparator());
        Add("Nuova connessione…", (_, _) => NewConnection());
        Add("Nuova cartella…", (_, _) => NewFolder());
        _miEdit = Add("Modifica / Rinomina…", (_, _) => EditSelected());
        _miDuplicate = Add("Duplica", (_, _) => DuplicateSelected());
        _miDelete = Add("Elimina", (_, _) => DeleteSelected());
        _treeMenu.Opening += (_, _) =>
        {
            var c = SelectedConnection;
            bool isConn = c is not null;
            bool isFolder = _tree.SelectedNode?.Tag is FolderTag;
            _miConnect.Enabled = _miMstsc.Enabled = _miDuplicate.Enabled = _miFavorite.Enabled = _miHistory.Enabled = isConn;
            _miWake.Enabled = c is not null && WakeOnLan.IsValidMac(c.MacAddress);
            _miWake.ToolTipText = _miWake.Enabled ? null : "Indica il MAC nella modifica della connessione";
            _miConnectFolder.Visible = isFolder;
            _miCheck.Enabled = isConn || isFolder;
            _miFavorite.Checked = c?.IsFavorite == true;
            _miEdit.Enabled = isConn || isFolder;
            _miDelete.Enabled = isConn || isFolder;
        };

        _tabMenu.Items.Add("Riconnetti", null, (_, _) => CurrentView?.Connect());
        _tabMenu.Items.Add("Schermo intero", null, (_, _) => ToggleFullScreen());
        _tabMenu.Items.Add("Stacca in una finestra", null, (_, _) => { if (CurrentView is { } v) ToggleDetach(v); });
        _tabMenu.Items.Add("Apri con mstsc", null, (_, _) => CurrentView?.OpenWithMstsc());
        _tabMenu.Items.Add("Vista affiancata", null, (_, _) => ToggleSplitView());
        _tabMenu.Items.Add("Cronologia…", null, (_, _) => { if (CurrentView is { } v) ShowHistory(v.Connection.Id); });
        _tabMenu.Items.Add(new ToolStripSeparator());
        _tabMenu.Items.Add("Chiudi scheda", null, (_, _) => { if (CurrentView is { } v) CloseSession(v); });
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => RefreshTree();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _search.Clear(); e.SuppressKeyPress = true; }
            else if (e.KeyCode is Keys.Down or Keys.Enter) { _tree.Focus(); SelectFirstConnection(); e.SuppressKeyPress = true; }
        };

        _tree.NodeMouseDoubleClick += (_, e) => { if (ConnectionOf(e.Node) is { } c) OpenSession(c); };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.ContextMenuStrip = _treeMenu;
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { ConnectSelected(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F2) { EditSelected(); e.SuppressKeyPress = true; }
        };
        _tree.AfterCollapse += (_, e) => { if (CollapseKey(e.Node) is { } k && _search.TextLength == 0) _collapsed.Add(k); };
        _tree.AfterExpand += (_, e) => { if (CollapseKey(e.Node) is { } k && _search.TextLength == 0) _collapsed.Remove(k); };

        // Trascinamento delle connessioni nelle cartelle.
        _tree.ItemDrag += (_, e) => { if (e.Item is TreeNode { Tag: RdpConnection }) DoDragDrop(e.Item, DragDropEffects.Move); };
        _tree.DragOver += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
        _tree.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode { Tag: RdpConnection c })
                return;
            var target = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            c.Group = target?.Tag switch
            {
                FolderTag f => f.Path,
                RdpConnection other => other.Group,
                _ => "",
            };
            SaveConnections();
            RefreshTree(c);
        };

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_silentTabSwitch)
                return;
            UpdateStatus();
            if (_fullScreen)
                UpdateFullScreenBounds();
        };
        _tabs.MouseUp += (_, e) =>
        {
            for (int i = 0; i < _tabs.TabCount; i++)
            {
                if (!_tabs.GetTabRect(i).Contains(e.Location))
                    continue;
                _tabs.SelectedIndex = i;
                if (e.Button == MouseButtons.Right)
                    _tabMenu.Show(_tabs, e.Location);
                else if (e.Button == MouseButtons.Middle && CurrentView is { } v)
                    CloseSession(v);
                break;
            }
        };

        FormClosing += OnFormClosing;
    }

    private static string? CollapseKey(TreeNode? node) => node?.Tag switch
    {
        FolderTag f => f.Path,
        SectionTag s => "\0" + s.Name,
        _ => null,
    };

    // ---------- dati ----------

    private void LoadConnections()
    {
        try
        {
            _doc = _store.Load();
        }
        catch (Exception ex)
        {
            string backup = _store.FilePath + $".illeggibile-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(_store.FilePath, backup); } catch (Exception) { backup = "(copia non riuscita)"; }
            MessageBox.Show(this,
                $"Impossibile leggere {_store.FilePath}:\n{ex.Message}\n\nIl file originale è stato copiato in:\n{backup}\nSi parte con un elenco vuoto.",
                "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _doc = new ConnectionsDocument();
        }
        RefreshTree();
    }

    private void SaveConnections()
    {
        try
        {
            _store.Save(_doc);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Salvataggio non riuscito ({_store.FilePath}):\n{ex.Message}", "RdpManager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateStatus();
    }

    // ---------- elenco ----------

    private static RdpConnection? ConnectionOf(TreeNode? node) => node?.Tag switch
    {
        RdpConnection c => c,
        QuickTag q => q.Connection,
        _ => null,
    };

    private RdpConnection? SelectedConnection => ConnectionOf(_tree.SelectedNode);

    private string SelectedFolderPath => _tree.SelectedNode?.Tag switch
    {
        FolderTag f => f.Path,
        RdpConnection c => c.Group,
        QuickTag q => q.Connection.Group,
        _ => "",
    };

    private void RefreshTree(object? select = null)
    {
        select ??= _tree.SelectedNode?.Tag;
        if (select is QuickTag q)
            select = q.Connection;
        var query = _search.Text.Trim();
        bool filtering = query.Length > 0;
        var folders = new Dictionary<string, TreeNode>(StringComparer.CurrentCultureIgnoreCase);

        TreeNodeCollection FolderNodes(string path)
        {
            if (path.Length == 0)
                return _tree.Nodes;
            if (folders.TryGetValue(path, out var existing))
                return existing.Nodes;
            int slash = path.LastIndexOf('/');
            var parent = FolderNodes(slash < 0 ? "" : path[..slash]);
            var node = new TreeNode(path[(slash + 1)..], ImgFolder, ImgFolder) { Tag = new FolderTag(path), Name = "f:" + path };
            parent.Add(node);
            folders[path] = node;
            return node.Nodes;
        }

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (!filtering)
        {
            // Sezioni rapide in cima: Preferiti e Recenti (gli stessi oggetti dell'elenco, non copie).
            AddSection("Preferiti", ImgStar, _doc.Favorites());
            AddSection("Recenti", ImgClock, _doc.Recent(RecentCount));
            foreach (var g in _doc.AllGroups())
                FolderNodes(g);
        }
        TreeNode? toSelect = null;
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        foreach (var c in _doc.Connections.Where(c => c.Matches(query) || (filtering && c.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                     .OrderBy(c => c.DisplayName, comparer))
        {
            var node = ConnectionNode(c, c);
            FolderNodes(c.Group).Add(node);
            if (ReferenceEquals(c, select))
                toSelect = node;
        }
        foreach (var (path, node) in folders)
        {
            if (filtering || !_collapsed.Contains(path))
                node.Expand();
            if (select is FolderTag f && comparer.Equals(f.Path, path))
                toSelect = node;
        }
        _tree.EndUpdate();
        if (toSelect is not null)
        {
            _tree.SelectedNode = toSelect;
            toSelect.EnsureVisible();
        }
        UpdateStatus();

        void AddSection(string name, int image, IReadOnlyList<RdpConnection> items)
        {
            if (items.Count == 0)
                return;
            var section = new TreeNode(name, image, image) { Tag = new SectionTag(name), NodeFont = new Font(_tree.Font, FontStyle.Bold) };
            foreach (var c in items)
                section.Nodes.Add(ConnectionNode(c, new QuickTag(c)));
            _tree.Nodes.Add(section);
            if (!_collapsed.Contains("\0" + name))
                section.Expand();
        }
    }

    private TreeNode ConnectionNode(RdpConnection c, object tag)
    {
        var node = new TreeNode(NodeText(c)) { Tag = tag };
        ApplyNodeState(node, c);
        return node;
    }

    private static string NodeText(RdpConnection c) =>
        c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");

    /// <summary>Stato mostrato nell'elenco: quello della sessione se è aperta, altrimenti la raggiungibilità.</summary>
    private (string Word, Color? Dot, bool Filled) NodeState(RdpConnection c)
    {
        if (AllViews.FirstOrDefault(v => v.Connection == c) is { } view)
            return (RdpSessionView.StateWord(view.State), RdpSessionView.StateColor(view.State), true);
        return _reach.GetValueOrDefault(c.Id) switch
        {
            ReachabilityStatus.Reachable => ("Raggiungibile", Color.FromArgb(46, 160, 67), false),
            ReachabilityStatus.Unreachable => ("Non raggiungibile", Color.FromArgb(150, 150, 150), false),
            _ => ("Non verificata", null, false),
        };
    }

    private void ApplyNodeState(TreeNode node, RdpConnection c)
    {
        var (word, dot, filled) = NodeState(c);
        node.ImageKey = node.SelectedImageKey = ComputerIcon(ConnectionDialog.ToColor(c.Color), dot, filled);
        var tip = $"{RdpFile.FormatAddress(c.Host, c.Port)} — {word}";
        var creds = _doc.ResolveCredentials(c);
        if (creds.Username.Length > 0)
            tip += $"\nUtente: {(creds.Domain.Length > 0 ? creds.Domain + "\\" : "")}{creds.Username}" +
                   (_doc.FindProfile(creds.ProfileId) is { } profile ? $" (profilo «{profile.Name}»)" : "");
        if (c.HasGateway)
            tip += $"\nGateway: {c.GatewayHost}";
        if (c.LastConnectedUtc is { } last)
            tip += $"\nUltima connessione: {last.ToLocalTime():g}";
        node.ToolTipText = tip;
        node.NodeFont = AllViews.Any(v => v.Connection == c && v.State == SessionState.Connected) ? new Font(_tree.Font, FontStyle.Bold) : null;
        if (node.Text != NodeText(c))
            node.Text = NodeText(c);
    }

    /// <summary>Aggiorna stato e colore dei nodi senza ricostruire l'elenco (selezione e scorrimento restano).</summary>
    private void UpdateTreeStates()
    {
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (ConnectionOf(n) is { } c)
                    ApplyNodeState(n, c);
                Walk(n.Nodes);
            }
        }
        _tree.BeginUpdate();
        Walk(_tree.Nodes);
        _tree.EndUpdate();
    }

    private void SelectFirstConnection()
    {
        static TreeNode? First(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is RdpConnection) return n;
                if (First(n.Nodes) is { } inner) return inner;
            }
            return null;
        }
        if (First(_tree.Nodes) is { } node)
            _tree.SelectedNode = node;
    }

    private void FocusSearch()
    {
        if (!SidebarShown)
            ShowSidebar(focusSearch: true);
        _search.Focus();
        _search.SelectAll();
    }

    private void NewConnection()
    {
        var c = new RdpConnection { Group = SelectedFolderPath };
        using var dlg = new ConnectionDialog(c, _doc.AllGroups(), "Nuova connessione", _doc.CredentialProfiles);
        ThemeManager.Apply(dlg);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _doc.Connections.Add(c);
        SaveConnections();
        RefreshTree(c);
    }

    private void EditSelected()
    {
        if (SelectedConnection is { } c)
        {
            EditConnection(c);
            return;
        }
        if (_tree.SelectedNode?.Tag is FolderTag f)
            RenameFolder(f.Path);
    }

    private void EditConnection(RdpConnection c)
    {
        var edited = c.Clone();
        using (var dlg = new ConnectionDialog(edited, _doc.AllGroups(), $"Modifica: {c.DisplayName}", _doc.CredentialProfiles))
        {
            ThemeManager.Apply(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
        }
        CopyInto(edited, c);
        SaveConnections();
        RefreshTree(c);
        UpdateSessionTitles();
    }

    /// <summary>
    /// Copia i valori modificati nell'oggetto originale, che le sessioni aperte già referenziano.
    /// Tutte le proprietà scrivibili tranne l'Id: così i campi aggiunti in futuro non vanno persi.
    /// </summary>
    internal static void CopyInto(RdpConnection from, RdpConnection to)
    {
        foreach (var p in typeof(RdpConnection).GetProperties())
            if (p.CanRead && p.CanWrite && p.Name != nameof(RdpConnection.Id))
                p.SetValue(to, p.GetValue(from));
    }

    private void DuplicateSelected()
    {
        if (SelectedConnection is not { } c)
            return;
        var copy = c.Duplicate();
        copy.LastConnectedUtc = null;
        _doc.Connections.Add(copy);
        SaveConnections();
        RefreshTree(copy);
    }

    private void DeleteSelected()
    {
        if (SelectedConnection is { } c)
        {
            if (MessageBox.Show(this, $"Eliminare la connessione «{c.DisplayName}»?", "Elimina",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _doc.Connections.Remove(c);
            SaveConnections();
            RefreshTree();
            return;
        }
        if (_tree.SelectedNode?.Tag is FolderTag f)
            DeleteFolder(f.Path);
    }

    private void ToggleFavorite()
    {
        if (SelectedConnection is not { } c)
            return;
        c.IsFavorite = !c.IsFavorite;
        SaveConnections();
        RefreshTree(c);
    }

    private void NewFolder()
    {
        var parent = SelectedFolderPath;
        var name = TextPromptDialog.Ask(this, "Nuova cartella",
            parent.Length > 0 ? $"Nome della cartella (dentro «{parent}»):" : "Nome della cartella:");
        if (_doc.AddFolder(parent, name) is not { } path)
            return;
        SaveConnections();
        RefreshTree(new FolderTag(path));
    }

    private void RenameFolder(string path)
    {
        var name = TextPromptDialog.Ask(this, "Rinomina cartella", "Nuovo nome:", path[(path.LastIndexOf('/') + 1)..]);
        if (_doc.RenameFolder(path, name) is not { } newPath)
            return;
        SaveConnections();
        RefreshTree(new FolderTag(newPath));
    }

    private void DeleteFolder(string path)
    {
        var parent = ConnectionsDocument.ParentFolder(path);
        var inside = _doc.Connections.Where(c => ConnectionsDocument.IsInFolder(c.Group, path)).ToList();
        var message = inside.Count == 0
            ? $"Eliminare la cartella «{path}»?"
            : $"Eliminare la cartella «{path}»?\nLe {inside.Count} connessioni contenute non vengono cancellate: passano in «{(parent.Length > 0 ? parent : "radice")}».";
        if (MessageBox.Show(this, message, "Elimina cartella", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _doc.DeleteFolder(path);
        SaveConnections();
        RefreshTree(parent.Length > 0 ? new FolderTag(parent) : null);
    }

    private void ImportRdpFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Importa file .rdp", Filter = "Connessioni Desktop remoto (*.rdp)|*.rdp|Tutti i file (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var group = SelectedFolderPath;
        var errors = new List<string>();
        RdpConnection? last = null;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var c = RdpFile.Load(file);
                if (string.IsNullOrWhiteSpace(c.Host))
                    throw new InvalidDataException("indirizzo («full address») mancante");
                c.Group = group;
                _doc.Connections.Add(c);
                last = c;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        SaveConnections();
        RefreshTree(last);
        int ok = dlg.FileNames.Length - errors.Count;
        MessageBox.Show(this,
            $"Importate {ok} connessioni" + (group.Length > 0 ? $" nella cartella «{group}»." : ".") +
            (errors.Count > 0 ? "\n\nNon importati:\n" + string.Join('\n', errors) : ""),
            "Importa .rdp", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---------- esportazione e importazione .rdpm ----------

    private void ExportRdpm()
    {
        if (_doc.Connections.Count == 0)
        {
            MessageBox.Show(this, "Non ci sono connessioni da esportare.", "Esporta", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? password;
        using (var options = new RdpmExportDialog(_doc.Connections.Count))
        {
            ThemeManager.Apply(options);
            if (options.ShowDialog(this) != DialogResult.OK)
                return;
            password = options.FilePassword;
        }
        using var save = new SaveFileDialog
        {
            Title = "Esporta connessioni", Filter = RdpmFile.DialogFilter, DefaultExt = RdpmFile.Extension.TrimStart('.'),
            FileName = $"connessioni-{DateTime.Now:yyyy-MM-dd}{RdpmFile.Extension}", AddExtension = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            Cursor = Cursors.WaitCursor;
            RdpmFile.Save(save.FileName, _doc, password);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Esportazione non riuscita: " + ex.Message, "Esporta", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        int withPassword = password is null ? 0
            : _doc.Connections.Count(c => !string.IsNullOrEmpty(c.Password)) + _doc.CredentialProfiles.Count(p => !string.IsNullOrEmpty(p.Password));
        var profiles = _doc.CredentialProfiles.Count > 0 ? $", {_doc.CredentialProfiles.Count} profili" : "";
        ShowNotice($"Esportate {_doc.Connections.Count} connessioni{profiles} in {Path.GetFileName(save.FileName)}" +
                   (password is null ? " (senza password)" : $" ({withPassword} password cifrate)"));
    }

    private void ImportRdpm()
    {
        using var open = new OpenFileDialog { Title = "Importa connessioni", Filter = RdpmFile.DialogFilter };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        var name = Path.GetFileName(open.FileName);
        ConnectionsDocument imported;
        try
        {
            var json = File.ReadAllText(open.FileName);
            string? password = null;
            bool retry = false;
            while (true)
            {
                if (RdpmFile.IsEncrypted(json))
                {
                    password = FilePasswordDialog.Ask(this, name, retry);
                    if (password is null)
                        return;
                }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    imported = RdpmFile.Import(json, password);
                    break;
                }
                catch (RdpmPasswordException)
                {
                    retry = true;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, $"«{name}» non si può importare: {ex.Message}", "Importa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ImportMode mode;
        using (var dlg = new RdpmImportDialog(imported.Connections.Count, _doc.Connections.Count,
                   imported.Connections.Any(c => c.HasPassword) || imported.CredentialProfiles.Any(p => p.HasPassword)))
        {
            ThemeManager.Apply(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
            mode = dlg.Mode;
        }
        var result = RdpmFile.ApplyImport(_doc, imported, mode);
        SaveConnections();
        RefreshTree();
        var text = $"Aggiunte {result.Added} connessioni";
        if (result.Skipped > 0)
            text += $", {result.Skipped} già presenti";
        if (result.Removed > 0)
            text += $", {result.Removed} sostituite";
        if (result.ProfilesAdded > 0)
            text += $", {result.ProfilesAdded} profili di credenziali";
        if (result.QuickTextsAdded > 0)
            text += $", {result.QuickTextsAdded} testi rapidi";
        MessageBox.Show(this, text + ".", "Importa", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (_backgroundChecks)
            _ = CheckReachabilityAsync(_doc.Connections.ToList());
    }

    // ---------- rete: raggiungibilità e Wake-on-LAN ----------

    private bool _checking;

    /// <summary>Prova la porta RDP delle connessioni (al massimo 8 alla volta) e aggiorna lo stato nell'elenco.</summary>
    private async Task CheckReachabilityAsync(IReadOnlyList<RdpConnection> list, bool announce = false)
    {
        if (_checking && !announce)
            return;
        _checking = true;
        try
        {
            using var gate = new SemaphoreSlim(8);
            var tasks = list.Select(async c =>
            {
                await gate.WaitAsync();
                try { return (c, await HostReachability.CheckAsync(c)); }
                finally { gate.Release(); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            if (IsDisposed)
                return;
            foreach (var (c, r) in results)
                _reach[c.Id] = r.Status;
            UpdateTreeStates();
            if (announce)
            {
                int ok = results.Count(r => r.Item2.IsReachable);
                ShowNotice(list.Count == 1
                    ? $"«{list[0].DisplayName}»: {(ok == 1 ? "raggiungibile" : "non raggiungibile")}"
                    : $"Raggiungibili {ok} su {list.Count}");
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task WakeAsync(RdpConnection c)
    {
        if (!WakeOnLan.IsValidMac(c.MacAddress))
        {
            if (MessageBox.Show(this, $"Per accendere «{c.DisplayName}» serve l'indirizzo MAC della sua scheda di rete.\n\nAprire la modifica della connessione?",
                    "Wake-on-LAN", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                EditConnection(c);
            return;
        }
        try
        {
            await WakeOnLan.SendAsync(c.MacAddress);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or FormatException)
        {
            MessageBox.Show(this, "Invio non riuscito: " + ex.Message, "Wake-on-LAN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ShowNotice($"Accensione inviata a «{c.DisplayName}» ({c.MacAddress}). Può servire un minuto.");
        // Si controlla per un paio di minuti quando il PC risponde.
        for (int i = 0; i < 8 && !IsDisposed; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (IsDisposed)
                return;
            var r = await HostReachability.CheckAsync(c);
            if (IsDisposed)
                return;
            _reach[c.Id] = r.Status;
            UpdateTreeStates();
            if (r.IsReachable)
            {
                ShowNotice($"«{c.DisplayName}» è raggiungibile.");
                return;
            }
        }
    }

    // ---------- varie ----------

    private void ShowNotice(string text)
    {
        _notice.Text = text;
        _notice.Visible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void UpdateStatus()
    {
        int sessions = _tabs.TabCount + _detached.Count;
        int connected = AllViews.Count(v => v.State == SessionState.Connected);
        _status.Text = $"{_doc.Connections.Count} connessioni salvate · {sessions} sessioni aperte ({connected} connesse)";
        Text = CurrentView is { } v ? $"{v.Connection.DisplayName} - RdpManager {AppInfo.VersionText}" : DefaultTitle;
    }

    private void UpdateEmptyHint()
    {
        var map = _shortcuts;
        _emptyHint.Text = "Nessuna sessione aperta.\n\nFai doppio clic su una connessione nella barra laterale (o premi Invio) per aprirla in una scheda.\n" +
                          $"Ctrl+N nuova connessione · Ctrl+Tab cambia scheda · {map[ShortcutAction.ToggleFullScreen]} schermo intero · " +
                          $"{map[ShortcutAction.ToggleSidebar]} barra laterale\n" +
                          $"{map[ShortcutAction.QuickSearch]} ricerca rapida · {map[ShortcutAction.Overview]} panoramica · " +
                          $"{map[ShortcutAction.ToggleSplitView]} vista affiancata";
    }

    private void ShowShortcutHelp()
    {
        var lines = Enum.GetValues<ShortcutAction>()
            .Select(a => $"{(_shortcuts[a].IsEmpty ? "(nessuna)" : _shortcuts[a].ToString()),-20}\t{ShortcutMap.Describe(a)}");
        MessageBox.Show(this,
            "Valgono anche con il fuoco dentro la sessione remota:\n\n" + string.Join('\n', lines) +
            "\n\nNell'app: Ctrl+Tab / Ctrl+Shift+Tab cambia scheda, F5 riconnetti, F11 schermo intero, Ctrl+F cerca.\n" +
            "Le scorciatoie si cambiano in File › Impostazioni.",
            "Scorciatoie da tastiera", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Controllo aggiornamenti sulle release GitHub (solo avviso, nessun download). All'avvio: in
    /// background, timeout breve, nessun messaggio se fallisce. Dal menu: dice anche l'esito.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var result = await UpdateChecker.CheckAsync(Http, AppInfo.Version, TimeSpan.FromSeconds(manual ? 15 : 5));
        if (IsDisposed)
            return;
        if (result is { Status: UpdateCheckStatus.NewerAvailable, Release: { } release })
        {
            _availableUpdate = release;
            _updateLabel.Text = $"È disponibile la versione {release.Version.ToString(3)} — clic per aggiornare";

            _updateLabel.ToolTipText = release.PageUrl;
            _updateLabel.Visible = true;
        }
        if (!manual)
            return;
        switch (result.Status)
        {
            case UpdateCheckStatus.NewerAvailable:
                await OfferUpdateAsync(result.Release!);
                break;
            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(this, $"RdpManager {AppInfo.VersionText} è la versione più recente.", "Aggiornamenti",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            default:
                MessageBox.Show(this, "Impossibile verificare gli aggiornamenti (rete assente o pagina delle release non raggiungibile).\n\n" +
                                      $"Puoi controllare a mano: {AppInfo.ReleasesUrl}", "Aggiornamenti",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenDataFolder()
    {
        var dir = Path.GetDirectoryName(_store.FilePath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
    }

    private void ShowAbout() =>
        MessageBox.Show(this,
            $"RdpManager {AppInfo.VersionText} — più desktop remoti nella stessa finestra.\n\n" +
            $"Progetto: {AppInfo.RepositoryUrl}\n" +
            $"Controllo RDP: {RdpClientHost.AvailableClassName ?? "non disponibile (usa «Apri con mstsc»)"}\n" +
            $"Dati: {_store.FilePath}\n\nLe password sono cifrate con DPAPI per l'utente Windows corrente.",
            "Informazioni", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ---------- immagini ----------

    private void BuildImages()
    {
        // Indici = valori di SessionState: Idle, Connecting, Connected, Disconnected.
        foreach (var state in new[] { SessionState.Idle, SessionState.Connecting, SessionState.Connected, SessionState.Disconnected })
        {
            var bmp = new Bitmap(12, 12);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(RdpSessionView.StateColor(state));
            g.FillEllipse(brush, 1, 1, 10, 10);
            _stateImages.Images.Add(bmp);
        }

        var folder = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(folder))
        {
            using var fill = new SolidBrush(Color.FromArgb(240, 196, 80));
            using var edge = new Pen(Color.FromArgb(190, 140, 40));
            g.FillRectangle(fill, 1, 4, 14, 10);
            g.FillRectangle(fill, 1, 2, 6, 3);
            g.DrawRectangle(edge, 1, 4, 13, 9);
        }
        _treeImages.Images.Add(folder);
        _treeImages.Images.Add(DrawComputer(null, null, false));

        var star = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(star))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var points = Enumerable.Range(0, 10).Select(i =>
            {
                double angle = -Math.PI / 2 + i * Math.PI / 5;
                double r = i % 2 == 0 ? 7.5 : 3.2;
                return new PointF((float)(8 + r * Math.Cos(angle)), (float)(8.5 + r * Math.Sin(angle)));
            }).ToArray();
            using var fill = new SolidBrush(Color.FromArgb(245, 180, 30));
            g.FillPolygon(fill, points);
        }
        _treeImages.Images.Add(star);

        var clock = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(clock))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(90, 110, 140), 1.6f);
            g.DrawEllipse(pen, 1.5f, 1.5f, 13, 13);
            g.DrawLine(pen, 8, 8, 8, 4);
            g.DrawLine(pen, 8, 8, 11, 9.5f);
        }
        _treeImages.Images.Add(clock);
    }

    /// <summary>Icona del PC con la striscia del colore della connessione e il pallino dello stato (in cache per chiave).</summary>
    private string ComputerIcon(Color? color, Color? dot, bool filled)
    {
        var key = $"pc|{color?.ToArgb()}|{dot?.ToArgb()}|{filled}";
        if (!_treeImages.Images.ContainsKey(key))
            _treeImages.Images.Add(key, DrawComputer(color, dot, filled));
        return key;
    }

    private static Bitmap DrawComputer(Color? color, Color? dot, bool filled)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var frame = new SolidBrush(Color.FromArgb(60, 60, 70));
        using var screen = new SolidBrush(color ?? Color.FromArgb(70, 140, 220));
        g.FillRectangle(frame, 1, 2, 14, 10);
        g.FillRectangle(screen, 2, 3, 12, 8);
        g.FillRectangle(frame, 6, 12, 4, 2);
        g.FillRectangle(frame, 4, 14, 8, 1);
        if (dot is { } d)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, 8.5f, 8.5f, 7.5f, 7.5f);
            if (filled)
            {
                using var b = new SolidBrush(d);
                g.FillEllipse(b, 9.5f, 9.5f, 5.5f, 5.5f);
            }
            else
            {
                using var p = new Pen(d, 2f);
                g.DrawEllipse(p, 10.2f, 10.2f, 4.2f, 4.2f);
            }
        }
        return bmp;
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var frame = new SolidBrush(Color.FromArgb(45, 45, 55));
            using var back = new SolidBrush(Color.FromArgb(40, 110, 200));
            using var front = new SolidBrush(Color.FromArgb(90, 180, 250));
            g.FillRectangle(frame, 2, 4, 22, 16);
            g.FillRectangle(back, 4, 6, 18, 12);
            g.FillRectangle(frame, 8, 14, 22, 16);
            g.FillRectangle(front, 10, 16, 18, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
