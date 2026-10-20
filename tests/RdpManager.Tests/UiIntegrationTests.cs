using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.Rdp;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove d'integrazione con la finestra principale vera e il controllo ActiveX RDP di Windows.
/// Usano host irraggiungibili (nessun server RDP necessario) e un archivio temporaneo,
/// quindi non toccano %APPDATA%\RdpManager.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiIntegrationTests : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections)
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList() });
        return store;
    }

    private static RdpConnection Unreachable(string name) =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd" };

    /// <summary>Esegue il corpo su un thread STA (richiesto da WinForms e ActiveX) con la finestra mostrata.</summary>
    private static void WithMainForm(ConnectionStore store, Action<MainForm> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                form.Show();
                Pump(TimeSpan.FromMilliseconds(300));
                body(form);
                form.Close();
                Pump(TimeSpan.FromMilliseconds(200));
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

    private static RdpSessionView OpenAndWaitDisconnected(MainForm form, RdpConnection c)
    {
        form.OpenSession(c);
        var view = Assert.Single(form.AllViews, v => v.Connection == c);
        Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(30)),
            $"Stato atteso Disconnected, trovato {view.State}: {view.StateText}");
        return view;
    }

    [Fact]
    public void MainWindow_ShowsSavedConnectionsInFolders()
    {
        var store = StoreWith(
            new RdpConnection { Name = "Alfa", Host = "a", Group = "Clienti/Rossi" },
            new RdpConnection { Name = "Beta", Host = "b" });

        WithMainForm(store, form =>
        {
            Assert.Equal(2, form.Document.Connections.Count);
            var clienti = Assert.Single(form.Tree.Nodes.Cast<TreeNode>(), n => n.Text == "Clienti");
            var rossi = Assert.Single(clienti.Nodes.Cast<TreeNode>());
            Assert.Equal("Rossi", rossi.Text);
            Assert.Equal("Alfa", Assert.Single(rossi.Nodes.Cast<TreeNode>()).Text);
            Assert.Contains(form.Tree.Nodes.Cast<TreeNode>(), n => n.Text == "Beta");
            Assert.False(form.Tabs.Visible);
        });
    }

    [Fact]
    public void Search_FiltersTreeAndEscapeRestoresIt()
    {
        var store = StoreWith(
            new RdpConnection { Name = "Server Rossi", Host = "10.0.0.1", Group = "Clienti" },
            new RdpConnection { Name = "Server Bianchi", Host = "10.0.0.2", Group = "Clienti" },
            new RdpConnection { Name = "Portatile", Host = "pc-anna" });

        WithMainForm(store, form =>
        {
            static List<string> ConnectionNodes(TreeNodeCollection nodes)
            {
                var names = new List<string>();
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is RdpConnection)
                        names.Add(n.Text);
                    names.AddRange(ConnectionNodes(n.Nodes));
                }
                return names;
            }

            form.SearchBox.Text = "rossi";
            Assert.Equal(["Server Rossi"], ConnectionNodes(form.Tree.Nodes));

            form.SearchBox.Text = "anna";
            Assert.Equal(["Portatile"], ConnectionNodes(form.Tree.Nodes));

            form.SearchBox.Text = "nessuno";
            Assert.Empty(ConnectionNodes(form.Tree.Nodes));

            form.SearchBox.Text = "";
            Assert.Equal(3, ConnectionNodes(form.Tree.Nodes).Count);
        });
    }

    [Fact]
    public void OpenSession_CreatesTabWithActiveXAndShowsDisconnectReason()
    {
        var c = Unreachable("Irraggiungibile");
        WithMainForm(StoreWith(c), form =>
        {
            var conn = form.Document.Connections[0];
            var view = OpenAndWaitDisconnected(form, conn);

            Assert.True(form.Tabs.Visible);
            var page = Assert.Single(form.Tabs.TabPages.Cast<TabPage>());
            Assert.Same(view, page.Tag);
            Assert.Equal((int)SessionState.Disconnected, page.ImageIndex);
            Assert.StartsWith("Irraggiungibile", page.Text);
            Assert.StartsWith("Disconnessa:", view.StateText);
            Assert.Contains(UnresolvableHost, view.StateText); // motivo fornito dal controllo RDP
            Assert.Contains(view.StateText, page.ToolTipText);

            Assert.NotNull(view.Client);
            Assert.True(view.Client!.IsOcxReady);
            Assert.IsAssignableFrom<AxHost>(view.Client);

            // Riaprire la stessa connessione non crea una seconda scheda.
            form.OpenSession(conn);
            Assert.Single(form.Tabs.TabPages.Cast<TabPage>());
        });
    }

    [Fact]
    public void Reconnect_RecreatesControlAndGoesThroughConnecting()
    {
        WithMainForm(StoreWith(Unreachable("R")), form =>
        {
            var view = OpenAndWaitDisconnected(form, form.Document.Connections[0]);
            var firstClient = view.Client;
            var seen = new List<SessionState>();
            view.StateChanged += (_, _) => seen.Add(view.State);

            view.Connect();

            Assert.True(PumpUntil(() => seen.Contains(SessionState.Disconnected), TimeSpan.FromSeconds(30)));
            Assert.Equal(SessionState.Connecting, seen[0]);
            Assert.NotSame(firstClient, view.Client);
            Assert.True(firstClient!.IsDisposed);
        });
    }

    [Fact]
    public void MultipleSessions_CtrlTabCyclesTabs()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            foreach (var c in form.Document.Connections)
                form.OpenSession(c);
            Assert.Equal(3, form.Tabs.TabCount);
            Assert.Equal(3, form.AllViews.Count());
            Assert.Equal(2, form.Tabs.SelectedIndex); // l'ultima aperta è in primo piano

            var route = form.RouteShortcut(Keys.Control | Keys.Tab, form.Handle);
            Assert.Equal(MainForm.ShortcutRoute.Handled, route);
            Assert.Equal(0, form.Tabs.SelectedIndex);
            form.RouteShortcut(Keys.Control | Keys.Tab, form.Handle);
            Assert.Equal(1, form.Tabs.SelectedIndex);
            form.RouteShortcut(Keys.Control | Keys.Shift | Keys.Tab, form.Handle);
            form.RouteShortcut(Keys.Control | Keys.Shift | Keys.Tab, form.Handle);
            Assert.Equal(2, form.Tabs.SelectedIndex);
            Assert.Equal("Tre", form.CurrentView!.Connection.Name);

            Assert.True(PumpUntil(() => form.AllViews.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
        });
    }

    [Fact]
    public void Shortcuts_InsideRemoteSessionGoToRemoteExceptCtrlTab()
    {
        WithMainForm(StoreWith(Unreachable("A"), Unreachable("B")), form =>
        {
            foreach (var c in form.Document.Connections)
                form.OpenSession(c);
            var client = form.CurrentView!.Client!;
            Assert.True(client.IsHandleCreated);
            // Finestra interna creata dal controllo ActiveX (non è un controllo WinForms).
            var inner = FindNativeChild(client.Handle);
            var target = inner != IntPtr.Zero ? inner : client.Handle;

            Assert.Equal(MainForm.ShortcutRoute.ToRemote, form.RouteShortcut(Keys.Control | Keys.N, target));
            Assert.Equal(MainForm.ShortcutRoute.ToRemote, form.RouteShortcut(Keys.F5, target));
            Assert.Equal(MainForm.ShortcutRoute.Handled, form.RouteShortcut(Keys.Control | Keys.Tab, target));
            Assert.Equal(MainForm.ShortcutRoute.Normal, form.RouteShortcut(Keys.Control | Keys.N, form.Tree.Handle));
            Assert.True(PumpUntil(() => form.AllViews.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
        });
    }

    [Fact]
    public void DetachAndReattach_KeepTheSameLiveActiveXControl()
    {
        // 127.0.0.1:1 rifiuta la connessione dopo ~15-20 s: il controllo resta "in connessione"
        // mentre lo spostiamo, e l'evento di disconnessione deve arrivare anche dopo gli spostamenti.
        var c = new RdpConnection { Name = "Locale", Host = "127.0.0.1", Port = 1 };
        WithMainForm(StoreWith(c), form =>
        {
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            Assert.True(PumpUntil(() => view.Client is { IsHandleCreated: true, IsOcxReady: true }, TimeSpan.FromSeconds(10)));
            var client = view.Client!;
            var handle = client.Handle;
            Assert.Equal(SessionState.Connecting, view.State);

            form.ToggleDetach(view);
            Pump(TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, form.Tabs.TabCount);
            var detached = Assert.Single(form.DetachedForms);
            Assert.True(detached.Visible);
            Assert.Same(detached, view.Parent);
            Assert.True(view.IsDetached);
            Assert.Same(client, view.Client);
            Assert.Equal(handle, client.Handle);
            Assert.True(client.IsOcxReady);
            Assert.Equal(SessionState.Connecting, view.State);

            // Chiudere la finestra separata riporta la sessione in una scheda senza disconnetterla.
            detached.Close();
            Pump(TimeSpan.FromMilliseconds(500));
            Assert.Empty(form.DetachedForms);
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.Same(view, form.CurrentView);
            Assert.False(view.IsDetached);
            Assert.False(view.IsDisposed);
            Assert.Same(client, view.Client);
            Assert.Equal(handle, client.Handle);

            // Stacca e riaggancia di nuovo, questa volta con il comando "Riaggancia".
            form.ToggleDetach(view);
            Pump(TimeSpan.FromMilliseconds(300));
            form.ToggleDetach(view);
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.Same(client, view.Client);

            // Gli eventi del controllo arrivano ancora: la sessione era viva durante gli spostamenti.
            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(60)),
                $"Evento di disconnessione non ricevuto dopo gli spostamenti (stato {view.State})");
            Assert.Same(client, view.Client);
        });
    }

    [Fact]
    public void CloseSession_RemovesTabAndDisposesControl()
    {
        WithMainForm(StoreWith(Unreachable("X"), Unreachable("Y")), form =>
        {
            var x = OpenAndWaitDisconnected(form, form.Document.Connections[0]);
            var y = OpenAndWaitDisconnected(form, form.Document.Connections[1]);
            var xClient = x.Client!;

            form.CloseSession(x);
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.True(x.IsDisposed);
            Assert.True(xClient.IsDisposed);
            Assert.Same(y, form.CurrentView);

            form.ToggleDetach(y);
            form.CloseSession(y);
            Assert.Empty(form.DetachedForms);
            Assert.Equal(0, form.Tabs.TabCount);
            Assert.False(form.Tabs.Visible);
            Assert.Empty(form.AllViews);
        });
    }

    [Fact]
    public void ClosingMainWindow_DisposesAllSessionsIncludingDetached()
    {
        var views = new List<RdpSessionView>();
        DetachedSessionForm? detachedForm = null;
        WithMainForm(StoreWith(Unreachable("P"), Unreachable("Q")), form =>
        {
            foreach (var c in form.Document.Connections)
                views.Add(OpenAndWaitDisconnected(form, c));
            form.ToggleDetach(views[1]);
            detachedForm = form.DetachedForms.Single();
        });
        Assert.All(views, v => Assert.True(v.IsDisposed));
        Assert.True(detachedForm!.IsDisposed);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    private static IntPtr FindNativeChild(IntPtr parent)
    {
        var child = FindWindowEx(parent, IntPtr.Zero, null, null);
        if (child == IntPtr.Zero)
            return IntPtr.Zero;
        var deeper = FindNativeChild(child);
        return deeper != IntPtr.Zero ? deeper : child;
    }
}
