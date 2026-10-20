using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove della finestra principale 1.2: Panoramica, ricerca rapida, vista affiancata, cronologia, tema scuro,
/// testi rapidi, profili di credenziali, area di notifica e scorciatoia globale. Come le altre prove UI usano host
/// irraggiungibili e un archivio temporaneo; lo stato «connessa» è simulato.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV12Tests : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi12-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(ConnectionsDocument doc)
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        return store;
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections) =>
        StoreWith(new ConnectionsDocument { Connections = connections.ToList() });

    private static RdpConnection Unreachable(string name, string label = "") =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd", Label = label };

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void WithMainForm(ConnectionStore store, Action<MainForm> body, bool systemIntegration = false) => OnStaThread(() =>
    {
        using var form = new MainForm(store, systemIntegration: systemIntegration)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(0, 0),
        };
        form.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        body(form);
        form.Overview?.Close();
        if (form.IsSplit)
            form.LeaveSplit();
        foreach (var v in form.AllViews.Where(v => v.State == SessionState.Connected))
            v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
        form.ExitApp();
        Pump(TimeSpan.FromMilliseconds(200));
    });

    private static void Pump(TimeSpan duration) => PumpUntil(() => false, duration);

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                return false;
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return true;
    }

    private static List<RdpSessionView> OpenAll(MainForm form)
    {
        foreach (var c in form.Document.Connections)
            form.OpenSession(c);
        Pump(TimeSpan.FromMilliseconds(200));
        return form.Tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).ToList();
    }

    // ---------- Panoramica ----------

    [Fact]
    public void Overview_ShowsOnlyConnectedSessions_AndAClickGoesThere()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            var views = OpenAll(form);
            views[0].SetStateForTests(SessionState.Connected, "Connessa");
            views[2].SetStateForTests(SessionState.Connected, "Connessa");
            form.Tabs.SelectedIndex = 1;

            form.ExecuteShortcut(ShortcutAction.Overview);
            Assert.True(PumpUntil(() => form.Overview is { Visible: true }, TimeSpan.FromSeconds(3)));
            var overview = form.Overview!;
            Assert.Equal([views[0], views[2]], overview.Tiles.Select(t => t.View));
            Assert.Equal(2, overview.Cells.Count);
            Assert.Equal(form.Bounds, overview.Bounds);
            // Giro di miniature: la scheda si sceglie per un attimo sotto la panoramica, poi torna quella di prima.
            Pump(TimeSpan.FromMilliseconds(900));
            Assert.True(PumpUntil(() => !form.OverviewBusy, TimeSpan.FromSeconds(3)));
            Assert.Equal(1, form.Tabs.SelectedIndex);

            overview.Choose(1);
            Assert.True(PumpUntil(() => form.Overview is null, TimeSpan.FromSeconds(3)));
            Assert.Same(views[2], form.CurrentView);

            // Ctrl+Alt+G apre e richiude.
            form.ExecuteShortcut(ShortcutAction.Overview);
            Assert.True(PumpUntil(() => form.Overview is not null, TimeSpan.FromSeconds(3)));
            form.ExecuteShortcut(ShortcutAction.Overview);
            Assert.True(PumpUntil(() => form.Overview is null, TimeSpan.FromSeconds(3)));
        });
    }

    // ---------- ricerca rapida ----------

    [Fact]
    public void QuickSearch_FindsWithATypo_OpensANewSessionOrSwitchesToTheOpenOne()
    {
        WithMainForm(StoreWith(Unreachable("Magazzino"), Unreachable("Ufficio", "PROD"), Unreachable("Laboratorio")), form =>
        {
            form.QuickSearchShown = dlg =>
            {
                dlg.QueryBox.Text = "magazzno"; // lettera mancante
                Pump(TimeSpan.FromMilliseconds(50));
                Assert.Equal("Magazzino", dlg.Results[0].Connection.Name);
                dlg.Accept();
            };
            form.ExecuteShortcut(ShortcutAction.QuickSearch);
            Assert.Equal("Magazzino", form.CurrentView!.Connection.Name);
            Assert.Equal(1, form.Tabs.TabCount);

            form.OpenSession(form.Document.Connections.Single(c => c.Name == "Laboratorio"));
            form.QuickSearchShown = dlg =>
            {
                dlg.QueryBox.Text = "magaz";
                Pump(TimeSpan.FromMilliseconds(50));
                Assert.Equal(QuickSearchKind.OpenSession, dlg.Results[0].Kind);
                dlg.Accept();
            };
            form.ExecuteShortcut(ShortcutAction.QuickSearch);
            Assert.Equal("Magazzino", form.CurrentView!.Connection.Name);
            Assert.Equal(2, form.Tabs.TabCount); // nessuna sessione doppia

            form.QuickSearchShown = dlg =>
            {
                dlg.QueryBox.Text = "prod"; // etichetta
                Pump(TimeSpan.FromMilliseconds(50));
                dlg.Accept();
            };
            form.ExecuteShortcut(ShortcutAction.QuickSearch);
            Assert.Equal("Ufficio", form.CurrentView!.Connection.Name);
        });
    }

    // ---------- vista affiancata ----------

    [Fact]
    public void SplitView_ShowsTwoSessions_WithoutRecreatingTheRdpControls()
    {
        WithMainForm(StoreWith(Unreachable("Sinistra"), Unreachable("Destra"), Unreachable("Terza")), form =>
        {
            var views = OpenAll(form);
            views[1].SetStateForTests(SessionState.Connected, "Connessa");
            form.Tabs.SelectedIndex = 0;
            var clients = views.Select(v => v.Client).ToList();

            form.ExecuteShortcut(ShortcutAction.ToggleSplitView);
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(form.IsSplit);
            Assert.True(form.SplitControl.Visible);
            Assert.Same(views[0], form.SplitViews.Left);
            Assert.Same(views[1], form.SplitViews.Right); // la successiva connessa
            Assert.Same(form.SplitControl.Panel1, views[0].Parent);
            Assert.Same(form.SplitControl.Panel2, views[1].Parent);
            Assert.True(views[0].Visible && views[1].Visible);
            int expected = SplitLayout.SplitterDistance(form.SplitControl.Width, form.Document.Settings.SplitRatio, form.SplitControl.SplitterWidth);
            Assert.InRange(form.SplitControl.SplitterDistance, expected - 2, expected + 2);

            // Andare a una terza sessione chiude la vista affiancata; le sessioni tornano nelle loro schede.
            form.OpenSession(views[2].Connection);
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.False(form.IsSplit);
            Assert.All(views, v => Assert.IsType<TabPage>(v.Parent));
            Assert.Same(views[2], form.CurrentView);
            // Le due sessioni affiancate hanno ancora gli stessi controlli ActiveX (la terza, disconnessa, è stata
            // riconnessa da OpenSession e ha un controllo nuovo: è voluto).
            Assert.Same(clients[0], views[0].Client);
            Assert.Same(clients[1], views[1].Client);

            // Chiudere una sessione affiancata riporta alle schede senza perdere l'altra.
            form.EnterSplit(views[0], views[2]);
            Assert.True(form.IsSplit);
            form.CloseSession(views[2]);
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.False(form.IsSplit);
            Assert.Equal(2, form.Tabs.TabCount);
            Assert.IsType<TabPage>(views[0].Parent);
        });
    }

    [Fact]
    public void SplitView_ANewSessionOrCtrlTabGoesBackToTheTabs_AndTheLastUsedHalfIsCurrent()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            var conns = form.Document.Connections;
            form.OpenSession(conns[0]);
            form.OpenSession(conns[1]);
            Pump(TimeSpan.FromMilliseconds(200));
            var views = form.AllViews.ToList();

            form.EnterSplit(views[0], views[1]);
            Assert.Same(views[0], form.CurrentView);       // all'inizio la metà sinistra
            form.ExecuteShortcut(ShortcutAction.NextSession);
            Pump(TimeSpan.FromMilliseconds(300));
            // Il fuoco è andato a destra (se Windows lo concede); in ogni caso CurrentView è una delle due metà.
            Assert.Contains(form.CurrentView, new[] { views[0], views[1] });

            form.OpenSession(conns[2]); // sessione nuova: non deve finire in una scheda nascosta
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.False(form.IsSplit);
            Assert.Equal("Tre", form.CurrentView!.Connection.Name);
            Assert.True(form.CurrentView.Visible);

            form.EnterSplit(views[0], views[1]);
            form.CycleTab(1);
            Assert.False(form.IsSplit);
            Assert.All(form.AllViews, v => Assert.IsType<TabPage>(v.Parent));
        });
    }

    [Fact]
    public void Overview_WhileSplit_RestoresTheSplitAfterCapturing()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            var views = OpenAll(form);
            foreach (var v in views)
                v.SetStateForTests(SessionState.Connected, "Connessa");
            form.EnterSplit(views[0], views[1]);
            form.ExecuteShortcut(ShortcutAction.Overview);
            Assert.True(PumpUntil(() => form.Overview is not null, TimeSpan.FromSeconds(3)));
            Pump(TimeSpan.FromMilliseconds(400));
            form.Overview!.Close(); // chiusa anche a metà di una cattura
            Assert.True(PumpUntil(() => form.Overview is null && !form.OverviewBusy, TimeSpan.FromSeconds(3)));
            Assert.True(form.IsSplit);
            Assert.True(form.SplitControl.Visible);
            Assert.Same(form.SplitControl.Panel1, views[0].Parent);
        });
    }

    [Fact]
    public void SplitView_NeedsTwoSessions()
    {
        WithMainForm(StoreWith(Unreachable("Sola")), form =>
        {
            OpenAll(form);
            form.ExecuteShortcut(ShortcutAction.ToggleSplitView);
            Assert.False(form.IsSplit);
        });
    }

    // ---------- cronologia ----------

    [Fact]
    public void History_RecordsStartEndAndReason_InHistoryJson()
    {
        WithMainForm(StoreWith(Unreachable("Server")), form =>
        {
            var view = OpenAll(form)[0];
            view.SetStateForTests(SessionState.Connected, "Connessa");
            var entry = Assert.Single(form.History.Entries);
            Assert.True(entry.IsOpen);
            Assert.Equal("Server", entry.Name);

            view.SetStateForTests(SessionState.Disconnected, "Disconnessa: rete");
            Assert.False(entry.IsOpen);
            Assert.Equal("rete", entry.Reason);

            var path = Path.Combine(_dir, HistoryStore.FileName);
            Assert.True(File.Exists(path));
            var saved = new HistoryStore(path).Load();
            Assert.Single(saved.Entries);
            Assert.NotNull(saved.Entries[0].EndUtc);

            // Una sessione connessa chiusa dall'app viene chiusa anche nella cronologia.
            view.SetStateForTests(SessionState.Connected, "Connessa");
            view.SetStateForTests(SessionState.Idle, "Pronta");
            Assert.Equal(2, form.History.Entries.Count);
            Assert.All(form.History.Entries, e => Assert.False(e.IsOpen));
        });
    }

    [Fact]
    public void History_CanBeTurnedOff()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Server")] };
        doc.Settings.HistoryEnabled = false;
        WithMainForm(StoreWith(doc), form =>
        {
            var view = OpenAll(form)[0];
            view.SetStateForTests(SessionState.Connected, "Connessa");
            view.SetStateForTests(SessionState.Disconnected, "Disconnessa");
            Assert.Empty(form.History.Entries);
        });
    }

    // ---------- tema scuro ----------

    [Fact]
    public void DarkTheme_ColorsTheMainWindow_AndLightRestores()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")] };
        doc.Settings.Theme = AppTheme.Dark;
        WithMainForm(StoreWith(doc), form =>
        {
            OpenAll(form);
            Assert.True(ThemeManager.Current.IsDark);
            Assert.Equal(ThemeManager.Dark.Window.ToArgb(), form.Tree.BackColor.ToArgb());
            Assert.Equal(ThemeManager.Dark.Input.ToArgb(), form.SearchBox.BackColor.ToArgb());
            Assert.Equal(TabDrawMode.OwnerDrawFixed, form.Tabs.DrawMode);
            Assert.Equal(ThemeManager.Dark.Text.ToArgb(), form.MainMenuStrip!.ForeColor.ToArgb());

            form.Document.Settings.Theme = AppTheme.Light;
            form.ApplyThemeForTests();
            Assert.False(ThemeManager.Current.IsDark);
            Assert.Equal(SystemColors.Window.ToArgb(), form.Tree.BackColor.ToArgb());
            Assert.Equal(TabDrawMode.Normal, form.Tabs.DrawMode);
        });
    }

    // ---------- testi rapidi e profili ----------

    [Fact]
    public void QuickTexts_AreOfferedInTheSessionMenu()
    {
        var doc = new ConnectionsDocument
        {
            Connections = [Unreachable("Uno")],
            QuickTexts = [new QuickText { Name = "Indirizzo IP", Text = "ipconfig /all\n" }, new QuickText { Text = "exit" }],
        };
        WithMainForm(StoreWith(doc), form =>
        {
            var view = OpenAll(form)[0];
            var menu = new ToolStripMenuItem();
            view.FillQuickTextsMenu(menu);
            var names = menu.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
            Assert.Equal(["Indirizzo IP", "exit", "Gestisci testi rapidi…"], names);
            // Senza sessione connessa i testi non si possono digitare.
            Assert.False(menu.DropDownItems[0].Enabled);
            view.SetStateForTests(SessionState.Connected, "Connessa");
            view.FillQuickTextsMenu(menu);
            Assert.True(menu.DropDownItems[0].Enabled);
        });
    }

    [Fact]
    public void Sessions_ConnectWithTheProfileCredentials()
    {
        var profile = new CredentialProfile { Name = "Admin", Username = "admin", Domain = "UFFICIO", Password = "Profilo-1!" };
        var c = Unreachable("Server");
        c.CredentialProfileId = profile.Id;
        c.UseGateway = true;
        c.GatewayHost = "gw.example.test";
        c.GatewayCredentials = GatewayCredentialsSource.Profile;
        c.GatewayProfileId = profile.Id;
        WithMainForm(StoreWith(new ConnectionsDocument { Connections = [c], CredentialProfiles = [profile] }), form =>
        {
            var view = OpenAll(form)[0];
            var effective = view.ResolveForConnect!(view.Connection);
            Assert.Equal(("admin", "UFFICIO", "Profilo-1!"), (effective.Username, effective.Domain, effective.Password));
            Assert.Equal("utente", view.Connection.Username); // la connessione salvata non cambia
            Assert.Equal("admin", view.ResolveGateway!(view.Connection)!.Username);
        });
    }

    // ---------- area di notifica e scorciatoia globale ----------

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void Tray_MenuListsConnections_GlobalHotkeyRestoresTheWindow()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno"), Unreachable("Due")] };
        doc.Connections[0].Group = "Clienti";
        doc.Connections[1].IsFavorite = true;
        // Combinazione insolita, per non scontrarsi con un RdpManager vero aperto sullo stesso PC.
        doc.Settings.GlobalHotkey = "Ctrl+Alt+Maiusc+F9";
        WithMainForm(StoreWith(doc), form =>
        {
            Assert.NotNull(form.TrayIcon);
            Assert.True(form.TrayIcon!.Visible);
            form.FillTrayMenu();
            var top = form.TrayMenu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
            Assert.Contains("Preferiti", top);
            Assert.Contains("Tutte le connessioni", top);
            var all = form.TrayMenu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Tutte le connessioni");
            var clienti = all.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Clienti");
            Assert.Equal("Uno", Assert.Single(clienti.DropDownItems.OfType<ToolStripMenuItem>()).Text);

            Assert.True(form.GlobalHotkeyRegistered, "RegisterHotKey non riuscita");
            form.WindowState = FormWindowState.Minimized;
            Pump(TimeSpan.FromMilliseconds(200));
            PostMessage(form.Handle, NativeMethods.WM_HOTKEY, (IntPtr)MainForm.HotkeyId, IntPtr.Zero);
            Assert.True(PumpUntil(() => form.WindowState != FormWindowState.Minimized, TimeSpan.FromSeconds(3)));
        }, systemIntegration: true);
    }

    [Fact]
    public void CloseToTray_HidesInsteadOfClosing_ExitClosesForReal()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")] };
        doc.Settings.CloseToTray = true;
        doc.Settings.GlobalHotkey = "";
        OnStaThread(() =>
        {
            using var form = new MainForm(StoreWith(doc), systemIntegration: true);
            form.Show();
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.False(form.GlobalHotkeyRegistered); // disattivata
            form.Close();
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.False(form.IsDisposed);
            Assert.False(form.Visible);
            Assert.NotNull(form.TrayIcon);

            form.RestoreFromTray();
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(form.Visible);

            form.ExitApp();
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(form.IsDisposed);
        });
    }

    // ---------- dialoghi nuovi ----------

    [Fact]
    public void NewDialogs_OpenWithoutErrors()
    {
        OnStaThread(() =>
        {
            var doc = new ConnectionsDocument
            {
                Connections = [Unreachable("Uno")],
                CredentialProfiles = [new CredentialProfile { Name = "Admin", Username = "admin" }],
                QuickTexts = [new QuickText { Text = "dir" }],
            };
            var history = new ConnectionHistory();
            history.End(history.Start(doc.Connections[0], DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow, "Disconnessione locale", 1);
            var settings = new AppSettings { Theme = AppTheme.Dark, GlobalHotkey = "" };
            Form[] dialogs =
            [
                new SettingsDialog(settings),
                new CredentialProfilesDialog(doc),
                new QuickTextsDialog(doc),
                new HistoryDialog(history, doc, null),
                new MstscImportDialog([new MstscHistoryItem("pc.example.test", "mario")], doc),
                new ConnectionDialog(doc.Connections[0], doc.AllGroups(), "Modifica", doc.CredentialProfiles),
            ];
            foreach (var d in dialogs)
            {
                d.StartPosition = FormStartPosition.Manual;
                d.Show();
                Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(d.Visible);
                d.Close();
                d.Dispose();
            }
            using var h = new HistoryDialog(history, doc, doc.Connections[0].Id);
            Assert.Single(h.List.Items);
        });
    }
}
