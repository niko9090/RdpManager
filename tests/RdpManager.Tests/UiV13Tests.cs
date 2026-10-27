using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove della finestra principale 1.3: errori di connessione spiegati, riordino trascinando, log senza password né
/// host, aggiornamento automatico con conferma (senza eseguire nessun installer vero). Host irraggiungibili, dati
/// temporanei.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV13Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi13-" + Guid.NewGuid().ToString("N"));

    public UiV13Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AppLog.Default = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections)
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList() });
        return store;
    }

    private static RdpConnection Unreachable(string name, string group = "") =>
        new() { Name = name, Host = "host-inesistente.invalid", Username = "utente", Password = "Pwd-Mai-Nei-Log", Group = group };

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            // Un'eccezione dell'interfaccia deve far fallire la prova, non aprire il dialogo di .NET e bloccarla.
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
        Pump(300);
        body(form);
        if (!form.IsDisposed)
        {
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

    // ---------- errori spiegati ----------

    [Fact]
    public void ADisconnectionShowsCauseAndSolution()
    {
        WithMainForm(StoreWith(Unreachable("Server")), form =>
        {
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, 30_000), "La sessione verso un host inesistente non si è chiusa");
            Assert.NotNull(view.LastError);
            Assert.Equal(ConnectionErrorKind.Network, view.LastError!.Kind);
            Assert.Contains("Causa probabile:", view.MessageText);
            Assert.Contains("Cosa fare:", view.MessageText);
            Assert.Contains("Riconnetti", view.MessageText);
        });
    }

    // ---------- riordino ----------

    [Fact]
    public void DraggingAConnectionReordersTheTree()
    {
        WithMainForm(StoreWith(Unreachable("Alfa", "G"), Unreachable("Bravo", "G"), Unreachable("Charlie", "G")), form =>
        {
            List<string> Names() => form.Tree.Nodes.Cast<TreeNode>().Single(n => n.Text == "G").Nodes.Cast<TreeNode>().Select(n => n.Text).ToList();
            Assert.Equal(["Alfa", "Bravo", "Charlie"], Names());
            var docs = form.Document.Connections;
            var charlie = docs.Single(c => c.Name == "Charlie");
            var alfa = docs.Single(c => c.Name == "Alfa");
            Assert.True(form.DropConnection(charlie, alfa));
            form.RefreshTreeForTests();
            Assert.Equal(["Charlie", "Alfa", "Bravo"], Names());
            Assert.Equal(ConnectionOrder.Manual, form.Document.Settings.ConnectionOrder);
            Assert.False(form.DropConnection(charlie, charlie));
        });
    }

    // ---------- log ----------

    [Fact]
    public void SessionEventsAreLogged_WithoutPasswordsOrHosts()
    {
        var log = new AppLog(Path.Combine(_dir, "logs"));
        AppLog.Default = log;
        var c = Unreachable("Server segreto");
        WithMainForm(StoreWith(c), form =>
        {
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, 30_000));
            view.SetStateForTests(SessionState.Connected, "Connessa");
            view.SetStateForTests(SessionState.Disconnected, "Disconnessa");
        });
        var text = File.ReadAllText(log.CurrentPath, Encoding.UTF8);
        Assert.Contains("Disconnessa " + AppLog.Ref(c), text);
        Assert.Contains("Connessa " + AppLog.Ref(c), text);
        Assert.DoesNotContain("Pwd-Mai-Nei-Log", text);
        Assert.DoesNotContain("host-inesistente", text);
        Assert.DoesNotContain("Server segreto", text);
    }

    // ---------- aggiornamento automatico con conferma ----------

    /// <summary>Installazione finta in una cartella temporanea: il programma di aggiornamento ne fa la copia di riserva.</summary>
    private void UseFakeInstall(MainForm form)
    {
        var install = Path.Combine(_dir, "installata");
        Directory.CreateDirectory(Path.Combine(install, "runtimes"));
        File.WriteAllText(Path.Combine(install, UpdateJob.ExeName), "MZ app finta");
        File.WriteAllText(Path.Combine(install, InstallLocation.UninstallerName), "MZ disinstallatore finto");
        File.WriteAllText(Path.Combine(install, "runtimes", "libreria.dll"), "dll finta");
        form.InstallDirOverride = install;
        form.UpdateRunnerFolder = Path.Combine(_dir, "update-runner");
    }

    private PendingUpdate PrepareVerified(MainForm form, string version = "9.9.9")
    {
        var path = Path.Combine(_dir, $"RdpManager-Setup-{version}.exe");
        File.WriteAllText(path, "MZ installer finto " + version);
        var release = new ReleaseInfo($"v{version}", new Version(version), $"https://github.com/niko9090/RdpManager/releases/tag/v{version}", null)
        {
            Notes = "## Novità\n- prova",
        };
        Assert.True(form.Updates.Accept(release, new UpdateDownloadResult(UpdateDownloadStatus.Verified, path, "ok", Sha256Sums.ComputeHex(path))));
        return form.Updates.Ready!;
    }

    [Fact]
    public void UpdateOffer_LaterAndOnExit()
    {
        var started = new List<ProcessStartInfo>();
        OnSta(() =>
        {
            using var form = new MainForm(StoreWith(Unreachable("Uno"))) { StartPosition = FormStartPosition.Manual };
            form.StartInstaller = started.Add;
            form.PortableOverride = false;
            UseFakeInstall(form);
            form.Show();
            Pump(300);
            PrepareVerified(form);

            form.ShowUpdateOffer();
            Assert.True(PumpUntil(() => form.UpdateOffer is { Visible: true }, 3000));
            form.UpdateOffer!.Finish(UpdateChoice.Later);
            Pump(100);
            Assert.False(form.Updates.ShouldOffer(DateTime.UtcNow));
            Assert.True(form.Updates.ShouldOffer(DateTime.UtcNow.AddHours(25)));

            form.ShowUpdateOffer();
            Assert.True(PumpUntil(() => form.UpdateOffer is { Visible: true }, 3000));
            form.UpdateOffer!.Finish(UpdateChoice.OnExit);
            Pump(100);
            Assert.True(form.Updates.InstallOnExit);
            Assert.Empty(started);
            form.ExitApp(); // alla chiusura: installa senza riaprire
            Pump(200);
            Assert.True(form.IsDisposed);
        });
        // Alla chiusura parte il programma di aggiornamento (la copia di riserva), che installa senza riaprire.
        var psi = Assert.Single(started);
        Assert.Equal(Path.Combine(_dir, "update-runner", "riserva", UpdateJob.ExeName), psi.FileName);
        var job = UpdateJob.TryParse(psi.ArgumentList.ToList());
        Assert.NotNull(job);
        Assert.False(job.Relaunch);
        Assert.EndsWith("RdpManager-Setup-9.9.9.exe", job.InstallerPath);
        Assert.Equal(new Version(9, 9, 9), job.ExpectedVersion);
        Assert.Equal(Environment.ProcessId, job.AppProcessId);
        Assert.True(File.Exists(Path.Combine(job.BackupDir, "runtimes", "libreria.dll")));
    }

    [Fact]
    public void UpdateNow_SavesSessionsForReopening_AndStartsTheUpdaterThatReopensTheApp()
    {
        var started = new List<ProcessStartInfo>();
        var store = StoreWith(Unreachable("Uno"), Unreachable("Due"));
        Guid[] opened = [];
        string? asked = null;
        OnSta(() =>
        {
            using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual };
            form.StartInstaller = started.Add;
            form.PortableOverride = false;
            UseFakeInstall(form);
            form.ConfirmOverride = q => { asked = q; return true; };
            form.Show();
            Pump(300);
            foreach (var c in form.Document.Connections)
                form.OpenSession(c);
            Pump(200);
            opened = form.AllViews.Select(v => v.Connection.Id).ToArray();
            PrepareVerified(form);
            form.InstallNow();
            Pump(300);
            Assert.True(form.IsDisposed);
        });
        Assert.Contains("2 sessioni aperte verranno chiuse", asked);
        var psi = Assert.Single(started);
        var job = UpdateJob.TryParse(psi.ArgumentList.ToList());
        Assert.NotNull(job);
        Assert.True(job.Relaunch);
        Assert.Equal(Path.Combine(_dir, "installata"), job.InstallDir);
        Assert.Equal(Path.GetFullPath(store.FilePath), job.DataFile); // riapre lo stesso elenco
        Assert.DoesNotContain("/RELAUNCH", job.InstallerArguments()); // riapre il programma di aggiornamento, non l'installer
        Assert.Contains("/LOG=", job.InstallerArguments());
        Assert.True(File.Exists(Path.Combine(job.BackupDir, UpdateJob.ExeName)));

        var saved = store.Load().Settings;
        Assert.True(saved.ReopenOnceAfterUpdate);
        Assert.Equal(opened, saved.OpenSessions);
        Assert.False(saved.ReopenSessionsOnStartup); // la riapertura normale resta com'era

        // Al riavvio (dopo l'aggiornamento) le sessioni si riaprono una volta.
        OnSta(() =>
        {
            using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual };
            form.Show();
            Assert.True(PumpUntil(() => form.AllViews.Count() == 2, 5000));
            form.ExitApp();
            Pump(200);
        });
        Assert.False(store.Load().Settings.ReopenOnceAfterUpdate);
    }

    [Fact]
    public void UpdateNow_ATamperedInstallerIsNeverStarted()
    {
        var started = new List<ProcessStartInfo>();
        OnSta(() =>
        {
            using var form = new MainForm(StoreWith(Unreachable("Uno"))) { StartPosition = FormStartPosition.Manual };
            form.StartInstaller = started.Add;
            form.PortableOverride = false;
            UseFakeInstall(form);
            form.Show();
            Pump(300);
            var ready = PrepareVerified(form);
            form.Updates.Choose(UpdateChoice.OnExit, DateTime.UtcNow);
            File.AppendAllText(ready.InstallerPath, "manomesso"); // cambiato dopo la verifica
            form.ExitApp();
            Pump(200);
            Assert.True(form.IsDisposed);
            Assert.False(File.Exists(ready.InstallerPath));
        });
        Assert.Empty(started);
    }
}
