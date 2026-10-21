using System.Text;
using RdpManager.Core;

namespace RdpManager.UI;

// Funzioni 1.4: blocco con PIN, spazi di lavoro, selezione multipla con azioni di gruppo, importazione CSV, F1.
public sealed partial class MainForm
{
    private void InitV14()
    {
        _idleTimer.Tick += (_, _) => CheckIdleLock();
        _idleTimer.Start();
        FormClosed += (_, _) => _idleTimer.Stop();
        _tree.NodeMouseClick += OnTreeMultiClick;
        _treeMenu.Opening += (_, _) => UpdateGroupMenu();
        BuildGroupMenu();
        // Avvio con il PIN impostato: la finestra resta nascosta finché non si sblocca.
        if (_locked)
            Shown += (_, _) => BeginInvoke(() => { Hide(); ShowLockForm(); });
    }

    // ---------- blocco con PIN ----------

    private bool _locked;
    private bool _reopenAfterUnlock;
    private LockForm? _lockForm;
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 15_000 };

    internal bool IsLocked => _locked;
    internal LockForm? LockScreen => _lockForm is { IsDisposed: false } f ? f : null;

    /// <summary>Per le prove: tempo di inattività da usare al posto di quello vero di Windows.</summary>
    internal Func<TimeSpan>? IdleOverride { get; set; }

    internal void CheckIdleLock()
    {
        if (_locked || !_doc.Settings.LockEnabled)
            return;
        // Con un dialogo aperto (finestra principale disabilitata) si aspetta: nasconderla lascerebbe il dialogo visibile.
        if (IsHandleCreated && !NativeMethods.IsWindowEnabled(Handle))
            return;
        var idle = IdleOverride?.Invoke() ?? NativeMethods.IdleTime();
        if (LockPolicy.ShouldLock(_doc.Settings, idle))
            LockApp();
    }

    /// <summary>
    /// Blocca l'app: password in memoria di nuovo cifrate, finestre nascoste (le sessioni restano connesse ma non si
    /// vedono), schermata del PIN. Senza PIN impostato propone di impostarlo.
    /// </summary>
    internal void LockApp()
    {
        if (_locked)
            return;
        if (!_doc.Settings.LockEnabled)
        {
            if (MessageBox.Show(this, "Per bloccare RdpManager serve un PIN. Impostarlo ora?", "Blocco",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                SetPinFromMenu();
            return;
        }
        _locked = true;
        Overview?.Close();
        UpdateOffer?.Hide();
        _doc.SealPasswords();
        foreach (var d in _detached)
            d.Hide();
        Hide();
        ShowLockForm();
        AppLog.Default?.Info("RdpManager bloccato");
    }

    private void ShowLockForm()
    {
        if (LockScreen is { } open)
        {
            open.Activate();
            return;
        }
        var screen = Screen.FromRectangle(WindowState == FormWindowState.Normal ? Bounds : RestoreBounds).Bounds;
        var form = new LockForm(screen, TryUnlock) { Icon = Icon };
        form.ForgotRequested += (_, _) => ForgotPin(form);
        _lockForm = form;
        form.Show();
        form.Activate();
    }

    private bool TryUnlock(string pin)
    {
        if (!PinLock.Verify(pin, _doc.Settings.LockPin))
        {
            AppLog.Default?.Warning("PIN sbagliato alla schermata di blocco");
            return false;
        }
        Unlock();
        return true;
    }

    private void Unlock()
    {
        _locked = false;
        _doc.UnsealPasswords();
        if (LockScreen is { } form)
        {
            form.AllowClose = true;
            form.Close();
            BeginInvoke(form.Dispose);
        }
        _lockForm = null;
        Show();
        foreach (var d in _detached)
            d.Show();
        WindowAttention.BringToFront(this);
        AppLog.Default?.Info("RdpManager sbloccato");
        if (_reopenAfterUnlock)
        {
            _reopenAfterUnlock = false;
            ReopenSessions();
        }
        UpdateTreeStates();
    }

    /// <summary>PIN dimenticato: dopo la conferma le connessioni restano, PIN e password salvate si cancellano.</summary>
    private void ForgotPin(IWin32Window owner)
    {
        int passwords = _doc.Connections.Count(c => c.HasPassword) + _doc.CredentialProfiles.Count(p => p.HasPassword);
        if (MessageBox.Show(owner,
                $"Il PIN viene tolto e le {passwords} password salvate vengono cancellate: le connessioni restano, ma le " +
                "password andranno reinserite.\n\nContinuare?", "PIN dimenticato",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        ForgetPinConfirmed();
    }

    /// <summary>PIN dimenticato, già confermato (anche per le prove automatiche).</summary>
    internal void ForgetPinConfirmed()
    {
        int n = _doc.ForgetPinAndPasswords();
        SaveConnections();
        AppLog.Default?.Warning($"PIN dimenticato: tolto il PIN e cancellate {n} password salvate");
        Unlock();
        ShowNotice($"PIN tolto: {n} password salvate cancellate, le connessioni sono rimaste.");
    }

    private void SetPinFromMenu()
    {
        using var dlg = new PinSetDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is not { } hash)
            return;
        _doc.Settings.LockPin = hash;
        SaveConnections();
        ShowNotice($"PIN impostato: {_shortcuts[ShortcutAction.Lock]} blocca RdpManager" +
                   (_doc.Settings.LockAfterMinutes > 0 ? $", e si blocca da solo dopo {_doc.Settings.LockAfterMinutes} minuti di inattività." : "."));
    }

    // ---------- spazi di lavoro ----------

    /// <summary>Id delle scorciatoie degli spazi di lavoro registrate con RegisterHotKey: 0x300 + posizione.</summary>
    private const int WorkspaceHotkeyBase = 0x300;

    private void FillWorkspacesMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        menu.DropDownItems.Add("&Salva lo spazio attuale…", null, (_, _) => SaveCurrentWorkspace());
        menu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var w in _doc.Workspaces)
        {
            var ws = w;
            var item = new ToolStripMenuItem($"{w.Name.Replace("&", "&&")}  ({w.Connections.Count})", null, (_, _) => OpenWorkspace(ws))
            {
                ShortcutKeyDisplayString = w.Shortcut,
            };
            menu.DropDownItems.Add(item);
        }
        if (_doc.Workspaces.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("(nessuno spazio salvato)") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("&Gestisci…", null, (_, _) => ManageWorkspaces());
    }

    /// <summary>Layout attuale della finestra per salvarlo in uno spazio di lavoro.</summary>
    internal WorkspaceLayout CurrentLayout() => new(
        AllViews.Select(v => v.Connection.Id).ToList(), CurrentView?.Connection.Id, Screens.Of(this)?.DeviceName, _fullScreen,
        SplitViews.Left?.Connection.Id, SplitViews.Right?.Connection.Id, _doc.Settings.SplitRatio);

    private void SaveCurrentWorkspace()
    {
        if (!AllViews.Any())
        {
            ShowNotice("Apri prima le sessioni da mettere nello spazio di lavoro.");
            return;
        }
        var name = TextPromptDialog.Ask(this, "Salva lo spazio di lavoro", "Nome (se esiste già viene aggiornato):",
            _lastWorkspace?.Name ?? $"Spazio {_doc.Workspaces.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var ws = _doc.SaveWorkspace(name, CurrentLayout());
        _lastWorkspace = ws;
        SaveConnections();
        RegisterSessionHotkeys();
        ShowNotice($"Spazio di lavoro «{ws.Name}» salvato con {ws.Connections.Count} sessioni.");
    }

    private Workspace? _lastWorkspace;

    /// <summary>Riapre lo spazio: sessioni nell'ordine salvato, monitor, schermo intero, vista affiancata, scheda scelta.</summary>
    internal void OpenWorkspace(Workspace ws)
    {
        if (_locked)
            return;
        var plan = _doc.PlanWorkspace(ws);
        _lastWorkspace = ws;
        Overview?.Close();
        if (plan.Open.Count == 0)
        {
            ShowNotice($"Lo spazio «{ws.Name}» non ha più connessioni.");
            return;
        }
        if (IsSplit)
            LeaveSplit();
        if (plan.Monitor is { } monitor && MonitorLayout.ChooseScreen(Screens.All(), monitor) is { } target
            && target.DeviceName.Equals(monitor, StringComparison.OrdinalIgnoreCase))
        {
            _doc.Settings.PreferredMonitor = target.DeviceName;
            if (!_fullScreen && Screens.Of(this)?.DeviceName != target.DeviceName)
                Screens.MoveForm(this, target);
        }
        foreach (var c in plan.Open)
            OpenSession(c);
        if (plan.FullScreen != _fullScreen)
            ToggleFullScreen();
        else if (_fullScreen)
            UpdateFullScreenBounds();
        if (plan.SplitLeft is { } l && plan.SplitRight is { } r
            && AllViews.FirstOrDefault(v => v.Connection == l) is { } lv && AllViews.FirstOrDefault(v => v.Connection == r) is { } rv)
        {
            _doc.Settings.SplitRatio = plan.SplitRatio;
            EnterSplit(lv, rv);
        }
        else if (plan.Selected is { } s && AllViews.FirstOrDefault(v => v.Connection == s) is { } sv)
            SelectView(sv);
        SaveConnections();
        ShowNotice($"Spazio di lavoro «{ws.Name}» aperto" + (plan.Missing > 0 ? $" ({plan.Missing} connessioni non esistono più)" : "") + ".");
        AppLog.Default?.Info($"Spazio di lavoro aperto: {plan.Open.Count} sessioni");
    }

    private void ManageWorkspaces()
    {
        using var dlg = new WorkspacesDialog(_doc);
        UnregisterSessionHotkeys(); // le combinazioni non devono scattare mentre si scrivono
        try
        {
            dlg.ShowDialog(this);
        }
        finally
        {
            RegisterSessionHotkeys();
        }
        if (dlg.Changed)
        {
            SaveConnections();
            RegisterSessionHotkeys();
        }
    }

    /// <summary>Registra le scorciatoie degli spazi di lavoro (insieme a quelle delle sessioni).</summary>
    private void RegisterWorkspaceHotkeys(List<string> failed)
    {
        for (int i = 0; i < _doc.Workspaces.Count; i++)
        {
            var w = _doc.Workspaces[i];
            if (w.Shortcut.Length == 0 || !GlobalHotkey.TryParse(w.Shortcut, out var combo))
                continue;
            var (mods, vk) = GlobalHotkey.ToNative(combo);
            int id = WorkspaceHotkeyBase + i;
            if (NativeMethods.RegisterHotKey(Handle, id, mods, vk))
                _sessionHotkeys.Add(id);
            else
                failed.Add(combo.ToString());
        }
    }

    /// <summary>WM_HOTKEY di uno spazio di lavoro; falso se l'id non è suo.</summary>
    private bool HandleWorkspaceHotkey(int id)
    {
        int index = id - WorkspaceHotkeyBase;
        if (index < 0 || index >= _doc.Workspaces.Count)
            return false;
        if (NativeMethods.IsWindowEnabled(Handle) && !_locked)
            OpenWorkspace(_doc.Workspaces[index]);
        return true;
    }

    // ---------- selezione multipla e azioni di gruppo ----------

    private readonly HashSet<Guid> _multi = [];
    private RdpConnection? _anchor;

    /// <summary>Connessioni selezionate: quelle della selezione multipla, altrimenti quella sotto il cursore.</summary>
    internal IReadOnlyList<RdpConnection> SelectedConnections =>
        _multi.Count > 1 ? GroupActions.Resolve(_doc, _multi) : SelectedConnection is { } c ? [c] : [];

    internal IReadOnlyCollection<Guid> MultiSelection => _multi;

    /// <summary>Ctrl+clic aggiunge o toglie, Maiusc+clic seleziona l'intervallo, il clic semplice ricomincia.</summary>
    private void OnTreeMultiClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        var c = ConnectionOf(e.Node);
        SelectForTests(c, ModifierKeys);
    }

    /// <summary>La logica del clic (anche per le prove automatiche).</summary>
    internal void SelectForTests(RdpConnection? c, Keys modifiers)
    {
        if (c is null)
        {
            _multi.Clear();
        }
        else if ((modifiers & Keys.Control) != 0)
        {
            if (_multi.Count == 0 && _anchor is { } a && a != c)
                _multi.Add(a.Id);
            if (!_multi.Add(c.Id))
                _multi.Remove(c.Id);
            _anchor = c;
        }
        else if ((modifiers & Keys.Shift) != 0 && _anchor is { } from)
        {
            var visible = VisibleConnections();
            int i = visible.IndexOf(from), j = visible.IndexOf(c);
            if (i >= 0 && j >= 0)
            {
                _multi.Clear();
                foreach (var x in visible.Skip(Math.Min(i, j)).Take(Math.Abs(i - j) + 1))
                    _multi.Add(x.Id);
            }
        }
        else
        {
            _multi.Clear();
            _anchor = c;
        }
        ApplyMultiSelectionColors();
    }

    /// <summary>Connessioni nell'ordine in cui si vedono nell'elenco (senza le sezioni Preferiti e Recenti).</summary>
    private List<RdpConnection> VisibleConnections()
    {
        var list = new List<RdpConnection>();
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is RdpConnection c)
                    list.Add(c);
                if (n.Tag is not SectionTag)
                    Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        return list;
    }

    /// <summary>Colora i nodi della selezione multipla (dopo ogni ricostruzione dell'elenco).</summary>
    private void ApplyMultiSelectionColors()
    {
        _multi.RemoveWhere(id => _doc.Find(id) is null);
        var p = ThemeManager.Current;
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                bool on = _multi.Count > 1 && ConnectionOf(n) is { } c && _multi.Contains(c.Id);
                n.BackColor = on ? p.Highlight : Color.Empty;
                n.ForeColor = on ? p.HighlightText : Color.Empty;
                Walk(n.Nodes);
            }
        }
        _tree.BeginUpdate();
        Walk(_tree.Nodes);
        _tree.EndUpdate();
        if (_multi.Count > 1)
            ShowNotice($"{_multi.Count} connessioni selezionate: clic destro per le azioni di gruppo, Canc per eliminarle, Invio per connetterle.");
    }

    private readonly List<ToolStripItem> _groupItems = [];

    private void BuildGroupMenu()
    {
        ToolStripMenuItem Add(string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick) { Visible = false };
            _groupItems.Add(item);
            return item;
        }
        var items = new ToolStripItem[]
        {
            Add("Connetti le selezionate", (_, _) => ConnectGroup()),
            Add("Sposta le selezionate in una cartella…", (_, _) => MoveGroup()),
            Add("Assegna un profilo di credenziali…", (_, _) => AssignGroupProfile()),
            Add("Elimina le selezionate…", (_, _) => DeleteGroup()),
        };
        var separator = new ToolStripSeparator { Visible = false };
        _groupItems.Add(separator);
        _treeMenu.Items.Insert(0, separator);
        for (int i = items.Length - 1; i >= 0; i--)
            _treeMenu.Items.Insert(0, items[i]);
    }

    /// <summary>Con più connessioni selezionate il menu mostra solo le azioni di gruppo.</summary>
    private void UpdateGroupMenu()
    {
        bool group = _multi.Count > 1;
        bool isFolder = _tree.SelectedNode?.Tag is FolderTag;
        foreach (ToolStripItem item in _treeMenu.Items)
        {
            if (_groupItems.Contains(item))
                item.Visible = group;
            else
                // Fuori dalla selezione multipla: tutte le voci singole, «Connetti tutta la cartella» solo sulle cartelle.
                item.Visible = !group && (item != _miConnectFolder || isFolder);
        }
        if (group)
        {
            _groupItems[0].Text = $"Connetti le {_multi.Count} selezionate";
            _groupItems[3].Text = $"Elimina le {_multi.Count} selezionate…";
        }
    }

    internal void ConnectGroup()
    {
        var list = SelectedConnections;
        if (list.Count > 6 && MessageBox.Show(this, $"Aprire {list.Count} sessioni insieme?", "Connetti",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var c in list)
            OpenSession(c);
    }

    private void MoveGroup()
    {
        using var dlg = new ChoiceDialog("Sposta in una cartella", $"Cartella per le {_multi.Count} connessioni (anche nuova, es. Clienti/Rossi; vuoto = radice):",
            _doc.AllGroups(), SelectedFolderPath, editable: true);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        int n = GroupActions.MoveToFolder(_doc, _multi, dlg.Value);
        SaveConnections();
        RefreshTree();
        ShowNotice($"{n} connessioni spostate in «{(dlg.Value.Length > 0 ? dlg.Value : "radice")}».");
    }

    private void AssignGroupProfile()
    {
        var profiles = _doc.CredentialProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var choices = new[] { "Credenziali proprie (nessun profilo)" }.Concat(profiles.Select(p => p.DisplayName)).ToList();
        using var dlg = new ChoiceDialog("Assegna un profilo", $"Credenziali per le {_multi.Count} connessioni:", choices, choices[0], editable: false);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        Guid? profile = dlg.SelectedIndex > 0 ? profiles[dlg.SelectedIndex - 1].Id : null;
        int n = GroupActions.AssignProfile(_doc, _multi, profile);
        SaveConnections();
        UpdateTreeStates();
        ShowNotice($"Profilo assegnato a {n} connessioni.");
    }

    /// <summary>Elimina le connessioni selezionate dopo la conferma (le sessioni aperte restano finché non si chiudono).</summary>
    internal void DeleteGroup(Func<string, bool>? confirm = null)
    {
        var list = SelectedConnections;
        if (list.Count == 0)
            return;
        var question = $"Eliminare {GroupActions.DescribeForConfirmation(list)}?\n\nLe sessioni già aperte restano finché non le chiudi.";
        bool ok = confirm?.Invoke(question)
                  ?? MessageBox.Show(this, question, "Elimina", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (!ok)
            return;
        int n = GroupActions.Delete(_doc, list.Select(c => c.Id));
        _multi.Clear();
        SaveConnections();
        RefreshTree();
        ShowNotice($"{n} connessioni eliminate.");
    }

    // ---------- importazione da CSV ----------

    private void ImportCsv()
    {
        using var open = new OpenFileDialog { Title = "Importa connessioni da CSV", Filter = "File CSV (*.csv;*.txt)|*.csv;*.txt|Tutti i file (*.*)|*.*" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        CsvTable table;
        try
        {
            table = CsvImport.Parse(ReadText(open.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Impossibile leggere il file: " + ex.Message, "Importa CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (table.Header.Count == 0 || table.Rows.Count == 0)
        {
            MessageBox.Show(this, "Il file non ha righe da importare (serve un'intestazione e almeno una riga).", "Importa CSV",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new CsvImportDialog(Path.GetFileName(open.FileName), table, _doc, SelectedFolderPath);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        var added = CsvImport.ImportInto(_doc, dlg.Rows);
        SaveConnections();
        RefreshTree(added.LastOrDefault());
        ShowNotice($"Importate {added.Count} connessioni dal CSV.");
        AppLog.Default?.Info($"Importazione CSV: {added.Count} connessioni");
        if (_backgroundChecks && added.Count > 0)
            _ = CheckReachabilityAsync(added);
    }

    /// <summary>UTF-8 (con o senza BOM); se non è UTF-8 valido, come lo salva Excel in italiano (Latin-1).</summary>
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---------- F1 ----------

    internal IReadOnlyList<ShortcutLine> ShortcutLines() =>
        ShortcutSummary.Build(_shortcuts, _doc.Settings.GlobalHotkey, _doc.Workspaces);

    private void ShowShortcutsDialog()
    {
        using var dlg = new ShortcutsDialog(ShortcutLines());
        dlg.ShowDialog(this);
    }
}
