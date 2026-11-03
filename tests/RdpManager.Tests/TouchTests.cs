using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove dell'uso col dito (1.6.2): misure dei bersagli e dei dialoghi su uno schermo piccolo (Surface 1366x912 al 150%),
/// riconoscimento del tocco prolungato, tastiera su schermo, pulsanti delle azioni principali.
/// </summary>
public sealed class TouchCoreTests
{
    // Surface: 1366x912 pixel veri al 150% = 911x608 logici; tolta la barra delle applicazioni (circa 40 logici) restano 911x568.
    private static readonly Rectangle SurfaceWorkArea = new(0, 0, 1366, 852);

    [Theory]
    [InlineData(96, 40)]
    [InlineData(120, 50)]
    [InlineData(144, 60)]
    [InlineData(192, 80)]
    public void MinTarget_ScalesWithDpi(int dpi, int expected) =>
        Assert.Equal(expected, TouchMetrics.Scale(TouchMetrics.MinTarget, dpi));

    [Fact]
    public void Scale_NeverGoesBelow96Dpi() => Assert.Equal(40, TouchMetrics.Scale(40, 0));

    [Fact]
    public void PaddingToReach_AddsHalfTheMissingHeightOnEachSide()
    {
        Assert.Equal(0, TouchMetrics.PaddingToReach(40, 40));
        Assert.Equal(0, TouchMetrics.PaddingToReach(60, 40));
        Assert.Equal(9, TouchMetrics.PaddingToReach(23, 40)); // 23 + 2*9 = 41
        Assert.True(23 + 2 * TouchMetrics.PaddingToReach(23, 40) >= 40);
    }

    [Fact]
    public void FitSize_OnSurfaceScreen_KeepsDialogsInsideTheWorkArea()
    {
        var max = TouchMetrics.MaxDialogSize(SurfaceWorkArea, 144);
        Assert.True(max.Width <= 1366 && max.Height <= 852);
        Assert.Equal(1366 - 2 * 18, max.Width); // margine di 12 px logici = 18 veri
        // Un dialogo di 900x900 logici (1350x1350 veri) viene ridotto; uno piccolo resta com'è.
        Assert.Equal(max, TouchMetrics.FitSize(new Size(1350, 1350), SurfaceWorkArea, 144));
        Assert.Equal(new Size(600, 300), TouchMetrics.FitSize(new Size(600, 300), SurfaceWorkArea, 144));
    }

    [Fact]
    public void Place_CentersOnOwnerButNeverOutsideTheWorkArea()
    {
        var size = new Size(600, 400);
        Assert.Equal(new Point(383, 226), TouchMetrics.Place(size, new Rectangle(0, 0, 1366, 852), SurfaceWorkArea));
        // Proprietario in un angolo, fuori dallo schermo a destra: il dialogo resta dentro.
        var p = TouchMetrics.Place(size, new Rectangle(1300, 800, 400, 300), SurfaceWorkArea);
        Assert.True(p.X >= 0 && p.X + size.Width <= 1366 && p.Y >= 0 && p.Y + size.Height <= 852);
        // Senza proprietario: al centro dell'area.
        Assert.Equal(new Point(383, 226), TouchMetrics.Place(size, null, SurfaceWorkArea));
    }

    // ---------- tocco prolungato ----------

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LongPress_FiresOnceAfterTheHoldTimeWithTheFingerStill()
    {
        var d = new LongPressDetector(TimeSpan.FromMilliseconds(500), tolerance: 10);
        d.Press(new Point(50, 60), T0);
        Assert.False(d.Poll(T0.AddMilliseconds(499)));
        Assert.True(d.Poll(T0.AddMilliseconds(500)));
        Assert.True(d.Fired);
        Assert.Equal(new Point(50, 60), d.Origin);
        Assert.False(d.Poll(T0.AddSeconds(5))); // una volta sola
    }

    [Fact]
    public void LongPress_SmallWobbleIsTolerated_BigMoveCancels()
    {
        var d = new LongPressDetector(TimeSpan.FromMilliseconds(500), tolerance: 10);
        d.Press(new Point(100, 100), T0);
        d.Move(new Point(106, 105)); // 7,8 px: dentro la tolleranza
        Assert.True(d.Poll(T0.AddMilliseconds(600)));

        d.Press(new Point(100, 100), T0);
        d.Move(new Point(120, 100)); // è un trascinamento
        Assert.False(d.IsArmed);
        Assert.False(d.Poll(T0.AddSeconds(2)));
        Assert.False(d.Fired);
    }

    [Fact]
    public void LongPress_ReleaseBeforeTheTimeCancels_AndANewPressStartsFresh()
    {
        var d = new LongPressDetector(TimeSpan.FromMilliseconds(500));
        d.Press(new Point(1, 1), T0);
        d.Release();
        Assert.False(d.Poll(T0.AddSeconds(1)));

        d.Press(new Point(1, 1), T0.AddSeconds(2));
        Assert.False(d.Poll(T0.AddSeconds(2).AddMilliseconds(100)));
        Assert.True(d.Poll(T0.AddSeconds(2).AddMilliseconds(500)));
    }

    [Fact]
    public void LongPress_FiredStaysTrueAfterReleaseUntilTheNextPress()
    {
        var d = new LongPressDetector();
        d.Press(new Point(0, 0), T0);
        Assert.True(d.Poll(T0 + TouchMetrics.LongPressHold));
        d.Release();
        Assert.True(d.Fired); // serve a ignorare il clic che segue
        d.Press(new Point(0, 0), T0.AddSeconds(3));
        Assert.False(d.Fired);
    }

    [Fact]
    public void LongPress_MoveWithoutPressIsIgnored()
    {
        var d = new LongPressDetector();
        d.Move(new Point(500, 500));
        Assert.False(d.IsArmed);
        Assert.False(d.Poll(T0.AddSeconds(10)));
    }

    // ---------- tastiera su schermo ----------

    [Fact]
    public void ScreenKeyboard_TriesTabTipFirstThenOsk()
    {
        var list = ScreenKeyboard.Candidates(@"C:\Program Files\Common Files", @"C:\Windows\System32");
        Assert.Equal([@"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe", @"C:\Windows\System32\osk.exe"], list);
    }

    [Fact]
    public void ScreenKeyboard_StartsTheFirstOneThatExistsAndStarts()
    {
        string[] list = ["tabtip.exe", "osk.exe"];
        var started = new List<string>();
        Assert.Equal("tabtip.exe", ScreenKeyboard.Open(list, _ => true, p => { started.Add(p); return true; }));
        Assert.Equal(["tabtip.exe"], started);

        // TabTip non c'è: osk.
        Assert.Equal("osk.exe", ScreenKeyboard.Open(list, p => p == "osk.exe", _ => true));

        // TabTip c'è ma non parte: osk.
        started.Clear();
        Assert.Equal("osk.exe", ScreenKeyboard.Open(list, _ => true, p => { started.Add(p); return p == "osk.exe"; }));
        Assert.Equal(["tabtip.exe", "osk.exe"], started);
    }

    [Fact]
    public void ScreenKeyboard_NoneAvailable_ReturnsNull() =>
        Assert.Null(ScreenKeyboard.Open(["a.exe", "b.exe"], _ => false, _ => true));

    // ---------- barra a schermo intero ----------

    [Fact]
    public void SessionActions_NowIncludeTheScreenKeyboard()
    {
        Assert.Contains(SessionBarAction.ScreenKeyboard, FullScreenBarLayout.SessionActions);
        Assert.Equal("Tastiera su schermo", FullScreenBarSettings.Describe(SessionBarAction.ScreenKeyboard));
    }

    [Fact]
    public void ActionButtonLayout_HonorsTheTouchMinimumHeight()
    {
        // Una riga: alta almeno 60 (40 logici al 150%).
        Assert.Equal(60, FullScreenBarLayout.LayoutActionButton(300, 80, 60, 16, minHeight: 60).Height);
        // Due righe: mai sotto il minimo.
        Assert.Equal(60, FullScreenBarLayout.LayoutActionButton(100, 80, 60, 16, minHeight: 60).Height);
        // Senza minimo: come prima.
        Assert.Equal(28, FullScreenBarLayout.LayoutActionButton(300, 80, 60, 16).Height);
    }
}

[Collection("UI")]
[Trait("Category", "UI")]
public sealed class TouchUiTests : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTouch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TouchUi.WorkAreaForTests = null;
        TouchUi.ScreenKeyboardForTests = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList() });
        return store;
    }

    private static RdpConnection Unreachable(string name) =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd" };

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static void WithMainForm(ConnectionStore store, Action<MainForm> body) => OnStaThread(() =>
    {
        using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.Show();
        Pump(300);
        body(form);
        form.ExitApp();
        Pump(200);
    });

    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control c in root.Controls)
            foreach (var d in All(c))
                yield return d;
    }

    /// <summary>L'area di lavoro di un Surface 1366x912 al 150% (911x568 logici, tolta la barra delle applicazioni) per i dpi del dialogo.</summary>
    private static Rectangle Surface(Control c) =>
        new(0, 0, TouchMetrics.Scale(911, c.DeviceDpi), TouchMetrics.Scale(568, c.DeviceDpi));

    private static List<Form> Dialogs()
    {
        var doc = new ConnectionsDocument
        {
            Connections = [Unreachable("Uno")],
            CredentialProfiles = [new CredentialProfile { Name = "Admin", Username = "admin" }],
            QuickTexts = [new QuickText { Text = "dir" }],
        };
        var history = new ConnectionHistory();
        history.End(history.Start(doc.Connections[0], DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow, "Disconnessione locale", 1);
        return
        [
            new ConnectionDialog(doc.Connections[0], doc.AllGroups(), "Modifica connessione", doc.CredentialProfiles),
            new SettingsDialog(new AppSettings { GlobalHotkey = "" }),
            new RdpmExportDialog(doc),
            new NetworkScanForm(doc, (_, _) => new DiscoveryImportResult([], 0)),
            new HistoryDialog(history, doc, null),
            new CredentialProfilesDialog(doc),
            new QuickTextsDialog(doc),
            new MstscImportDialog([new MstscHistoryItem("pc.example.test", "mario")], doc),
            new WorkspacesDialog(doc),
            new PinSetDialog(),
            new ChoiceDialog("Cartella", "Scegli", ["A", "B"], "A", editable: true),
        ];
    }

    [Fact]
    public void EveryFormClassInTheAppIsTouchAware_ExceptTheFullScreenOnes()
    {
        // Un dialogo nuovo che parte da Form e non da TouchForm resterebbe senza adattamento al tocco: lo segnala questa prova.
        string[] fullScreenOrHosts = ["MainForm", "OverviewForm", "LockForm"];
        var offenders = typeof(MainForm).Assembly.GetTypes()
            .Where(t => typeof(Form).IsAssignableFrom(t) && t != typeof(TouchForm) && !typeof(TouchForm).IsAssignableFrom(t))
            .Where(t => !fullScreenOrHosts.Contains(t.Name) && !t.IsNested)
            .Select(t => t.Name).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Dialogs_FitTheSurfaceWorkArea_AndStayInsideIt()
    {
        OnStaThread(() =>
        {
            foreach (var dialog in Dialogs())
            {
                using var d = dialog;
                TouchUi.WorkAreaForTests = Surface(d);
                var area = TouchUi.WorkAreaForTests.Value;
                d.StartPosition = FormStartPosition.Manual;
                d.Location = new Point(5, 5);
                d.Show();
                Pump(150);
                string name = d.GetType().Name;
                Assert.True(d.Width <= area.Width, $"{name} è più largo dell'area di lavoro: {d.Width} > {area.Width}");
                Assert.True(d.Height <= area.Height, $"{name} è più alto dell'area di lavoro: {d.Height} > {area.Height}");
                Assert.True(area.Contains(d.Bounds), $"{name} esce dall'area di lavoro: {d.Bounds} fuori da {area}");
                Assert.True(((TouchForm)d).TouchApplied, name);
                d.Close();
            }
        });
    }

    [Fact]
    public void LongDialogs_ScrollInsteadOfGrowingPastTheScreen_AndKeepOkAndCancelVisible()
    {
        OnStaThread(() =>
        {
            var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")] };
            using var d = new ConnectionDialog(doc.Connections[0], doc.AllGroups(), "Modifica");
            // Schermo molto basso (400 logici): il modulo non ci sta e deve scorrere.
            TouchUi.WorkAreaForTests = new Rectangle(0, 0, TouchMetrics.Scale(911, d.DeviceDpi), TouchMetrics.Scale(400, d.DeviceDpi));
            d.StartPosition = FormStartPosition.Manual;
            d.Show();
            Pump(150);
            var area = TouchUi.WorkAreaForTests.Value;
            Assert.True(area.Contains(d.Bounds));
            var scroll = All(d).OfType<Panel>().Single(p => p.AutoScroll);
            Assert.True(scroll.VerticalScroll.Visible || scroll.DisplayRectangle.Height > scroll.ClientSize.Height, "il modulo dovrebbe scorrere");
            var ok = All(d).OfType<Button>().Single(b => b.Text == "OK");
            var cancel = All(d).OfType<Button>().Single(b => b.Text == "Annulla");
            Assert.True(d.ClientRectangle.Contains(d.PointToClient(ok.PointToScreen(new Point(ok.Width / 2, ok.Height / 2)))));
            Assert.True(d.ClientRectangle.Contains(d.PointToClient(cancel.PointToScreen(new Point(cancel.Width / 2, cancel.Height / 2)))));
            d.Close();
        });
    }

    [Fact]
    public void LongAutoSizeDialog_WithoutItsOwnLayout_GetsAScrollingBody()
    {
        OnStaThread(() =>
        {
            using var d = new ChoiceDialog("Cartella", "Scegli", ["A", "B"], "A", editable: true);
            TouchUi.WorkAreaForTests = new Rectangle(0, 0, 200, 120); // piccolissima: costringe al riquadro che scorre
            d.StartPosition = FormStartPosition.Manual;
            d.Show();
            Pump(150);
            Assert.True(TouchUi.IsScrolling(d));
            Assert.True(TouchUi.WorkAreaForTests.Value.Contains(d.Bounds));
            d.Close();
        });
    }

    [Fact]
    public void DialogButtonsAndChecks_AreAtLeastFortyLogicalPixels()
    {
        OnStaThread(() =>
        {
            foreach (var dialog in Dialogs())
            {
                using var d = dialog;
                TouchUi.WorkAreaForTests = Surface(d);
                d.StartPosition = FormStartPosition.Manual;
                d.Show();
                Pump(150);
                int min = TouchMetrics.Scale(TouchMetrics.MinTarget, d.DeviceDpi);
                foreach (var c in All(d).Where(c => c is Button or CheckBox or RadioButton))
                    Assert.True(c.Height >= min, $"{d.GetType().Name}: «{c.Text}» è alto {c.Height} px (minimo {min})");
                // La casella interna di un numero o di una tendina è una parte del controllo (che si misura a parte), non un bersaglio.
                foreach (var c in All(d).OfType<TextBox>().Where(t => !t.Multiline && t.Parent is not (NumericUpDown or ComboBox)))
                    Assert.True(c.Height >= min, $"{d.GetType().Name}: casella di testo alta {c.Height} px (minimo {min})");
                foreach (var c in All(d).OfType<NumericUpDown>())
                    Assert.True(c.Height >= min, $"{d.GetType().Name}: numero alto {c.Height} px (minimo {min})");
                foreach (var c in All(d).OfType<ComboBox>())
                    Assert.True(c.Height >= min - 4, $"{d.GetType().Name}: tendina alta {c.Height} px (minimo {min})");
                foreach (var c in All(d).OfType<TabControl>())
                    for (int i = 0; i < c.TabCount; i++)
                        Assert.True(c.GetTabRect(i).Height >= min - 4, $"{d.GetType().Name}: linguetta alta {c.GetTabRect(i).Height} px (minimo {min})");
                d.Close();
            }
        });
    }

    [Fact]
    public void ListsInDialogs_HaveRowsAtLeastFortyLogicalPixels()
    {
        OnStaThread(() =>
        {
            var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")], QuickTexts = [new QuickText { Text = "dir" }] };
            using var d = new QuickTextsDialog(doc);
            using var s = new SettingsDialog(new AppSettings { GlobalHotkey = "" });
            foreach (var form in new Form[] { d, s })
            {
                TouchUi.WorkAreaForTests = Surface(form);
                form.StartPosition = FormStartPosition.Manual;
                form.Show();
                Pump(150);
                int min = TouchMetrics.Scale(TouchMetrics.MinTarget, form.DeviceDpi);
                foreach (var list in All(form).OfType<ListBox>())
                    Assert.True(list.ItemHeight >= min, $"{form.GetType().Name}: righe alte {list.ItemHeight} px (minimo {min})");
                foreach (var list in All(form).OfType<ListView>().Where(l => l.View == View.Details))
                    Assert.True(list.SmallImageList!.ImageSize.Height >= min, $"{form.GetType().Name}: righe di elenco basse");
                form.Close();
            }
        });
    }

    // ---------- finestra principale ----------

    [Fact]
    public void MainWindow_SidebarStaysOnTheLeft_WithTouchSizedTargets()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due")), form =>
        {
            int min = TouchUi.MinTarget(form);
            Assert.Equal(DockStyle.Left, form.Sidebar.Dock);
            Assert.Equal(0, form.Sidebar.Top);
            Assert.True(form.Tree.ItemHeight >= min, $"righe dell'elenco alte {form.Tree.ItemHeight} px (minimo {min})");
            Assert.True(form.SearchBox.Height >= min, "casella di ricerca bassa");

            foreach (var b in form.TouchButtons)
            {
                Assert.True(b.Height >= min, $"«{b.Text}» alto {b.Height} px (minimo {min})");
                Assert.True(b.Width >= min, $"«{b.Text}» largo {b.Width} px");
                Assert.True(form.Sidebar.ClientRectangle.Contains(b.Bounds.Location) || b.Parent!.Bottom <= form.Sidebar.Height, $"«{b.Text}» fuori dalla barra laterale");
                Assert.Same(form.Sidebar, FindAncestor(b, form.Sidebar));
            }
            // I pulsanti stanno in fondo alla barra, non in cima.
            Assert.True(form.TouchConnectButton.Parent!.Top > form.Sidebar.Height / 3, "la barra dei pulsanti da dito dev'essere in basso");

            // Barra degli strumenti dell'elenco: ogni pulsante da dito.
            foreach (var strip in All(form.Sidebar).OfType<ToolStrip>())
                foreach (ToolStripItem item in strip.Items.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator && i.Visible && !i.IsOnOverflow))
                    Assert.True(item.Height >= min - 2, $"«{item.Text}» alto {item.Height} px (minimo {min})");
        });
    }

    private static Control? FindAncestor(Control c, Control target)
    {
        for (var p = c.Parent; p is not null; p = p.Parent)
            if (p == target)
                return p;
        return null;
    }

    [Fact]
    public void MainWindow_MenusAndTabsAreTouchSized()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due")), form =>
        {
            int min = TouchUi.MinTarget(form);
            var menu = form.MainMenuStrip!;
            foreach (ToolStripMenuItem top in menu.Items)
            {
                Assert.True(top.Height >= min - 2, $"menu «{top.Text}» alto {top.Height} px (minimo {min})");
                top.ShowDropDown();
                Pump(80);
                foreach (ToolStripItem item in top.DropDownItems.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator && i.Visible))
                    Assert.True(item.Height >= min - 2, $"voce «{item.Text}» di «{top.Text}» alta {item.Height} px (minimo {min})");
                top.HideDropDown();
            }

            form.OpenSession(form.Document.Connections[0]);
            Pump(200);
            Assert.True(form.Tabs.GetTabRect(0).Height >= min - 4, $"linguetta alta {form.Tabs.GetTabRect(0).Height} px (minimo {min})");
            var view = form.CurrentView!;
            var bar = All(view).OfType<ToolStrip>().Single();
            foreach (ToolStripItem item in bar.Items.Cast<ToolStripItem>().Where(i => i is ToolStripButton or ToolStripDropDownButton && i.Visible))
                Assert.True(item.Height >= min - 2, $"«{item.Text}» della sessione alto {item.Height} px (minimo {min})");
            Assert.Contains(bar.Items.Cast<ToolStripItem>(), i => i.Text == "Tastiera");
        });
    }

    [Fact]
    public void MainWindow_TouchButtons_ConnectDisconnectSwitchAndOpenTheKeyboard()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due")), form =>
        {
            Assert.False(form.TouchDisconnectButton.Enabled); // niente sessioni
            Assert.False(form.TouchNextButton.Enabled);

            form.Tree.SelectedNode = form.Tree.Nodes.Cast<TreeNode>().SelectMany(Flatten).First(n => n.Tag is RdpConnection c && c.Name == "Uno");
            form.TouchConnectButton.PerformClick();
            Pump(200);
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.True(form.TouchDisconnectButton.Enabled);
            Assert.False(form.TouchNextButton.Enabled); // una sola scheda

            form.Tree.SelectedNode = form.Tree.Nodes.Cast<TreeNode>().SelectMany(Flatten).First(n => n.Tag is RdpConnection c && c.Name == "Due");
            form.TouchConnectButton.PerformClick();
            Pump(200);
            Assert.Equal(2, form.Tabs.TabCount);
            Assert.Equal(1, form.Tabs.SelectedIndex);
            form.TouchPreviousButton.PerformClick();
            Assert.Equal(0, form.Tabs.SelectedIndex);
            form.TouchNextButton.PerformClick();
            Assert.Equal(1, form.Tabs.SelectedIndex);

            int opened = 0;
            TouchUi.ScreenKeyboardForTests = () => { opened++; return true; };
            form.TouchKeyboardButton.PerformClick();
            Pump(100);
            Assert.Equal(1, opened);
            form.ExecuteSessionAction(SessionBarAction.ScreenKeyboard);
            Assert.Equal(2, opened);

            form.TouchDisconnectButton.PerformClick(); // la sessione non è connessa: si chiude senza domande
            Pump(100);
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.True(form.TouchFullScreenButton.Enabled);
        });
    }

    private static IEnumerable<TreeNode> Flatten(TreeNode n) => new[] { n }.Concat(n.Nodes.Cast<TreeNode>().SelectMany(Flatten));

    [Fact]
    public void MainWindow_LongPressOnTheList_OpensTheContextMenuWithoutRightClick()
    {
        WithMainForm(StoreWith(Unreachable("Uno")), form =>
        {
            var node = form.Tree.Nodes.Cast<TreeNode>().SelectMany(Flatten).First(n => n.Tag is RdpConnection);
            var point = new Point(node.Bounds.X + 10, node.Bounds.Y + node.Bounds.Height / 2);
            form.ShowTreeMenuAt(point);
            Pump(100);
            Assert.Same(node, form.Tree.SelectedNode);
            var menu = form.Tree.ContextMenuStrip!;
            Assert.True(menu.Visible);
            menu.Close();
        });
    }

    [Fact]
    public void LongPressControl_FiresOnTick_AndNotWhenReleasedOrMoved()
    {
        OnStaThread(() =>
        {
            var now = DateTime.UtcNow;
            using var panel = new Panel { Size = new Size(100, 100) };
            var fired = new List<Point>();
            using var press = LongPress.Attach(panel, p => fired.Add(p), () => now);

            press.Press(new Point(10, 20));
            now += TimeSpan.FromMilliseconds(300);
            press.Tick();
            Assert.Empty(fired);
            now += TimeSpan.FromMilliseconds(300);
            press.Tick();
            Assert.Equal([new Point(10, 20)], fired);
            Assert.True(press.FiredRecently);
            now += TimeSpan.FromSeconds(3);
            Assert.False(press.FiredRecently);

            press.Press(new Point(1, 1));
            press.Release();
            now += TimeSpan.FromSeconds(1);
            press.Tick();
            Assert.Single(fired);
        });
    }

    [Fact]
    public void MainWindow_IsBroughtInsideTheWorkArea()
    {
        OnStaThread(() =>
        {
            using var form = new MainForm(StoreWith(Unreachable("Uno")));
            TouchUi.WorkAreaForTests = new Rectangle(0, 0, 1000, 700);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(300, 300);
            form.Size = new Size(1400, 900);
            form.Show();
            Pump(300);
            Assert.True(TouchUi.WorkAreaForTests.Value.Contains(form.Bounds), $"{form.Bounds} fuori da 1000x700");
            form.ExitApp();
            Pump(100);
        });
    }
}
