using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove della finestra principale 1.4: blocco con PIN, spazi di lavoro, selezione multipla con azioni di gruppo,
/// anteprima dell'importazione CSV e riepilogo F1. Host irraggiungibili, dati temporanei, nessuna rete vera.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV14Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi14-" + Guid.NewGuid().ToString("N"));

    public UiV14Tests() => Directory.CreateDirectory(_dir);

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

    private static RdpConnection Unreachable(string name, string group = "", string? password = null) =>
        new() { Name = name, Host = name.ToLowerInvariant() + ".invalid", Username = "utente", Password = password, Group = group };

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try { body(); } catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void WithMainForm(ConnectionStore store, Action<MainForm> body) => OnSta(() =>
    {
        using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.StartInstaller = _ => throw new InvalidOperationException("nelle prove non si avvia nessun installer");
        form.Show();
        Pump(400);
        body(form);
        if (!form.IsDisposed)
        {
            if (form.IsSplit)
                form.LeaveSplit();
            if (form.IsFullScreen)
                form.ToggleFullScreen();
            foreach (var v in form.AllViews.Where(v => v.State == SessionState.Connected))
                v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
            form.ExitApp();
        }
        Pump(200);
    });

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

    // ---------- blocco con PIN ----------

    [Fact]
    public void Lock_StartsLockedWithAPin_PasswordsSealedUntilTheRightPin()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Server", password: "Segreta-1")] };
        doc.Settings.LockPin = PinLock.Create("2580", 10_000);
        WithMainForm(StoreWith(doc), form =>
        {
            Assert.True(form.IsLocked);
            Assert.True(PumpUntil(() => form.LockScreen is { Visible: true } && !form.Visible, 3000), "Schermata di blocco non comparsa");
            var c = form.Document.Connections[0];
            Assert.Null(c.Password);                 // ancora cifrata
            Assert.NotNull(c.ProtectedPassword);

            form.OpenSession(c);                       // da bloccata non si apre nulla
            Assert.Empty(form.AllViews);

            var lockScreen = form.LockScreen!;
            lockScreen.PinBox.Text = "1111";
            lockScreen.TryUnlock();
            Assert.True(form.IsLocked);
            Assert.Equal("PIN sbagliato.", lockScreen.ErrorText);

            lockScreen.PinBox.Text = "2580";
            lockScreen.TryUnlock();
            Assert.True(PumpUntil(() => !form.IsLocked && form.Visible && form.LockScreen is null, 3000));
            Assert.Equal("Segreta-1", c.Password);    // decifrata solo ora

            // Ctrl+Alt+L (o il menu) blocca di nuovo: le password tornano cifrate e la finestra si nasconde.
            form.ExecuteShortcut(ShortcutAction.Lock);
            Assert.True(form.IsLocked);
            Assert.Null(c.Password);
            Assert.False(form.Visible);
            form.LockScreen!.PinBox.Text = "2580";
            form.LockScreen.TryUnlock();
            Assert.True(PumpUntil(() => !form.IsLocked, 3000));
        });
    }

    [Fact]
    public void Lock_AfterInactivity_AndAForgottenPinKeepsConnectionsButNotPasswords()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno", password: "P1"), Unreachable("Due")] };
        doc.Settings.LockPin = PinLock.Create("2580", 10_000);
        doc.Settings.LockAfterMinutes = 5;
        var store = StoreWith(doc);
        WithMainForm(store, form =>
        {
            form.LockScreen!.PinBox.Text = "2580";
            form.LockScreen.TryUnlock();
            Assert.True(PumpUntil(() => !form.IsLocked, 3000));

            form.IdleOverride = () => TimeSpan.FromMinutes(4);
            form.CheckIdleLock();
            Assert.False(form.IsLocked);
            form.IdleOverride = () => TimeSpan.FromMinutes(6);
            form.CheckIdleLock();
            Assert.True(form.IsLocked);

            form.ForgetPinConfirmed();
            Assert.False(form.IsLocked);
            Assert.Equal(2, form.Document.Connections.Count);
            Assert.All(form.Document.Connections, c => Assert.False(c.HasPassword));
            Assert.False(form.Document.Settings.LockEnabled);
        });
        var saved = store.Load();
        Assert.Equal(2, saved.Connections.Count);
        Assert.Null(saved.Settings.LockPin);
        Assert.All(saved.Connections, c => Assert.Null(c.ProtectedPassword));
    }

    // ---------- spazi di lavoro ----------

    [Fact]
    public void Workspace_SaveCurrentLayoutAndReopenIt()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno"), Unreachable("Due"), Unreachable("Tre")] };
        var store = StoreWith(doc);
        WithMainForm(store, form =>
        {
            var c = form.Document.Connections;
            form.OpenSession(c[2]);
            form.OpenSession(c[0]);
            Pump(200);
            var views = form.AllViews.ToList();
            form.EnterSplit(views[0], views[1]);
            var ws = form.Document.SaveWorkspace("Prova", form.CurrentLayout());
            Assert.Equal([c[2].Id, c[0].Id], ws.Connections);
            Assert.Equal(c[2].Id, ws.SplitLeft);

            // Chiudo tutto e riapro lo spazio: stesse sessioni, stesso ordine, vista affiancata.
            form.LeaveSplit();
            foreach (var v in form.AllViews.ToList())
                form.CloseSession(v);
            Assert.Empty(form.AllViews);
            form.OpenWorkspace(ws);
            Pump(300);
            Assert.Equal([c[2], c[0]], form.AllViews.Select(v => v.Connection));
            Assert.True(form.IsSplit);
            Assert.Same(c[2], form.SplitViews.Left!.Connection);
            Assert.Same(c[0], form.SplitViews.Right!.Connection);
        });
        Assert.Equal("Prova", Assert.Single(store.Load().Workspaces).Name);
    }

    // ---------- selezione multipla e azioni di gruppo ----------

    [Fact]
    public void MultiSelection_CtrlAndShiftClick_GroupConnectAndDelete()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Alfa", "G"), Unreachable("Bravo", "G"), Unreachable("Charlie", "G"), Unreachable("Delta", "G")] };
        WithMainForm(StoreWith(doc), form =>
        {
            var c = form.Document.Connections.ToList();
            form.SelectForTests(c[0], Keys.None);
            form.SelectForTests(c[2], Keys.Shift);            // Alfa..Charlie
            Assert.Equal(3, form.MultiSelection.Count);
            form.SelectForTests(c[1], Keys.Control);          // tolgo Bravo
            Assert.Equal([c[0], c[2]], form.SelectedConnections);
            form.SelectForTests(c[3], Keys.Control);          // aggiungo Delta
            Assert.Equal(3, form.SelectedConnections.Count);

            form.ConnectGroup();
            Pump(200);
            Assert.Equal(3, form.AllViews.Count());

            string? asked = null;
            form.DeleteGroup(q => { asked = q; return false; });   // annullato
            Assert.Equal(4, form.Document.Connections.Count);
            Assert.Contains("3 connessioni: Alfa, Charlie, Delta", asked);
            form.DeleteGroup(_ => true);
            Assert.Equal(["Bravo"], form.Document.Connections.Select(x => x.Name));
            Assert.Empty(form.MultiSelection);

            form.SelectForTests(c[1], Keys.None);              // clic semplice: niente selezione multipla
            Assert.Empty(form.MultiSelection);
        });
    }

    // ---------- CSV ----------

    [Fact]
    public void CsvDialog_PreviewFollowsTheColumnMapping()
    {
        OnSta(() =>
        {
            var doc = new ConnectionsDocument { Connections = [new RdpConnection { Host = "gia.example.test" }] };
            var table = CsvImport.Parse("Descrizione;Server;Altro\nUfficio;uff.example.test;x\nVecchio;gia.example.test;y\nVuota;;z\n");
            using var dlg = new CsvImportDialog("prova.csv", table, doc, "Importate");
            dlg.Show();
            Pump(200);
            Assert.Equal(["Nome", "Host", "(ignora)"], dlg.MappingBoxes.Select(b => b.Text));
            Assert.Equal(1, dlg.Rows.Count(r => r.CanImport));
            Assert.Equal(3, dlg.PreviewList.Items.Count);
            Assert.Contains("c'è già", dlg.PreviewList.Items[1].SubItems[6].Text);
            Assert.Contains("host mancante", dlg.PreviewList.Items[2].SubItems[6].Text);

            dlg.MappingBoxes[1].SelectedIndex = (int)CsvField.Ignore;   // senza colonna Host nessuna riga è valida
            Pump(100);
            Assert.Equal(0, dlg.Rows.Count(r => r.CanImport));
            dlg.Close();
        });
    }

    [Fact]
    public void CsvFile_InLatin1OrUtf8_IsReadCorrectly()
    {
        var utf8 = Path.Combine(_dir, "utf8.csv");
        File.WriteAllText(utf8, "Nome;Host\nCittà;pc.example.test\n", new System.Text.UTF8Encoding(true));
        var latin = Path.Combine(_dir, "latin1.csv");
        File.WriteAllBytes(latin, System.Text.Encoding.Latin1.GetBytes("Nome;Host\nCittà;pc.example.test\n"));
        Assert.Contains("Città", MainForm.ReadText(utf8));
        Assert.Contains("Città", MainForm.ReadText(latin));
    }

    // ---------- F1 ----------

    [Fact]
    public void F1_ShowsAllShortcutsGroupedBySection()
    {
        var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")] };
        doc.Workspaces.Add(new Workspace { Name = "Mattina", Shortcut = "Ctrl+Alt+Maiusc+1", Connections = [doc.Connections[0].Id] });
        WithMainForm(StoreWith(doc), form =>
        {
            var lines = form.ShortcutLines();
            using var dlg = new ShortcutsDialog(lines);
            dlg.Show();
            Pump(100);
            Assert.Equal(lines.Count, dlg.List.Items.Count);
            Assert.Contains(dlg.List.Items.Cast<ListViewItem>(), i => i.Text == "Ctrl+Alt+L");
            Assert.Contains(dlg.List.Items.Cast<ListViewItem>(), i => i.Text == "Ctrl+Alt+Maiusc+1");
            Assert.True(dlg.List.Groups.Count >= 3);
            dlg.Close();
        });
    }

    [Fact]
    public void NewDialogsOpenWithoutErrors()
    {
        OnSta(() =>
        {
            var doc = new ConnectionsDocument { Connections = [Unreachable("Uno")] };
            doc.Workspaces.Add(new Workspace { Name = "W" });
            Form[] dialogs =
            [
                new PinSetDialog(),
                new WorkspacesDialog(doc),
                new ChoiceDialog("Prova", "Scegli", ["a", "b"], "b", editable: false),
                new SettingsDialog(new AppSettings { LockPin = PinLock.Create("2580", 10_000) }),
                new ConnectionDialog(doc.Connections[0], doc.AllGroups(), "Modifica"),
            ];
            foreach (var d in dialogs)
            {
                d.StartPosition = FormStartPosition.Manual;
                d.Show();
                Pump(100);
                Assert.True(d.Visible);
                d.Close();
                d.Dispose();
            }
        });
    }
}
