using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.Rdp;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Rete di sicurezza contro blocchi e chiusure improvvise: eccezioni non gestite registrate in crash.log con lo stack,
/// avvisi non a raffica, controllo RDP eliminato solo a disconnessione avvenuta, eventi in ritardo ignorati.
/// </summary>
[Collection("UI")]
public sealed class CrashSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerCrash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("prova di errore"); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void CrashLog_WritesFullStackTraceAndSource()
    {
        var log = new CrashLog(_dir);
        log.Write("Eccezione sul thread dell'interfaccia", Thrown());
        var text = File.ReadAllText(log.CurrentPath);
        Assert.Contains("Eccezione sul thread dell'interfaccia", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("prova di errore", text);
        Assert.Contains(nameof(Thrown), text); // lo stack c'è, non solo il tipo
    }

    [Fact]
    public void CrashLog_NeverWritesPasswordsAndMarksTermination()
    {
        var log = new CrashLog(_dir);
        log.Write("thread", new InvalidOperationException("login password=segreto123 fallito"), terminating: true);
        var text = File.ReadAllText(log.CurrentPath);
        Assert.DoesNotContain("segreto123", text);
        Assert.Contains("il programma si chiude", text);
    }

    [Fact]
    public void CrashLog_DoesNotThrowWhenFolderIsUnusable()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "RdpManagerCrashFile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x"); // un file al posto della cartella
        try
        {
            var log = new CrashLog(Path.Combine(blocker, "logs"));
            log.Write("thread", Thrown()); // non deve lanciare
            log.Write("thread", null);
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void CrashLog_RotatesWhenTooBig()
    {
        var log = new CrashLog(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(log.CurrentPath, new string('x', (int)CrashLog.MaxBytes + 10));
        log.Write("thread", Thrown());
        Assert.True(File.Exists(Path.Combine(_dir, "crash.1.log")));
        Assert.True(new FileInfo(log.CurrentPath).Length < CrashLog.MaxBytes);
    }

    [Fact]
    public void UiHandler_LogsEveryErrorButWarnsTheUserOnlyOnceInAWhile()
    {
        var log = new CrashLog(_dir) { NoticeInterval = TimeSpan.FromMinutes(5) };
        int notices = 0;
        log.Attach(_ => notices++);
        log.UiHandler(Thrown());
        log.UiHandler(Thrown());
        log.UiHandler(Thrown());
        Assert.Equal(1, notices);
        Assert.Equal(3, File.ReadAllText(log.CurrentPath).Split("Eccezione sul thread dell'interfaccia").Length - 1);
    }

    [Fact]
    public void UiHandler_SurvivesAFailingNotice()
    {
        var log = new CrashLog(_dir);
        log.Attach(_ => throw new InvalidOperationException("la finestra di avviso non si apre"));
        log.UiHandler(Thrown()); // l'avviso fallito non fa ripartire l'errore
        Assert.Contains("prova di errore", File.ReadAllText(log.CurrentPath));
    }

    [Fact]
    public void ComEventHandlerFailure_IsLoggedNotPropagated()
    {
        Directory.CreateDirectory(_dir);
        var saved = CrashLog.Default;
        CrashLog.Default = new CrashLog(_dir);
        try
        {
            Run(() =>
            {
                using var host = RdpClientHost.Create();
                // Come un evento nativo il cui gestore lancia (controllo già eliminato, finestra chiusa…).
                RdpClientHost.RaiseSafely(host, "OnDisconnected", () => throw new ObjectDisposedException("Label"));
            });
        }
        finally { CrashLog.Default = saved; }
        Assert.Contains("ObjectDisposedException", File.ReadAllText(Path.Combine(_dir, CrashLog.FileName)));
    }

    // ---------- chiusura della sessione ----------

    private static RdpSessionView NewView() => new(new RdpConnection { Name = "Prova", Host = "127.0.0.1", Port = 1 });

    [Fact]
    public void CloseGracefully_DoesNotDisposeAConnectedViewUntilItIsDisconnected()
    {
        Run(() =>
        {
            var view = NewView();
            view.SetStateForTests(SessionState.Connected, "Connessa");
            bool done = false;
            view.CloseGracefully(TimeSpan.FromSeconds(30), () => done = true);
            Pump(300);
            Assert.False(view.IsDisposed); // ancora connessa: il controllo non si tocca
            view.SetStateForTests(SessionState.Disconnected, "Disconnessa");
            Assert.True(PumpUntil(() => view.IsDisposed && done, 3000));
        });
    }

    [Fact]
    public void CloseGracefully_GivesUpWaitingAfterTheTimeout()
    {
        Run(() =>
        {
            var view = NewView();
            view.SetStateForTests(SessionState.Connecting, "…");
            view.CloseGracefully(TimeSpan.FromMilliseconds(200));
            Assert.True(PumpUntil(() => view.IsDisposed, 3000), "la chiusura non può restare appesa per sempre");
        });
    }

    [Fact]
    public void CloseGracefully_DisposesAtOnceWhenNotConnected_AndTwiceIsHarmless()
    {
        Run(() =>
        {
            var view = NewView();
            view.CloseGracefully(TimeSpan.FromSeconds(1));
            Assert.True(view.IsDisposed);
            view.CloseGracefully(TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public void StateChangeArrivingAfterDispose_IsIgnored()
    {
        Run(() =>
        {
            var view = NewView();
            view.Dispose();
            view.SetStateForTests(SessionState.Disconnected, "Disconnessa dopo la chiusura"); // niente eccezioni
            Assert.True(view.IsDisposed);
        });
    }

    // ---------- finestra principale ----------

    [Fact]
    public void CloseSession_OfADetachedView_AndAgainOnTheSameView_DoesNotThrow()
    {
        Run(() =>
        {
            Directory.CreateDirectory(_dir);
            var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
            store.Save(new ConnectionsDocument { Connections = [new RdpConnection { Name = "A", Host = "127.0.0.1", Port = 1 }] });
            using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1000, 700) };
            form.Show();
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            view.SetStateForTests(SessionState.Disconnected, "Disconnessa");
            form.ToggleDetach(view);
            Assert.Single(form.DetachedForms);
            form.CloseSession(view);
            Assert.True(view.IsDisposed);
            Assert.Empty(form.DetachedForms);
            // Una seconda richiesta di chiusura sulla stessa vista (clic doppio, evento in ritardo) non lancia.
            form.CloseSession(view);
            form.Close();
        });
    }

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
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "La prova non è terminata entro 60 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Pump(int ms) => PumpUntil(() => false, ms);

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
}
