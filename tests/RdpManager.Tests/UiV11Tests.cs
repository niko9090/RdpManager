using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.Rdp;
using RdpManager.UI;
using Xunit.Abstractions;

namespace RdpManager.Tests;

/// <summary>
/// Prove della finestra principale 1.1 (schermo intero dell'app, barra di passaggio, barra laterale,
/// scorciatoie anche dentro la sessione, riapertura e riconnessione). Come <see cref="UiIntegrationTests"/>
/// usano host irraggiungibili e un archivio temporaneo; lo stato «connessa» si simula dove serve.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV11Tests(ITestOutputHelper output) : IDisposable
{
    private const string UnresolvableHost = "host-inesistente.invalid";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi11-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(AppSettings? settings, params RdpConnection[] connections)
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList(), Settings = settings ?? new AppSettings() });
        return store;
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections) => StoreWith(null, connections);

    private static RdpConnection Unreachable(string name) =>
        new() { Name = name, Host = UnresolvableHost, Username = "utente", Password = "pwd" };

    /// <summary>127.0.0.1:1 rifiuta dopo ~15-20 s: il controllo resta visibile e «in connessione» per un po'.</summary>
    private static RdpConnection SlowRefusal(string name) =>
        new() { Name = name, Host = "127.0.0.1", Port = 1, Username = "utente", Password = "pwd" };

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
                if (form.IsFullScreen)
                    form.ToggleFullScreen();
                // Le sessioni «connesse» simulate farebbero chiedere conferma alla chiusura.
                foreach (var v in form.AllViews.Where(v => v.State == SessionState.Connected))
                    v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
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

    private static List<RdpSessionView> OpenAll(MainForm form)
    {
        foreach (var c in form.Document.Connections)
            form.OpenSession(c);
        Pump(TimeSpan.FromMilliseconds(200));
        return form.Tabs.TabPages.Cast<TabPage>().Select(p => (RdpSessionView)p.Tag!).ToList();
    }

    private static void SimulateConnected(params RdpSessionView[] views)
    {
        foreach (var v in views)
            v.SetStateForTests(SessionState.Connected, "Connessa");
    }

    private static List<string> SwitchBarButtons(MainForm form) =>
        Descendants(form.SwitchBarControl).OfType<Button>().Where(b => b.Tag is RdpSessionView).Select(b => b.Text).ToList();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    [Fact]
    public void FullScreen_IsABorderlessWindowOnTheChosenMonitorAndRestores()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due")), form =>
        {
            OpenAll(form);
            var before = form.Bounds;
            var border = form.FormBorderStyle;

            form.ToggleFullScreen();
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.True(form.IsFullScreen);
            Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
            var chosen = MonitorLayout.ChooseScreen(Screens.All(), form.Document.Settings.PreferredMonitor)!;
            Assert.Equal(chosen.Bounds, form.Bounds);
            Assert.Equal(chosen.Bounds, form.FullScreenTarget());
            Assert.False(form.MainMenuStrip!.Visible);
            // Stesso TabControl con le stesse sessioni, intestazioni delle schede fuori dall'area visibile.
            Assert.Equal(2, form.Tabs.TabCount);
            Assert.True(form.Tabs.Top < 0, $"Intestazioni ancora visibili: Top = {form.Tabs.Top}");
            Assert.All(form.AllViews, v => Assert.False(v.ToolbarVisible));

            form.ToggleFullScreen();
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.False(form.IsFullScreen);
            Assert.Equal(border, form.FormBorderStyle);
            Assert.Equal(before, form.Bounds);
            Assert.True(form.MainMenuStrip!.Visible);
            Assert.Equal(DockStyle.Fill, form.Tabs.Dock);
            Assert.All(form.AllViews, v => Assert.True(v.ToolbarVisible));
            Assert.True(PumpUntil(() => form.AllViews.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
        });
    }

    [Fact]
    public void FullScreen_SessionUsingAllMonitorsSpansTheVirtualDesktop()
    {
        var all = new RdpConnection { Name = "Tutti", Host = UnresolvableHost, UseAllMonitors = true };
        WithMainForm(StoreWith(all), form =>
        {
            OpenAll(form);
            var screens = Screens.All();
            var expected = screens.Count > 1 ? MonitorLayout.VirtualBounds(screens) : MonitorLayout.ChooseScreen(screens, null)!.Bounds;
            Assert.Equal(expected, form.FullScreenTarget());
        });
    }

    [Fact]
    public void SwitchBar_NumbersOnlyConnectedSessionsLikeTheShortcuts()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            var views = OpenAll(form);
            Assert.True(PumpUntil(() => views.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
            SimulateConnected(views[0], views[2]);

            form.ToggleFullScreen();
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(form.SwitchBarVisible); // compare per un attimo entrando a schermo intero
            form.ShowSwitchBar();
            Assert.Equal(["1  Uno", "Due", "2  Tre"], SwitchBarButtons(form));
            Assert.True(form.SwitchBarControl.Top == 0 && form.SwitchBarControl.Width > 0);

            // Clic vero su «Due» (non connessa): la scheda diventa quella.
            var due = Descendants(form.SwitchBarControl).OfType<Button>().Single(b => b.Text == "Due");
            due.PerformClick();
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, form.Tabs.SelectedIndex);
        });
    }

    [Fact]
    public void Shortcuts_CycleConnectedSessionsAndWorkWithTheFocusInTheRemoteControl()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")), form =>
        {
            var views = OpenAll(form);
            Assert.True(PumpUntil(() => views.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
            SimulateConnected(views[0], views[2]);
            form.Tabs.SelectedIndex = 0;

            form.ExecuteShortcut(ShortcutAction.NextSession);
            Assert.Equal(2, form.Tabs.SelectedIndex); // «Due» non è connessa: si salta
            form.ExecuteShortcut(ShortcutAction.NextSession);
            Assert.Equal(0, form.Tabs.SelectedIndex);
            form.ExecuteShortcut(ShortcutAction.PreviousSession);
            Assert.Equal(2, form.Tabs.SelectedIndex);
            form.ExecuteShortcut(ShortcutAction.Session1);
            Assert.Equal(0, form.Tabs.SelectedIndex);
            form.ExecuteShortcut(ShortcutAction.Session2);
            Assert.Equal(2, form.Tabs.SelectedIndex); // seconda connessa = terza scheda
            form.ExecuteShortcut(ShortcutAction.Session3);
            Assert.Equal(2, form.Tabs.SelectedIndex); // non esiste: resta dov'è

            // Le combinazioni configurabili non vanno al PC remoto nemmeno se il tasto è diretto al controllo.
            var target = form.CurrentView!.Client!.Handle;
            Assert.Equal(MainForm.ShortcutRoute.Handled, form.RouteShortcut(Keys.Control | Keys.Alt | Keys.Left, target));
            Assert.Equal(0, form.Tabs.SelectedIndex);
            Assert.Equal(MainForm.ShortcutRoute.ToRemote, form.RouteShortcut(Keys.Control | Keys.Alt | Keys.End, target));

            Assert.Equal(MainForm.ShortcutRoute.Handled, form.RouteShortcut(Keys.Control | Keys.Alt | Keys.Enter, target));
            Assert.True(form.IsFullScreen);
        });
    }

    /// <summary>
    /// Tastiera vera (SendInput) con il fuoco nella finestra interna del controllo RDP: le scorciatoie sono registrate
    /// con RegisterHotKey mentre l'app è in primo piano (nessun hook di tastiera, tolto nella 1.3).
    /// </summary>
    [Fact]
    public void RegisterHotKey_CtrlAltRightTypedInsideTheRemoteSessionSwitchesTab()
    {
        WithMainForm(StoreWith(SlowRefusal("Uno"), SlowRefusal("Due")), form =>
        {
            var views = OpenAll(form);
            Assert.True(PumpUntil(() => views.All(v => v.Client is { IsOcxReady: true, IsHandleCreated: true }), TimeSpan.FromSeconds(10)));
            SimulateConnected(views[0], views[1]);
            form.Tabs.SelectedIndex = 0;
            Pump(TimeSpan.FromMilliseconds(200));

            WindowAttention.BringToFront(form);
            if (!PumpUntil(() => IsOwnForeground() && form.SessionHotkeysRegistered, TimeSpan.FromSeconds(3)))
            {
                output.WriteLine("Windows non ha dato il primo piano alla finestra di prova: parte con tastiera vera non eseguita.");
                return;
            }
            views[0].FocusSession();
            Assert.True(PumpUntil(() => IsInside(GetFocus(), views[0].Client!.Handle), TimeSpan.FromSeconds(3)),
                "Il fuoco non è entrato nel controllo RDP");

            int before = form.ShortcutsExecuted;
            SendCombo(0x11, 0x12, 0x27); // Ctrl+Alt+Destra
            Assert.True(PumpUntil(() => form.Tabs.SelectedIndex == 1, TimeSpan.FromSeconds(3)),
                $"Ctrl+Alt+Destra non ha cambiato scheda (scorciatoie eseguite: {form.ShortcutsExecuted - before})");
            Assert.Equal(before + 1, form.ShortcutsExecuted);

            // Fuori dal primo piano le combinazioni tornano libere per gli altri programmi.
            PostActivateApp(form, active: false);
            Assert.True(PumpUntil(() => !form.SessionHotkeysRegistered, TimeSpan.FromSeconds(2)));
        });
    }

    private static void PostActivateApp(Form form, bool active) =>
        SendMessage(form.Handle, 0x001C /* WM_ACTIVATEAPP */, active ? 1 : 0, 0);

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    /// <summary>Tastiera vera: Ctrl+Tab con il fuoco nell'elenco passa dal filtro dei messaggi e cambia scheda.</summary>
    [Fact]
    public void ShortcutFilter_RealCtrlTabFromTheTreeSwitchesTab()
    {
        WithMainForm(StoreWith(Unreachable("Uno"), Unreachable("Due")), form =>
        {
            OpenAll(form);
            form.Tabs.SelectedIndex = 0;
            WindowAttention.BringToFront(form);
            if (!PumpUntil(IsOwnForeground, TimeSpan.FromSeconds(3)))
            {
                output.WriteLine("Windows non ha dato il primo piano alla finestra di prova: parte con tastiera vera non eseguita.");
                return;
            }
            form.Tree.Focus();
            Assert.True(PumpUntil(() => form.Tree.Focused, TimeSpan.FromSeconds(2)));
            SendCombo(0x11, 0x09); // Ctrl+Tab
            Assert.True(PumpUntil(() => form.Tabs.SelectedIndex == 1, TimeSpan.FromSeconds(3)), "Ctrl+Tab non ha cambiato scheda");
        });
    }

    [Fact]
    public void Sidebar_PinnedCanBeHiddenAndAutoHideOverlaysTheSessionsAlsoInFullScreen()
    {
        WithMainForm(StoreWith(Unreachable("Uno")), form =>
        {
            OpenAll(form);
            Assert.True(form.SidebarShown);
            Assert.Equal(DockStyle.Left, form.Sidebar.Dock);
            int contentLeftPinned = form.Tabs.PointToScreen(Point.Empty).X;

            form.ToggleSidebar();
            Assert.False(form.SidebarShown);
            form.ToggleSidebar();
            Assert.True(form.SidebarShown);

            form.SetSidebarModeForTests(SidebarMode.AutoHide);
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(form.SidebarShown);
            Assert.True(form.Tabs.PointToScreen(Point.Empty).X < contentLeftPinned, "La sessione deve allargarsi senza la barra fissa");
            form.ShowSidebarForTests();
            Assert.True(form.SidebarShown);
            Assert.Equal(DockStyle.None, form.Sidebar.Dock);
            Assert.Equal(0, form.Sidebar.Parent!.Controls.GetChildIndex(form.Sidebar)); // sopra le sessioni
            Assert.Equal(SidebarMode.AutoHide, form.Document.Settings.SidebarMode);

            form.ToggleSidebar();
            Assert.False(form.SidebarShown);
            form.ToggleFullScreen();
            form.ToggleSidebar();
            Assert.True(form.SidebarShown, "La barra laterale deve essere disponibile anche a schermo intero");
            Assert.True(form.Sidebar.Bounds.IntersectsWith(form.Sidebar.Parent.ClientRectangle));
        });
    }

    [Fact]
    public void Tree_ShowsFavoritesAndRecentAtTheTopButNotWhileSearching()
    {
        var fav = new RdpConnection { Name = "Server preferito", Host = "a", IsFavorite = true, Label = "PROD", Group = "Clienti" };
        var recent = new RdpConnection { Name = "Usato ieri", Host = "b", LastConnectedUtc = DateTime.UtcNow.AddDays(-1) };
        var other = new RdpConnection { Name = "Altro", Host = "c" };
        WithMainForm(StoreWith(fav, recent, other), form =>
        {
            var top = form.Tree.Nodes.Cast<TreeNode>().ToList();
            Assert.Equal("Preferiti", top[0].Text);
            Assert.Equal(["Server preferito  [PROD]"], top[0].Nodes.Cast<TreeNode>().Select(n => n.Text));
            Assert.Equal("Recenti", top[1].Text);
            Assert.Equal(["Usato ieri"], top[1].Nodes.Cast<TreeNode>().Select(n => n.Text));
            Assert.Contains(top, n => n.Text == "Clienti");

            form.SearchBox.Text = "prod"; // anche l'etichetta si cerca
            var found = form.Tree.Nodes.Cast<TreeNode>().ToList();
            Assert.DoesNotContain(found, n => n.Text is "Preferiti" or "Recenti");
            var folder = Assert.Single(found);
            Assert.Equal("Clienti", folder.Text);
            Assert.Equal("Server preferito  [PROD]", Assert.Single(folder.Nodes.Cast<TreeNode>()).Text);
        });
    }

    [Fact]
    public void ConnectFolder_OpensEveryConnectionOfTheFolderAndSubfolders()
    {
        WithMainForm(StoreWith(
            new RdpConnection { Name = "A", Host = UnresolvableHost, Group = "Ufficio" },
            new RdpConnection { Name = "B", Host = UnresolvableHost, Group = "Ufficio/Piano 2" },
            new RdpConnection { Name = "C", Host = UnresolvableHost, Group = "Casa" }), form =>
        {
            form.ConnectFolder("Ufficio");
            Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(["A", "B"], form.AllViews.Select(v => v.Connection.Name).Order());
            Assert.True(PumpUntil(() => form.AllViews.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
        });
    }

    [Fact]
    public void AutoReconnect_RetriesUpToTheLimitThenStops()
    {
        var c = new RdpConnection { Name = "Instabile", Host = UnresolvableHost, AutoReconnect = true, AutoReconnectMaxAttempts = 2 };
        WithMainForm(StoreWith(c), form =>
        {
            form.OpenSession(form.Document.Connections[0]);
            var view = Assert.Single(form.AllViews);
            Assert.True(PumpUntil(() => view.ReconnectPending, TimeSpan.FromSeconds(30)), view.StateText);
            Assert.Contains("nuovo tentativo", view.StateText);
            Assert.Equal(1, view.ReconnectAttempts);
            Assert.True(PumpUntil(() => view.ReconnectAttempts == 2 && view.State == SessionState.Disconnected && !view.ReconnectPending,
                TimeSpan.FromSeconds(60)), $"{view.ReconnectAttempts}: {view.StateText}");
            Pump(TimeSpan.FromSeconds(5)); // nessun terzo tentativo
            Assert.Equal(2, view.ReconnectAttempts);
            Assert.False(view.ReconnectPending);
            Assert.DoesNotContain("nuovo tentativo", view.StateText);
        });
    }

    [Fact]
    public void ReopenSessions_RestoresTabsInOrderAndRemembersThemOnClose()
    {
        var a = Unreachable("Primo");
        var b = Unreachable("Secondo");
        var settings = new AppSettings { ReopenSessionsOnStartup = true, OpenSessions = [b.Id, a.Id] };
        var store = StoreWith(settings, a, b);
        WithMainForm(store, form =>
        {
            Assert.True(PumpUntil(() => form.Tabs.TabCount == 2, TimeSpan.FromSeconds(5)));
            Assert.Equal(["Secondo", "Primo"], form.AllViews.Select(v => v.Connection.Name));
            form.CloseSession(form.AllViews.First());
            Assert.True(PumpUntil(() => form.AllViews.All(v => v.State == SessionState.Disconnected), TimeSpan.FromSeconds(30)));
        });
        Assert.Equal([a.Id], store.Load().Settings.OpenSessions);
    }

    [Fact]
    public void CopyInto_CopiesEveryEditableFieldButTheId()
    {
        var target = new RdpConnection { Name = "Vecchio", Host = "x" };
        var id = target.Id;
        var edited = new RdpConnection
        {
            Name = "Nuovo", Host = "y", Port = 3390, AudioMode = AudioMode.None, RedirectPrinters = true, RedirectMicrophone = true,
            UseAllMonitors = true, AutoReconnect = true, AutoReconnectMaxAttempts = 7, IsFavorite = true, Color = "#AA0000",
            Label = "TEST", MacAddress = "AA:BB:CC:DD:EE:FF", Password = "segreta",
        };
        MainForm.CopyInto(edited, target);
        Assert.Equal(id, target.Id);
        foreach (var p in typeof(RdpConnection).GetProperties().Where(p => p.CanWrite && p.Name != nameof(RdpConnection.Id)))
            Assert.Equal(p.GetValue(edited), p.GetValue(target));
    }

    // ---------- tastiera vera ----------

    private static bool IsOwnForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsInside(IntPtr hwnd, IntPtr ancestor)
    {
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
            if (h == ancestor)
                return true;
        return false;
    }

    private static void SendCombo(params ushort[] keys)
    {
        var inputs = keys.Select(k => Key(k, up: false)).Concat(keys.Reverse().Select(k => Key(k, up: true))).ToArray();
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()));
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (vk is 0x25 or 0x26 or 0x27 or 0x28 ? 1u : 0u) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
