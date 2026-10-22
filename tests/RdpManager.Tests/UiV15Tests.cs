using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;
using Xunit.Abstractions;

namespace RdpManager.Tests;

/// <summary>
/// Prove dell'interfaccia 1.5: a schermo intero niente in alto, barra a sinistra, a destra o in basso, fissa (la
/// sessione non viene coperta), a comparsa o nascosta, sezioni nell'ordine scelto, pulsanti rapidi = scorciatoie,
/// impostazioni della barra. Host irraggiungibili e archivio temporaneo; lo stato «connessa» si simula.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV15Tests(ITestOutputHelper output) : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi15-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(Action<ConnectionsDocument>? setup, params RdpConnection[] connections)
    {
        var doc = new ConnectionsDocument { Connections = connections.ToList() };
        setup?.Invoke(doc);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        return store;
    }

    private static RdpConnection Unreachable(string name, bool favorite = false) =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd", IsFavorite = favorite };

    private static void WithMainForm(ConnectionStore store, Action<MainForm> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                form.Show();
                Pump(300);
                body(form);
                if (form.IsFullScreen)
                    form.ToggleFullScreen();
                foreach (var v in form.AllViews.Where(v => v.State == SessionState.Connected))
                    v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
                form.Close();
                Pump(200);
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

    private static void Pump(int ms) => PumpUntil(() => false, ms);

    private static bool PumpUntil(Func<bool> condition, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms)
                return false;
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return true;
    }

    private static List<RdpSessionView> OpenAllConnected(MainForm form)
    {
        foreach (var c in form.Document.Connections)
            form.OpenSession(c);
        Pump(200);
        var views = form.Tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).ToList();
        Assert.True(PumpUntil(() => views.All(v => v.State == SessionState.Disconnected), 30_000));
        foreach (var v in views)
            v.SetStateForTests(SessionState.Connected, "Connessa");
        form.Tabs.SelectedIndex = 0;
        Pump(100);
        return views;
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

    /// <summary>Area della sessione (il pannello che contiene le schede) nelle coordinate della barra.</summary>
    private static Rectangle SessionArea(MainForm form) => form.Tabs.Parent!.Bounds;

    /// <summary>A schermo intero niente occupa la parte alta: nessun controllo visibile largo come lo schermo e basso in cima.</summary>
    private static void AssertNothingAtTheTop(MainForm form)
    {
        var root = form.FullScreenBarControl.Parent!;
        foreach (Control c in root.Controls)
        {
            if (!c.Visible || c == form.Tabs.Parent)
                continue;
            bool topStrip = c.Top <= 0 && c.Bottom < root.ClientSize.Height && c.Width >= root.ClientSize.Width / 2;
            Assert.False(topStrip, $"«{c.GetType().Name}» occupa la parte alta dello schermo: {c.Bounds}");
        }
        Assert.Equal(0, SessionArea(form).Top);
    }

    [Theory]
    [InlineData(BarPosition.Left)]
    [InlineData(BarPosition.Right)]
    [InlineData(BarPosition.Bottom)]
    public void FixedBar_SitsOnItsSide_TheSessionUsesTheRestWithoutOverlap(BarPosition position)
    {
        WithMainForm(StoreWith(null, Unreachable("Uno"), Unreachable("Due")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = position, Mode = BarMode.Fixed, Width = 250 });
            form.ToggleFullScreen();
            Pump(200);

            var bar = form.FullScreenBarControl;
            var root = bar.Parent!.ClientRectangle;
            Assert.True(form.FullScreenBarShown);
            Assert.True(bar.Visible);
            var session = SessionArea(form);
            Assert.False(session.IntersectsWith(bar.Bounds), $"sessione {session} e barra {bar.Bounds} si sovrappongono");
            Assert.Equal(root, Rectangle.Union(session, bar.Bounds));
            switch (position)
            {
                case BarPosition.Left:
                    Assert.Equal((0, 250, root.Height), (bar.Left, bar.Width, bar.Height));
                    break;
                case BarPosition.Right:
                    Assert.Equal((root.Right, 250, root.Height), (bar.Right, bar.Width, bar.Height));
                    break;
                case BarPosition.Bottom:
                    Assert.Equal((root.Bottom, 250, root.Width), (bar.Bottom, bar.Height, bar.Width));
                    break;
            }
            AssertNothingAtTheTop(form);
            Assert.Equal(BarSection.ConnectedSessions, form.FullScreenBarControl.ShownSections[0]);
            Assert.False(form.SidebarShown); // la barra della finestra non c'è a schermo intero

            // Nascosta con la scorciatoia: la sessione si riprende tutto lo schermo.
            form.ExecuteShortcut(ShortcutAction.ToggleSidebar);
            Pump(100);
            Assert.False(bar.Visible);
            Assert.Equal(root, SessionArea(form));
            form.ExecuteShortcut(ShortcutAction.ToggleSidebar);
            Assert.True(bar.Visible);
        });
    }

    [Fact]
    public void AutoRevealBar_OpensOverTheSession_AndHiddenOnlyWithTheShortcut()
    {
        WithMainForm(StoreWith(null, Unreachable("Uno")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = BarPosition.Right, Mode = BarMode.AutoReveal });
            form.ToggleFullScreen();
            form.HideFullScreenBarOverlayForTests(); // entrando compare per un attimo
            Pump(100);
            var bar = form.FullScreenBarControl;
            var root = bar.Parent!.ClientRectangle;
            Assert.False(bar.Visible);
            Assert.Equal(root, SessionArea(form)); // la sessione prende tutto lo schermo

            form.RevealFullScreenBar();
            Assert.True(bar.Visible);
            Assert.Equal(root.Right, bar.Right);
            Assert.Equal(root, SessionArea(form)); // sopra la sessione, che non si restringe
            Assert.Equal(0, bar.Parent.Controls.GetChildIndex(bar));
            AssertNothingAtTheTop(form);
            form.ToggleSidebar();
            Assert.False(bar.Visible);

            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = BarPosition.Bottom, Mode = BarMode.Hidden });
            Pump(100);
            Assert.False(bar.Visible);
            Assert.Equal(root, SessionArea(form));
            form.ExecuteShortcut(ShortcutAction.NextSession); // con «nascosta» non compare da sola
            Assert.False(bar.Visible);
            form.ToggleSidebar();
            Assert.True(bar.Visible);
            Assert.Equal(root.Bottom, bar.Bottom);
            form.ToggleSidebar();
            Assert.False(bar.Visible);
        });
    }

    [Fact]
    public void Sections_FollowTheChosenOrder_AndQuickButtonsAreTheShortcutActions()
    {
        var store = StoreWith(doc =>
        {
            doc.QuickTexts.Add(new QuickText { Name = "Data", Text = "date /t" });
            doc.SaveWorkspace("Mattina", new WorkspaceLayout([doc.Connections[0].Id], null, null, false, null, null, 0.5));
        }, Unreachable("Uno", favorite: true), Unreachable("Due"), Unreachable("Tre"));
        WithMainForm(store, form =>
        {
            var views = OpenAllConnected(form);
            var settings = new FullScreenBarSettings
            {
                Sections =
                [
                    new(BarSection.QuickButtons), new(BarSection.Workspaces), new(BarSection.ConnectedSessions),
                    new(BarSection.Favorites, visible: false), new(BarSection.AllConnections), new(BarSection.QuickTexts),
                ],
            };
            form.ApplyFullScreenBarForTests(settings);
            form.ToggleFullScreen();
            Pump(150);
            var bar = form.FullScreenBarControl;
            Assert.Equal([BarSection.QuickButtons, BarSection.Workspaces, BarSection.ConnectedSessions, BarSection.AllConnections, BarSection.QuickTexts],
                bar.ShownSections);
            // Titoli delle sezioni nello stesso ordine sullo schermo (dall'alto in basso).
            var titles = Descendants(bar).OfType<Label>().Where(l => l.Text == l.Text.ToUpperInvariant() && l.Text.Length > 3)
                .OrderBy(l => l.PointToScreen(Point.Empty).Y).Select(l => l.Text).ToList();
            Assert.Equal(bar.ShownSections.Select(s => FullScreenBarSettings.Title(s).ToUpperInvariant()), titles);

            // Pulsanti rapidi: esattamente le azioni delle scorciatoie, e un clic fa quello che fa la scorciatoia.
            var quick = Descendants(bar).OfType<Button>().Where(b => b.Tag is ShortcutAction).ToList();
            Assert.Equal(FullScreenBarLayout.QuickActions, quick.Select(b => (ShortcutAction)b.Tag!));
            Assert.Equal(FullScreenBarLayout.QuickActions.Select(ShortcutMap.Describe), quick.Select(b => b.Text));
            int before = form.ShortcutsExecuted;
            quick.Single(b => (ShortcutAction)b.Tag! == ShortcutAction.NextSession).PerformClick();
            Assert.Equal(before + 1, form.ShortcutsExecuted);
            Assert.Equal(1, form.Tabs.SelectedIndex);

            // Una sessione cliccata nella barra diventa quella visibile; i numeri sono quelli di Ctrl+Alt+N.
            var sessionButtons = Descendants(bar).OfType<Button>().Where(b => b.Tag is RdpSessionView).ToList();
            Assert.Equal(["1  Uno", "2  Due", "3  Tre"], sessionButtons.Select(b => b.Text));
            sessionButtons[2].PerformClick();
            Pump(50);
            Assert.Same(views[2], form.CurrentView);

            // Preferiti nascosti nelle impostazioni: non ci sono.
            Assert.DoesNotContain(BarSection.Favorites, bar.ShownSections);

            // Ricerca fra tutte le connessioni: Invio apre (qui: porta alla sessione già aperta).
            var search = bar.SearchBox!;
            search.Text = "due";
            var list = Descendants(bar).OfType<ListBox>().Single();
            Assert.Equal(["Due"], list.Items.Cast<RdpConnection>().Select(c => c.Name));
            Assert.True(bar.Visible);
        });
    }

    [Fact]
    public void ThumbnailStyle_ShowsAPictureForEachSession()
    {
        WithMainForm(StoreWith(null, Unreachable("Uno"), Unreachable("Due")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { SessionStyle = SessionListStyle.Thumbnails });
            form.ToggleFullScreen();
            Pump(150);
            var pictures = Descendants(form.FullScreenBarControl).OfType<PictureBox>().ToList();
            Assert.Equal(2, pictures.Count);
            Assert.All(pictures, p => Assert.True(p.Width > 50 && p.Height == p.Width * 9 / 16));
        });
    }

    [Fact]
    public void TopEdge_DoesNothing_TheBarSideEdgeOpensTheAutoRevealBar()
    {
        WithMainForm(StoreWith(null, Unreachable("Uno")), form =>
        {
            OpenAllConnected(form);
            form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Position = BarPosition.Left, Mode = BarMode.AutoReveal, RevealDelayMs = 200 });
            form.ToggleFullScreen();
            form.HideFullScreenBarOverlayForTests();
            WindowAttention.BringToFront(form);
            if (!PumpUntil(() => Form.ActiveForm == form, 3000))
            {
                output.WriteLine("Windows non ha dato il primo piano alla finestra di prova: parte con il mouse vero non eseguita.");
                return;
            }
            var saved = Cursor.Position;
            try
            {
                var screen = form.Bounds;
                Cursor.Position = new Point(screen.Left + screen.Width / 2, screen.Top);
                Pump(900);
                Assert.False(form.FullScreenBarControl.Visible, "Il bordo in alto non deve aprire niente");
                Cursor.Position = new Point(screen.Left, screen.Top + screen.Height / 2);
                Assert.True(PumpUntil(() => form.FullScreenBarControl.Visible, 3000), "Il bordo sinistro deve aprire la barra a comparsa");
                Cursor.Position = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
                Assert.True(PumpUntil(() => !form.FullScreenBarControl.Visible, 3000), "Lontano dalla barra deve richiudersi");
            }
            finally
            {
                Cursor.Position = saved;
            }
        });
    }

    [Fact]
    public void SettingsDialog_SavesPositionModeMeasuresStyleAndSectionOrder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new AppSettings();
                using var dlg = new SettingsDialog(settings);
                dlg.Show();
                Application.DoEvents();
                Assert.Equal(3, dlg.BarPositionBox.Items.Count); // sinistra, destra, in basso: niente in alto
                Assert.DoesNotContain(dlg.BarPositionBox.Items.Cast<string>(), t => t.Contains("alto", StringComparison.OrdinalIgnoreCase));
                dlg.BarPositionBox.SelectedIndex = (int)BarPosition.Bottom;
                dlg.BarModeBox.SelectedIndex = (int)BarMode.AutoReveal;
                dlg.BarWidthBox.Value = 200;
                dlg.BarDelayBox.Value = 700;
                dlg.BarStyleBox.SelectedIndex = 0; // con miniatura
                dlg.BarSectionsBox.SelectedIndex = 4; // Pulsanti rapidi
                dlg.MoveSectionForTests(-1);
                dlg.MoveSectionForTests(-1);
                dlg.BarSectionsBox.SetItemChecked(5, false); // Testi rapidi
                Descendants(dlg).OfType<Button>().Single(b => b.Text == "OK").PerformClick();

                var bar = settings.Bar;
                Assert.Equal((BarPosition.Bottom, BarMode.AutoReveal, 200, 700, SessionListStyle.Thumbnails),
                    (bar.Position, bar.Mode, bar.Width, bar.RevealDelayMs, bar.SessionStyle));
                Assert.Equal([BarSection.ConnectedSessions, BarSection.Favorites, BarSection.QuickButtons, BarSection.AllConnections, BarSection.Workspaces, BarSection.QuickTexts],
                    bar.Sections.Select(s => s.Kind));
                Assert.False(bar.Sections[5].Visible);
                Assert.Equal(5, bar.VisibleSections.Count);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }
}
