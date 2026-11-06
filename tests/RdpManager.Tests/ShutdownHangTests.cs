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

    private static bool PumpUntil(Func<bool> condition, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return condition();
    }

    /// <summary>Apre una sessione verso un host che non esiste e la porta in uno stato fermo (nessuna riconnessione in corso).</summary>
    private static RdpSessionView OpenSettledView(MainForm form)
    {
        var connection = new RdpConnection { Name = "prova", Host = "host-inesistente.invalid", Username = "u", Password = "p" };
        form.Document.Connections.Add(connection);
        form.OpenSession(connection);
        var view = form.AllViews.Single();
        Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, 30_000), "la sessione non si è fermata");
        view.Disconnect(); // ferma la riconnessione automatica
        return view;
    }

    private static void CloseCleanly(MainForm form)
    {
        foreach (var v in form.AllViews.ToList())
            v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
        form.Close();
        PumpUntil(() => form.IsDisposed, 2000);
    }

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
    public void CancelAlreadySetOnEntry_HasNoEffects()
    {
        RunSta(() =>
        {
            using var exited = new ManualResetEventSlim();
            using var form = NewForm();
            form.ShutdownTimeout = TimeSpan.FromMilliseconds(300);
            form.ForcedExit = exited.Set;
            form.ForcedKill = () => { };
            form.Show();
            var view = OpenSettledView(form);
            view.SetStateForTests(SessionState.Connecting, "Connessione"); // se la chiusura la toccasse, cambierebbe
            var token = form.ShutdownToken;

            // WinForms preimposta e.Cancel (Validate fallita, finestre possedute che annullano): nessun effetto.
            var e = new FormClosingEventArgs(CloseReason.UserClosing, cancel: true);
            form.RaiseClosingForTests(e);

            Assert.True(e.Cancel);
            Assert.False(form.ShutdownInProgress);
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(SessionState.Connecting, view.State);
            Assert.False(exited.Wait(TimeSpan.FromSeconds(1)), "chiusura annullata ma il programma è uscito");
            CloseCleanly(form);
        });
    }

    [Fact]
    public void CancelAfterPointOfNoReturn_IsIgnored()
    {
        RunSta(() =>
        {
            using var form = NewForm();
            form.ForcedExit = () => { };
            form.ForcedKill = () => { };
            form.Show();
            // Un gestore che arriva dopo quello del programma e prova ad annullare: troppo tardi, gli effetti sono già avvenuti.
            form.FormClosing += (_, e) => e.Cancel = true;
            form.Close();
            Assert.True(form.IsDisposed, "la chiusura è stata annullata dopo il punto di non ritorno");
        });
    }

    [Fact]
    public void NestedClose_DoesNotLeakItsFlagsIntoTheOuterClose()
    {
        RunSta(() =>
        {
            using var form = NewForm();
            form.ForcedExit = () => { };
            form.ForcedKill = () => { };
            form.Show();
            bool innerCancelled = false, entered = false;
            form.FormClosing += (_, _) =>
            {
                if (entered)
                    return;
                entered = true;
                // Chiusura annidata nel gestore della prima: viene annullata (da noi) ma non deve toccare quella esterna.
                var inner = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
                form.RaiseClosingForTests(inner);
                innerCancelled = inner.Cancel;
            };
            form.Close();
            Assert.True(innerCancelled);
            Assert.True(form.IsDisposed, "la chiusura annidata ha annullato quella esterna");
        });
    }

    [Fact]
    public void WindowsShutdown_NeverCancels_AndKeepsConnectedViewsAlive()
    {
        RunSta(() =>
        {
            using var exited = new ManualResetEventSlim();
            using var form = NewForm();
            form.ShutdownTimeout = TimeSpan.FromMilliseconds(300);
            form.ForcedExit = exited.Set;
            form.ForcedKill = () => { };
            form.Show();
            var view = OpenSettledView(form);
            view.SetStateForTests(SessionState.Connected, "Connessa");
            var token = form.ShutdownToken;
            bool disposed = false;
            view.Disposed += (_, _) => disposed = true;

            var e = new FormClosingEventArgs(CloseReason.WindowsShutDown, cancel: false);
            form.RaiseClosingForTests(e);

            // Niente annullamento (Windows direbbe «RdpManager impedisce l'arresto») e niente eliminazione del controllo connesso.
            Assert.False(e.Cancel);
            Assert.False(disposed);
            Assert.True(form.Enabled);
            Assert.True(form.ShutdownInProgress);
            Assert.True(token.IsCancellationRequested);
            // Con Windows l'arresto lo governa il sistema: nessuna uscita forzata a metà.
            Assert.False(exited.Wait(TimeSpan.FromSeconds(1)));

            // Un'altra app blocca l'arresto: WM_ENDSESSION con wParam = 0 e la finestra torna com'era.
            form.EndSessionForTests(ending: false);
            Assert.False(form.ShutdownInProgress);
            Assert.False(disposed);
            Assert.False(form.ShutdownToken.IsCancellationRequested);
            CloseCleanly(form);
        });
    }

    [Fact]
    public void WindowsShutdown_WhileQuestionOrWaitIsPending_DoesNotCancel()
    {
        RunSta(() =>
        {
            using var form = NewForm();
            form.ForcedExit = () => { };
            form.ForcedKill = () => { };
            form.Show();
            var view = OpenSettledView(form);
            view.SetStateForTests(SessionState.Connecting, "Connessione"); // resta «in corso»: l'attesa parte

            var first = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
            form.RaiseClosingForTests(first);
            Assert.True(first.Cancel);
            Assert.True(form.ShutdownInProgress);

            var windows = new FormClosingEventArgs(CloseReason.WindowsShutDown, cancel: false);
            form.RaiseClosingForTests(windows);
            Assert.False(windows.Cancel);
            form.EndSessionForTests(ending: false);
            Assert.False(form.ShutdownInProgress);
            Assert.True(form.Enabled);
            CloseCleanly(form);
        });
    }

    [Fact]
    public void WaitingForSessions_BlocksNewSessions_AndCancelRestoresTheWindow()
    {
        RunSta(() =>
        {
            using var form = NewForm();
            form.ForcedExit = () => { };
            form.ForcedKill = () => { };
            form.Show();
            var view = OpenSettledView(form);
            view.SetStateForTests(SessionState.Connecting, "Connessione");
            var other = new RdpConnection { Name = "altra", Host = "altro.invalid", Username = "u", Password = "p" };
            form.Document.Connections.Add(other);

            var e = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
            form.RaiseClosingForTests(e);
            Assert.True(e.Cancel); // attesa delle sessioni
            Assert.False(form.Enabled);
            form.OpenSession(other);
            Assert.Single(form.AllViews);

            form.RaiseClosingForTests(new FormClosingEventArgs(CloseReason.UserClosing, cancel: true));
            Assert.True(form.Enabled);
            form.OpenSession(other);
            Assert.Equal(2, form.AllViews.Count());
            CloseCleanly(form);
        });
    }

    [Fact]
    public void NeverStartedSession_GoesToDisconnected()
    {
        RunSta(() =>
        {
            using var view = new RdpSessionView(new RdpConnection { Name = "x", Host = "host-inesistente.invalid" });
            view.Connect(); // senza finestra visibile il controllo non parte: resta in attesa
            Assert.Equal(SessionState.Connecting, view.State);
            view.Disconnect();
            Assert.Equal(SessionState.Disconnected, view.State);
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
            var view = OpenSettledView(form);
            view.SetStateForTests(SessionState.Connecting, "Connessione");
            form.ShowNoticeForTests("messaggio");
            Assert.True(form.NoticeTimerRunning);
            var before = form.ShutdownToken;

            form.RaiseClosingForTests(new FormClosingEventArgs(CloseReason.UserClosing, cancel: false)); // l'attesa parte
            Assert.True(form.ShutdownInProgress);
            Assert.True(before.IsCancellationRequested);
            Assert.False(form.NoticeTimerRunning);

            // Annullata da altri: timer e segnale tornano come prima.
            form.RaiseClosingForTests(new FormClosingEventArgs(CloseReason.UserClosing, cancel: true));
            Assert.False(form.ShutdownInProgress);
            Assert.True(form.NoticeTimerRunning);
            Assert.False(form.ShutdownToken.IsCancellationRequested);
            Assert.NotEqual(before, form.ShutdownToken);
            CloseCleanly(form);
        });
    }

    [Fact]
    public void Watchdog_Kills_EvenIfExitNeverReturns()
    {
        using var hang = new ManualResetEventSlim();
        using var killed = new ManualResetEventSlim();
        var dog = ShutdownWatchdog.Start(TimeSpan.FromMilliseconds(100), () => hang.Wait(TimeSpan.FromSeconds(30)), killed.Set,
            killDelay: TimeSpan.FromMilliseconds(100));
        Assert.True(killed.Wait(TimeSpan.FromSeconds(5)), "l'uscita è rimasta appesa e la terminazione non è partita");
        Assert.True(dog.Fired);
        hang.Set();
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
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void VpnProfile_Save_FailureLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RdpManagerVpnSave-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "vpn.json");
        try
        {
            Directory.CreateDirectory(path); // il file di destinazione è una cartella: il Move fallisce
            Assert.ThrowsAny<Exception>(() => new VpnProfile { Address = "10.0.0.2/32" }.Save(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
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
