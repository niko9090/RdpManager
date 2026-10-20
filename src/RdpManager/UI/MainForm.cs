using System.Diagnostics;
using System.Drawing.Drawing2D;
using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.UI;

public sealed class MainForm : Form
{
    private sealed record FolderTag(string Path);

    private const int ImgFolder = 0, ImgComputer = 1;

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
        Text = "Nessuna sessione aperta.\n\nFai doppio clic su una connessione a sinistra (o premi Invio) per aprirla in una scheda.\n" +
               "Ctrl+N nuova connessione · Ctrl+Tab / Ctrl+Shift+Tab cambia scheda · F11 schermo intero",
    };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ImageList _stateImages = new() { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ImageList _treeImages = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ContextMenuStrip _treeMenu = new();
    private readonly ContextMenuStrip _tabMenu = new();
    private readonly List<DetachedSessionForm> _detached = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);

    private static readonly HttpClient Http = new();
    private readonly ToolStripStatusLabel _updateLabel = new() { IsLink = true, Visible = false };
    private ReleaseInfo? _availableUpdate;

    private static readonly string DefaultTitle = $"RdpManager {AppInfo.VersionText} - Desktop remoti";

    public MainForm() : this(new ConnectionStore(), checkUpdatesOnStartup: true) { }

    /// <summary>Con un archivio diverso da quello predefinito (usato dalle prove automatiche, senza controllo aggiornamenti).</summary>
    internal MainForm(ConnectionStore store, bool checkUpdatesOnStartup = false)
    {
        _store = store;
        Text = DefaultTitle;
        Size = new Size(1400, 900);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = CreateAppIcon();

        BuildImages();
        _tree.ImageList = _treeImages;
        _tabs.ImageList = _stateImages;

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
        split.Panel1.Controls.Add(_tree);
        split.Panel1.Controls.Add(BuildTreeToolbar());
        split.Panel1.Controls.Add(_search);
        split.Panel2.Controls.Add(_tabs);
        split.Panel2.Controls.Add(_emptyHint);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_updateLabel);
        _updateLabel.Click += (_, _) => { if (_availableUpdate is { } r) OpenUrl(r.PageUrl); };
        if (checkUpdatesOnStartup)
            Shown += async (_, _) => await CheckForUpdatesAsync(manual: false);

        Controls.Add(split);
        Controls.Add(statusStrip);
        Controls.Add(BuildMenu());
        Load += (_, _) => split.SplitterDistance = 280;

        BuildContextMenus();
        WireEvents();
        LoadConnections();
        MstscLauncher.CleanupOldFiles();
    }

    // ---------- costruzione interfaccia ----------

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick, string? keyText = null) =>
            new(text, null, onClick) { ShortcutKeys = keys, ShortcutKeyDisplayString = keyText };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&Importa file .rdp…", Keys.Control | Keys.I, (_, _) => ImportRdpFiles()));
        file.DropDownItems.Add(Item("Apri &cartella dei dati", Keys.None, (_, _) => OpenDataFolder()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Esci", Keys.Alt | Keys.F4, (_, _) => Close()));

        var conn = new ToolStripMenuItem("&Connessioni");
        conn.DropDownItems.Add(Item("&Nuova connessione…", Keys.Control | Keys.N, (_, _) => NewConnection()));
        conn.DropDownItems.Add(Item("Nuova &cartella…", Keys.Control | Keys.Shift | Keys.N, (_, _) => NewFolder()));
        conn.DropDownItems.Add(Item("&Modifica…", Keys.Control | Keys.E, (_, _) => EditSelected()));
        conn.DropDownItems.Add(Item("&Duplica", Keys.Control | Keys.D, (_, _) => DuplicateSelected()));
        conn.DropDownItems.Add(Item("&Elimina", Keys.None, (_, _) => DeleteSelected(), "Canc"));
        conn.DropDownItems.Add(new ToolStripSeparator());
        conn.DropDownItems.Add(Item("C&onnetti", Keys.None, (_, _) => ConnectSelected(), "Invio"));
        conn.DropDownItems.Add(Item("Apri con mst&sc", Keys.None, (_, _) => { if (SelectedConnection is { } c) LaunchMstsc(c); }));
        conn.DropDownItems.Add(Item("Cerca", Keys.Control | Keys.F, (_, _) => { _search.Focus(); _search.SelectAll(); }));

        var session = new ToolStripMenuItem("&Sessione");
        session.DropDownItems.Add(Item("&Riconnetti", Keys.F5, (_, _) => CurrentView?.Connect()));
        session.DropDownItems.Add(Item("&Schermo intero", Keys.F11, (_, _) => CurrentView?.EnterFullScreen()));
        session.DropDownItems.Add(Item("S&tacca in una finestra", Keys.Control | Keys.Shift | Keys.D, (_, _) => { if (CurrentView is { } v) ToggleDetach(v); }));
        session.DropDownItems.Add(Item("Apri con &mstsc", Keys.None, (_, _) => CurrentView?.OpenWithMstsc()));
        session.DropDownItems.Add(Item("&Chiudi scheda", Keys.Control | Keys.F4, (_, _) => { if (CurrentView is { } v) CloseSession(v); }));
        session.DropDownItems.Add(new ToolStripSeparator());
        session.DropDownItems.Add(Item("Scheda s&uccessiva", Keys.None, (_, _) => CycleTab(1), "Ctrl+Tab"));
        session.DropDownItems.Add(Item("Scheda &precedente", Keys.None, (_, _) => CycleTab(-1), "Ctrl+Shift+Tab"));

        var help = new ToolStripMenuItem("A&iuto");
        help.DropDownItems.Add(Item("Controlla &aggiornamenti", Keys.None, async (_, _) => await CheckForUpdatesAsync(manual: true)));
        help.DropDownItems.Add(Item("Pagina del &progetto su GitHub", Keys.None, (_, _) => OpenUrl(AppInfo.RepositoryUrl)));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("&Informazioni", Keys.None, (_, _) => ShowAbout()));

        menu.Items.AddRange([file, conn, session, help]);
        return menu;
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

    private void BuildContextMenus()
    {
        _treeMenu.Items.Add("Connetti", null, (_, _) => ConnectSelected());
        _treeMenu.Items.Add("Apri con mstsc", null, (_, _) => { if (SelectedConnection is { } c) LaunchMstsc(c); });
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add("Nuova connessione…", null, (_, _) => NewConnection());
        _treeMenu.Items.Add("Nuova cartella…", null, (_, _) => NewFolder());
        _treeMenu.Items.Add("Modifica / Rinomina…", null, (_, _) => EditSelected());
        _treeMenu.Items.Add("Duplica", null, (_, _) => DuplicateSelected());
        _treeMenu.Items.Add("Elimina", null, (_, _) => DeleteSelected());
        _treeMenu.Opening += (_, _) =>
        {
            bool isConn = SelectedConnection is not null;
            _treeMenu.Items[0].Enabled = _treeMenu.Items[1].Enabled = _treeMenu.Items[6].Enabled = isConn;
            _treeMenu.Items[5].Enabled = _treeMenu.Items[7].Enabled = _tree.SelectedNode is not null;
        };

        _tabMenu.Items.Add("Riconnetti", null, (_, _) => CurrentView?.Connect());
        _tabMenu.Items.Add("Schermo intero", null, (_, _) => CurrentView?.EnterFullScreen());
        _tabMenu.Items.Add("Stacca in una finestra", null, (_, _) => { if (CurrentView is { } v) ToggleDetach(v); });
        _tabMenu.Items.Add("Apri con mstsc", null, (_, _) => CurrentView?.OpenWithMstsc());
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

        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Tag is RdpConnection c) OpenSession(c); };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.ContextMenuStrip = _treeMenu;
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { ConnectSelected(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F2) { EditSelected(); e.SuppressKeyPress = true; }
        };
        _tree.AfterCollapse += (_, e) => { if (e.Node?.Tag is FolderTag f && _search.TextLength == 0) _collapsed.Add(f.Path); };
        _tree.AfterExpand += (_, e) => { if (e.Node?.Tag is FolderTag f && _search.TextLength == 0) _collapsed.Remove(f.Path); };

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

        _tabs.SelectedIndexChanged += (_, _) => UpdateStatus();
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

    private RdpConnection? SelectedConnection => _tree.SelectedNode?.Tag as RdpConnection;

    private string SelectedFolderPath => _tree.SelectedNode?.Tag switch
    {
        FolderTag f => f.Path,
        RdpConnection c => c.Group,
        _ => "",
    };

    private void RefreshTree(object? select = null)
    {
        select ??= _tree.SelectedNode?.Tag;
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
            foreach (var g in _doc.AllGroups())
                FolderNodes(g);
        }
        TreeNode? toSelect = null;
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        foreach (var c in _doc.Connections.Where(c => c.Matches(query)).OrderBy(c => c.DisplayName, comparer))
        {
            var node = new TreeNode(c.DisplayName, ImgComputer, ImgComputer)
            {
                Tag = c,
                ToolTipText = $"{RdpFile.FormatAddress(c.Host, c.Port)}" + (c.Username.Length > 0 ? $"\nUtente: {(c.Domain.Length > 0 ? c.Domain + "\\" : "")}{c.Username}" : ""),
            };
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

    private void NewConnection()
    {
        var c = new RdpConnection { Group = SelectedFolderPath };
        using var dlg = new ConnectionDialog(c, _doc.AllGroups(), "Nuova connessione");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _doc.Connections.Add(c);
        SaveConnections();
        RefreshTree(c);
    }

    private void EditSelected()
    {
        switch (_tree.SelectedNode?.Tag)
        {
            case RdpConnection c:
                var edited = c.Clone();
                using (var dlg = new ConnectionDialog(edited, _doc.AllGroups(), $"Modifica: {c.DisplayName}"))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                }
                CopyInto(edited, c);
                SaveConnections();
                RefreshTree(c);
                UpdateSessionTitles();
                break;
            case FolderTag f:
                RenameFolder(f.Path);
                break;
        }
    }

    /// <summary>Copia i valori modificati nell'oggetto originale, che le sessioni aperte già referenziano.</summary>
    private static void CopyInto(RdpConnection from, RdpConnection to)
    {
        to.Name = from.Name; to.Host = from.Host; to.Port = from.Port;
        to.Username = from.Username; to.Domain = from.Domain;
        to.Password = from.Password; to.ProtectedPassword = from.ProtectedPassword;
        to.ResolutionMode = from.ResolutionMode; to.DesktopWidth = from.DesktopWidth; to.DesktopHeight = from.DesktopHeight;
        to.RedirectClipboard = from.RedirectClipboard; to.RedirectDrives = from.RedirectDrives;
        to.Group = from.Group;
    }

    private void DuplicateSelected()
    {
        if (SelectedConnection is not { } c)
            return;
        var copy = c.Duplicate();
        _doc.Connections.Add(copy);
        SaveConnections();
        RefreshTree(copy);
    }

    private void DeleteSelected()
    {
        switch (_tree.SelectedNode?.Tag)
        {
            case RdpConnection c:
                if (MessageBox.Show(this, $"Eliminare la connessione «{c.DisplayName}»?", "Elimina",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                _doc.Connections.Remove(c);
                SaveConnections();
                RefreshTree();
                break;
            case FolderTag f:
                DeleteFolder(f.Path);
                break;
        }
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

    // ---------- sessioni ----------

    internal RdpSessionView? CurrentView => _tabs.SelectedTab?.Tag as RdpSessionView;

    internal IEnumerable<RdpSessionView> AllViews =>
        _tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).Concat(_detached.Select(d => d.View));

    private void ConnectSelected()
    {
        if (SelectedConnection is { } c)
            OpenSession(c);
    }

    internal void OpenSession(RdpConnection c)
    {
        // Una sessione per connessione: se è già aperta la si porta in primo piano.
        var existing = AllViews.FirstOrDefault(v => v.Connection == c);
        if (existing is not null)
        {
            if (existing.IsDetached)
                _detached.First(d => d.View == existing).Activate();
            else
                _tabs.SelectedTab = (TabPage)existing.Parent!;
            if (existing.State == SessionState.Disconnected)
                existing.Connect();
            return;
        }

        var view = new RdpSessionView(c);
        view.StateChanged += (_, _) => UpdateSessionTab(view);
        view.CloseRequested += (_, _) => CloseSession(view);
        view.DetachToggleRequested += (_, _) => ToggleDetach(view);
        view.AttentionRequired += (_, _) => BringSessionToFront(view);
        ConfigureNewSession?.Invoke(view);
        AddTab(view);
        view.Connect();
    }

    private void AddTab(RdpSessionView view)
    {
        var page = new TabPage { Tag = view };
        page.Controls.Add(view);
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
            if (view.Parent is TabPage page)
                _tabs.SelectedTab = page;
            host = this;
        }
        LastAttentionResult = WindowAttention.BringToFront(host);
    }

    private void UpdateSessionTab(RdpSessionView view)
    {
        if (view.Parent is TabPage page)
        {
            page.Text = view.Connection.DisplayName + "   ";
            page.ToolTipText = $"{RdpFile.FormatAddress(view.Connection.Host, view.Connection.Port)}\n{view.StateText}";
            page.ImageIndex = (int)view.State;
        }
        UpdateStatus();
    }

    private void UpdateSessionTitles()
    {
        foreach (var v in AllViews)
            UpdateSessionTab(v);
    }

    internal void CloseSession(RdpSessionView view)
    {
        if (view.State == SessionState.Connected &&
            MessageBox.Show(this, $"Disconnettere «{view.Connection.DisplayName}» e chiudere la scheda?", "Chiudi sessione",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

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
    }

    internal void ToggleDetach(RdpSessionView view)
    {
        if (view.IsDetached)
        {
            ReattachFromForm(_detached.First(d => d.View == view), closeForm: true);
            return;
        }
        if (view.Parent is not TabPage page)
            return;
        page.Controls.Remove(view);
        _tabs.TabPages.Remove(page);
        page.Dispose();
        ShowHintIfEmpty();

        view.SetDetached(true);
        var form = new DetachedSessionForm(view, Icon);
        form.FormClosing += (_, e) =>
        {
            // Chiudere la finestra separata riporta la sessione in una scheda (non la disconnette).
            if (_detached.Contains(form) && e.CloseReason != CloseReason.ApplicationExitCall)
                ReattachFromForm(form, closeForm: false);
        };
        _detached.Add(form);
        form.Show();
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
        try { MstscLauncher.Launch(c); }
        catch (Exception ex) { MessageBox.Show(this, "Impossibile avviare mstsc.exe: " + ex.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

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
        // Con il fuoco dentro una sessione remota le altre combinazioni (Ctrl+N, F5, …) vanno al PC remoto.
        return Control.FromChildHandle(target) is RdpClientHost ? ShortcutRoute.ToRemote : ShortcutRoute.Normal;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        RouteShortcut(keyData, msg.HWnd) switch
        {
            ShortcutRoute.Handled => true,
            ShortcutRoute.ToRemote => false,
            _ => base.ProcessCmdKey(ref msg, keyData),
        };

    // Accesso per le prove automatiche.
    /// <summary>Chiamata su ogni nuova sessione prima della connessione (le prove la usano per impostare opzioni).</summary>
    internal Action<RdpSessionView>? ConfigureNewSession { get; set; }
    internal TabControl Tabs => _tabs;
    internal TreeView Tree => _tree;
    internal TextBox SearchBox => _search;
    internal ConnectionsDocument Document => _doc;
    internal IReadOnlyList<DetachedSessionForm> DetachedForms => _detached;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var views = AllViews.ToList();
        int connected = views.Count(v => v.State == SessionState.Connected);
        if (connected > 0 && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, $"Ci sono {connected} sessioni connesse. Disconnetterle e uscire?", "Esci",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        foreach (var form in _detached.ToList())
        {
            _detached.Remove(form);
            form.ReleaseView();
            form.Close();
        }
        foreach (var v in views)
            v.Dispose();
    }

    // ---------- varie ----------

    private void UpdateStatus()
    {
        int sessions = _tabs.TabCount + _detached.Count;
        int connected = AllViews.Count(v => v.State == SessionState.Connected);
        _status.Text = $"{_doc.Connections.Count} connessioni salvate · {sessions} sessioni aperte ({connected} connesse)";
        Text = CurrentView is { } v ? $"{v.Connection.DisplayName} - RdpManager {AppInfo.VersionText}" : DefaultTitle;
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
            _updateLabel.Text = $"È disponibile la versione {release.Version.ToString(3)} — clic per aprire la pagina";
            _updateLabel.ToolTipText = release.PageUrl;
            _updateLabel.Visible = true;
        }
        if (!manual)
            return;
        switch (result.Status)
        {
            case UpdateCheckStatus.NewerAvailable:
                if (MessageBox.Show(this, $"È disponibile RdpManager {result.Release!.Version.ToString(3)} (in uso: {AppInfo.VersionText}).\n\nAprire la pagina della release per scaricare il Setup?",
                        "Aggiornamenti", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenUrl(result.Release.PageUrl);
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

    private void BuildImages()
    {
        // Indici = valori di SessionState: Idle, Connecting, Connected, Disconnected.
        foreach (var color in new[] { Color.Gray, Color.Orange, Color.LimeGreen, Color.Red })
        {
            var bmp = new Bitmap(12, 12);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
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

        var computer = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(computer))
        {
            using var frame = new SolidBrush(Color.FromArgb(60, 60, 70));
            using var screen = new SolidBrush(Color.FromArgb(70, 140, 220));
            g.FillRectangle(frame, 1, 2, 14, 10);
            g.FillRectangle(screen, 2, 3, 12, 8);
            g.FillRectangle(frame, 6, 12, 4, 2);
            g.FillRectangle(frame, 4, 14, 8, 1);
        }
        _treeImages.Images.Add(computer);
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
