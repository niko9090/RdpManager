using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// 1.11.1: alla chiusura la finestra restava bianca e ferma (thread dell'interfaccia bloccato in qualcosa che non finisce).
/// La chiusura deve avere un cane da guardia su un thread suo: se il thread dell'interfaccia è bloccato, il programma esce lo stesso.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class ShutdownHangTests
{
    private static void RunSta(Action body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "RdpManagerShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                TouchUi.TouchScreenForTests = () => false;
                body();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "la prova stessa si è bloccata");
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static MainForm NewForm() =>
        new(new ConnectionStore(Path.Combine(Path.GetTempPath(), "RdpManagerShutdown-" + Guid.NewGuid().ToString("N") + ".json")),
            systemIntegration: false);

    [Fact]
    public void BlockedClosing_ForcesExit_WhileUiThreadIsStillStuck()
    {
        RunSta(() =>
        {
            using var exited = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var form = NewForm();
            form.ShutdownTimeout = TimeSpan.FromMilliseconds(300);
            form.ForcedExit = exited.Set;
            form.ForcedKill = () => { };
            form.Show();
            // Un gestore aggiunto dopo quello del programma che non finisce mai: come un controllo RDP o una chiamata di rete appesi.
            form.FormClosing += (_, _) => release.Wait(TimeSpan.FromSeconds(20));

            var closer = new System.Windows.Forms.Timer { Interval = 10 };
            closer.Tick += (_, _) => { closer.Stop(); form.Close(); };
            closer.Start();
            // L'uscita forzata deve arrivare mentre il thread dell'interfaccia è ancora fermo nel gestore.
            var watcher = Task.Run(() => { var fired = exited.Wait(TimeSpan.FromSeconds(10)); release.Set(); return fired; });
            Application.Run(form);

            Assert.True(watcher.Result, "la chiusura bloccata non ha fatto uscire il programma");
        });
    }

    [Fact]
    public void CancelledClosing_DoesNotForceExit()
    {
        RunSta(() =>
        {
            using var exited = new ManualResetEventSlim();
            using var form = NewForm();
            form.ShutdownTimeout = TimeSpan.FromMilliseconds(400);
            form.ForcedExit = exited.Set;
            form.ForcedKill = () => { };
            form.Show();
            form.FormClosing += (_, e) => e.Cancel = true;
            form.Close();
            Assert.False(exited.Wait(TimeSpan.FromSeconds(1.5)), "chiusura annullata ma il programma è uscito");
        });
    }

    [Fact]
    public void NestedClose_IsIgnored_AndDoesNotStopTheWatchdog()
    {
        RunSta(() =>
        {
            using var exited = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var form = NewForm();
            form.ShutdownTimeout = TimeSpan.FromMilliseconds(500);
            form.ForcedExit = exited.Set;
            form.ForcedKill = () => { };
            form.Show();
            int calls = 0;
            bool inShutdownDuringNested = false;
            form.FormClosing += (_, _) =>
            {
                if (++calls == 1)
                {
                    // Secondo clic sulla X mentre la chiusura è in corso: deve essere ignorato.
                    form.Close();
                    inShutdownDuringNested = form.ShutdownInProgress;
                    release.Wait(TimeSpan.FromSeconds(20));
                }
            };
            var closer = new System.Windows.Forms.Timer { Interval = 10 };
            closer.Tick += (_, _) => { closer.Stop(); form.Close(); };
            closer.Start();
            var watcher = Task.Run(() => { var fired = exited.Wait(TimeSpan.FromSeconds(10)); release.Set(); return fired; });
            Application.Run(form);

            Assert.True(inShutdownDuringNested, "la chiusura annidata ha annullato la chiusura in corso");
            Assert.True(watcher.Result, "la chiusura annidata ha fermato il cane da guardia");
        });
    }

    [Fact]
    public void CancelledClosing_RestoresTimersAndToken()
    {
        RunSta(() =>
        {
            using var form = NewForm();
            form.ForcedExit = () => { };
            form.ForcedKill = () => { };
            form.Show();
            bool cancel = true;
            form.FormClosing += (_, e) => e.Cancel = cancel;
            form.Close();
            Assert.False(form.ShutdownInProgress);
            cancel = false;
            form.Close(); // una seconda chiusura riesce
            Assert.True(form.IsDisposed);
        });
    }

    [Fact]
    public void Watchdog_Kills_EvenIfExitNeverReturns()
    {
        var old = ShutdownWatchdog.KillDelay;
        ShutdownWatchdog.KillDelay = TimeSpan.FromMilliseconds(100);
        try
        {
            using var hang = new ManualResetEventSlim();
            using var killed = new ManualResetEventSlim();
            var dog = ShutdownWatchdog.Start(TimeSpan.FromMilliseconds(100), () => hang.Wait(TimeSpan.FromSeconds(30)), killed.Set);
            Assert.True(killed.Wait(TimeSpan.FromSeconds(5)), "l'uscita è rimasta appesa e la terminazione non è partita");
            Assert.True(dog.Fired);
            hang.Set();
        }
        finally { ShutdownWatchdog.KillDelay = old; }
    }

    [Fact]
    public void DefaultExit_DoesNothing_OutsideTheRealApp()
    {
        Assert.False(ShutdownWatchdog.Enabled);
        ShutdownWatchdog.DefaultExit();
        ShutdownWatchdog.DefaultKill();
    }

    [Fact]
    public void VpnProfile_Save_LeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RdpManagerVpnSave-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "vpn.json");
        try
        {
            new VpnProfile { Address = "10.0.0.2/32" }.Save(path);
            new VpnProfile { Address = "10.0.0.3/32" }.Save(path);
            Assert.Equal("10.0.0.3/32", VpnProfile.Load(path).Address);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Watchdog_Stop_PreventsExit_AndFiringIsLogged()
    {
        using var exited = new ManualResetEventSlim();
        var stopped = ShutdownWatchdog.Start(TimeSpan.FromMilliseconds(200), exited.Set, () => { });
        stopped.Stop();
        Assert.False(exited.Wait(TimeSpan.FromMilliseconds(800)));
        Assert.False(stopped.Fired);

        var log = new List<string>();
        using var fired = new ManualResetEventSlim();
        var dog = ShutdownWatchdog.Start(TimeSpan.FromMilliseconds(100), fired.Set, () => { }, log.Add);
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(dog.Fired);
        Assert.Contains(log, l => l.Contains("uscita forzata"));
    }
}
