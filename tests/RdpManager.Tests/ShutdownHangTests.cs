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
