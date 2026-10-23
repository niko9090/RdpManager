using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove dell'interfaccia 1.5.1: pulsante del monitor nella barra a schermo intero in tutte le posizioni, barra
/// ridimensionata trascinandone il bordo (la sessione si adatta, la misura resta salvata), barra laterale della
/// finestra ridimensionabile, esportazione .rdpm che propone le password e avvisa senza, riepilogo dell'importazione.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV151Tests : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi151-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(Action<ConnectionsDocument>? setup, params RdpConnection[] connections)
    {
        var doc = new ConnectionsDocument { Connections = connections.ToList() };
        setup?.Invoke(doc);
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        return store;
    }

    private static RdpConnection Unreachable(string name) =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd" };

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void WithMainForm(ConnectionStore store, Action<MainForm> body) => Run(() =>
    {
        using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1280, 800) };
        form.Show();
        Pump(300);
        body(form);
        if (form.IsFullScreen)
            form.ToggleFullScreen();
        foreach (var v in form.AllViews.Where(v => v.State == SessionState.Connected))
            v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
        form.Close();
        Pump(200);
    });

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static bool PumpUntil(Func<bool> condition, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return condition();
    }

    private static void OpenAllConnected(MainForm form)
    {
        foreach (var c in form.Document.Connections)
            form.OpenSession(c);
        Pump(200);
        var views = form.AllViews.ToList();
        Assert.True(PumpUntil(() => views.All(v => v.State == SessionState.Disconnected), 30_000));
        foreach (var v in views)
            v.SetStateForTests(SessionState.Connected, "Connessa");
        form.Tabs.SelectedIndex = 0;
        Pump(100);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    private static Rectangle SessionArea(MainForm form) => form.Tabs.Parent!.Bounds;

    // ---------- (1) monitor nella barra ----------

    [Theory]
    [InlineData(BarPosition.Left)]
    [InlineData(BarPosition.Right)]
    [InlineData(BarPosition.Bottom)]
    public void MonitorButton_IsInTheFullScreenBar_InEveryPosition(BarPosition position)
    {
        WithMainForm(StoreWith(null, Unreachable("Uno")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = position, Mode = BarMode.Fixed });
            form.ToggleFullScreen();
            Pump(200);
            var bar = form.FullScreenBarControl;
            var monitor = Descendants(bar).OfType<FullScreenSideBar.ActionButton>()
                .Single(b => b.Tag is SessionBarAction.NextMonitor);
            Assert.Equal("Monitor successivo", monitor.Text);
            Assert.True(monitor.Visible);
            var where = bar.RectangleToClient(monitor.RectangleToScreen(monitor.ClientRectangle));
            Assert.True(bar.ClientRectangle.IntersectsWith(where), $"il pulsante {where} è fuori dalla barra {bar.ClientRectangle}");

            var done = new List<SessionBarAction>();
            form.SessionActionForTests = done.Add;
            if (monitor.Enabled)
                monitor.PerformClick();
            else
                form.ExecuteSessionAction(SessionBarAction.NextMonitor); // un solo monitor: il pulsante c'è ma è spento
            Assert.Equal([SessionBarAction.NextMonitor], done);
        });
    }

    // ---------- (2) misura della barra trascinando il bordo ----------

    [Theory]
    [InlineData(BarPosition.Left, 80, 0, 340)]
    [InlineData(BarPosition.Right, -80, 0, 340)]
    [InlineData(BarPosition.Bottom, 0, -80, 340)]
    [InlineData(BarPosition.Left, -500, 0, FullScreenBarSettings.MinWidth)]
    public void FullScreenBar_DragTheEdge_TheSessionAdaptsAndTheSizeIsSaved(BarPosition position, int dx, int dy, int expected)
    {
        var store = StoreWith(null, Unreachable("Uno"), Unreachable("Due"));
        WithMainForm(store, form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = position, Mode = BarMode.Fixed, Width = 260 });
            form.ToggleFullScreen();
            Pump(200);
            var bar = form.FullScreenBarControl;
            var root = bar.Parent!.ClientRectangle;

            // Il bordo da trascinare è sul lato verso la sessione.
            var grip = bar.Grip;
            Assert.True(grip.Visible);
            Assert.Equal(FullScreenBarLayout.ResizeGrip(bar.ClientSize, position), grip.Bounds);
            Assert.Equal(position == BarPosition.Bottom ? Cursors.SizeNS : Cursors.SizeWE, grip.Cursor);

            var start = grip.PointToScreen(new Point(2, 2));
            grip.SimulateDrag(start, new Point(start.X + dx, start.Y + dy));
            Pump(100);

            Assert.Equal(expected, position == BarPosition.Bottom ? bar.Height : bar.Width);
            var session = SessionArea(form);
            Assert.False(session.IntersectsWith(bar.Bounds), $"sessione {session} e barra {bar.Bounds} si sovrappongono");
            Assert.Equal(root, Rectangle.Union(session, bar.Bounds)); // la sessione prende tutto il resto
            Assert.Equal(expected, form.Document.Settings.Bar.Width);
            Assert.Equal(expected, store.Load().Settings.Bar.Width); // salvata
        });
    }

    [Fact]
    public void FullScreenBar_DragFarAway_StopsAtTheCoreLimit()
    {
        WithMainForm(StoreWith(null, Unreachable("Uno")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = BarPosition.Left, Mode = BarMode.Fixed });
            form.ToggleFullScreen();
            Pump(200);
            var bar = form.FullScreenBarControl;
            var root = bar.Parent!.ClientRectangle;
            var start = bar.Grip.PointToScreen(Point.Empty);
            bar.Grip.SimulateDrag(start, new Point(start.X + 5000, start.Y));
            Pump(100);
            Assert.Equal(FullScreenBarLayout.MaxThickness(root, BarPosition.Left), bar.Width);
            Assert.True(bar.Width <= FullScreenBarSettings.MaxWidth);
            Assert.True(SessionArea(form).Width >= root.Width / 2);
        });
    }

    [Fact]
    public void FullScreenBar_SavedSize_IsUsedAtTheNextStart()
    {
        var store = StoreWith(d => d.Settings.FullScreenBar = new FullScreenBarSettings { Position = BarPosition.Right, Width = 410 }, Unreachable("Uno"));
        WithMainForm(store, form =>
        {
            OpenAllConnected(form);
            form.ToggleFullScreen();
            Pump(200);
            Assert.Equal(410, form.FullScreenBarControl.Width);
        });
    }

    // ---------- barra laterale della finestra ----------

    [Fact]
    public void WindowSidebar_Pinned_SavedWidthIsUsedAndTheSplitterSavesIt()
    {
        var store = StoreWith(d => d.Settings.SidebarWidth = 400, Unreachable("Uno"));
        WithMainForm(store, form =>
        {
            Assert.Equal(400, form.Sidebar.Width);
            Assert.True(form.SidebarShown, "barra laterale non visibile");
            Assert.True(form.SidebarSplitter.Visible, $"splitter: {form.SidebarSplitter.Bounds} parent {form.SidebarSplitter.Parent?.Visible} sidebar {form.Sidebar.Bounds}");
            Assert.False(form.SidebarGrip.Visible); // fissa: si trascina lo splitter

            // Come dopo un trascinamento dello splitter.
            form.Sidebar.Width = 330;
            typeof(Splitter).GetMethod("OnSplitterMoved", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form.SidebarSplitter, [new SplitterEventArgs(0, 0, 330, 0)]);
            Assert.Equal(330, form.Document.Settings.SidebarWidth);
            Assert.Equal(330, store.Load().Settings.SidebarWidth);
        });
    }

    [Fact]
    public void WindowSidebar_AutoHide_DragTheEdge()
    {
        var store = StoreWith(d => d.Settings.SidebarMode = SidebarMode.AutoHide, Unreachable("Uno"));
        WithMainForm(store, form =>
        {
            form.ShowSidebarForTests();
            Pump(100);
            var grip = form.SidebarGrip;
            Assert.True(grip.Visible);
            Assert.Equal(DockStyle.Right, grip.Dock);
            var start = grip.PointToScreen(Point.Empty);
            grip.SimulateDrag(start, new Point(start.X + 120, start.Y));
            Pump(100);
            Assert.Equal(WindowSidebar.DefaultWidth + 120, form.Sidebar.Width);
            Assert.Equal(WindowSidebar.DefaultWidth + 120, store.Load().Settings.SidebarWidth);

            grip.SimulateDrag(start, new Point(start.X - 5000, start.Y));
            Pump(100);
            Assert.Equal(WindowSidebar.MinWidth, form.Sidebar.Width); // entro i limiti del Core
        });
    }

    // ---------- (3) .rdpm: password ----------

    private static ConnectionsDocument WithPasswords()
    {
        var profile = new CredentialProfile { Name = "Admin", Username = "admin", Password = "profilo" };
        var doc = new ConnectionsDocument { CredentialProfiles = [profile] };
        doc.Connections.Add(new RdpConnection { Name = "A", Host = "a.invalid", Password = "segreta" });
        doc.Connections.Add(new RdpConnection { Name = "B", Host = "b.invalid", CredentialProfileId = profile.Id });
        return doc;
    }

    [Fact]
    public void ExportDialog_ProposesWithPasswords_AndWarnsIfWithoutIsChosen()
    {
        Run(() =>
        {
            using var dlg = new RdpmExportDialog(WithPasswords());
            Assert.True(dlg.WithPasswordsChosen); // proposta: con le password, cifrate

            var asked = new List<string>();
            bool answer = false;
            dlg.Confirm = q => { asked.Add(q); return answer; };
            dlg.ShowProblem = _ => { };

            dlg.ChooseForTests(withPasswords: false);
            Assert.False(dlg.CanAccept()); // «No» all'avviso: si resta nel dialogo
            Assert.Contains("2 password", Assert.Single(asked));
            answer = true;
            Assert.True(dlg.CanAccept());
            Assert.Null(dlg.FilePassword);

            dlg.ChooseForTests(withPasswords: true, "corta");
            Assert.False(dlg.CanAccept());
            dlg.ChooseForTests(withPasswords: true, "password lunga", "diversa");
            Assert.False(dlg.CanAccept());
            dlg.ChooseForTests(withPasswords: true, "password lunga");
            Assert.True(dlg.CanAccept());
            Assert.Equal("password lunga", dlg.FilePassword);
            Assert.Equal(2, asked.Count); // con le password nessun avviso
        });
    }

    [Fact]
    public void ExportDialog_WithoutSavedPasswords_ProposesWithoutAndDoesNotWarn()
    {
        Run(() =>
        {
            var doc = new ConnectionsDocument();
            doc.Connections.Add(new RdpConnection { Host = "a.invalid" });
            using var dlg = new RdpmExportDialog(doc);
            Assert.False(dlg.WithPasswordsChosen);
            dlg.Confirm = _ => throw new InvalidOperationException("nessun avviso atteso");
            Assert.True(dlg.CanAccept());
        });
    }

    [Fact]
    public void Import_ShowsTheSummary_AndKeepsPasswordsAndProfiles()
    {
        var existing = new RdpConnection { Name = "B già qui", Host = "b.invalid" }; // senza password
        var store = StoreWith(null, existing);
        WithMainForm(store, form =>
        {
            var json = RdpmFile.Export(WithPasswords(), "password del file", iterations: 1000);
            var imported = RdpmFile.Import(json, "password del file");
            string? summary = null;
            form.ImportSummaryForTests = s => summary = s;

            var result = form.ApplyRdpmImport(imported, ImportMode.Merge, applySettings: false);

            Assert.NotNull(summary);
            Assert.Contains("2 password importate", summary);
            Assert.Contains("1 profilo di credenziali importato", summary);
            Assert.Equal((1, 1, 1), (result.Added, result.Skipped, result.ConnectionsLinkedToProfile));

            var saved = store.Load();
            Assert.Equal("segreta", saved.Connections.Single(c => c.Name == "A").Password);
            Assert.Equal("profilo", Assert.Single(saved.CredentialProfiles).Password);
            Assert.Equal("profilo", saved.ResolveCredentials(saved.Connections.Single(c => c.Name == "B già qui")).Password);
        });
    }
}
