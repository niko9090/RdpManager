using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Avvisi modali del controllo RDP riprodotti in locale con <see cref="FakeTlsRdpServer"/> (nessun
/// server reale, nessuna credenziale, nulla installato). La sorveglianza preme sempre «No».
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class CertificateWarningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerCert-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public CertificateWarningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Log(string line)
    {
        lock (_log)
            _log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    private string LogText() { lock (_log) return string.Join(Environment.NewLine, _log); }

    private void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)),
            "Interfaccia bloccata per oltre 60 s (finestra modale senza risposta?)\n" + LogText());
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

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

    private MainForm CreateForm(params RdpConnection[] connections)
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList() });
        var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), Size = new Size(1100, 700) };
        form.ConfigureNewSession = v =>
        {
            v.AllowCredentialPrompt = false;
            v.StateChanged += (_, _) => Log($"stato {v.Connection.Name}: {v.State} «{v.StateText}»");
        };
        form.Show();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(200));
        return form;
    }

    [Fact]
    public void Nla_WithoutPassword_DoesNotOpenWindowsCredentialDialogAndDoesNotBlock()
    {
        // Riproduce la causa trovata con un PC Windows reale: con NLA, PromptForCredentials=false non bastava e
        // compariva «Sicurezza di Windows» (CredentialUIBroker, altro processo) che bloccava l'app.
        using var server = new FakeTlsRdpServer(Log, FakeTlsRdpServer.ProtocolCredSsp);
        using var watcher = new ModalDialogWatcher(Log);
        RunSta(() =>
        {
            using var form = CreateForm(new RdpConnection { Name = "NLA", Host = "127.0.0.1", Port = server.Port, Username = "rdpmanager-prova" });
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            Assert.True(view.PromptForCredentialsDisabledVerified);

            var sw = Stopwatch.StartNew();
            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(20)), LogText());
            Log($"disconnessa dopo {sw.Elapsed.TotalSeconds:0.0} s, codice {view.LastDisconnectReason}");

            Assert.True(server.Connections >= 1, "il server finto non è stato contattato");
            Assert.Equal(7943, view.LastDisconnectReason);
            Assert.Contains("richiesta interattiva è disattivata", view.StateText);
            Assert.Equal(0, view.AuthenticationWarningCount);
            Assert.True(form.Enabled && WindowAttentionProbe.IsEnabled(form));
            form.Close();
        });
        Assert.Empty(watcher.Seen);
    }

    [Fact]
    public void CertificateWarning_RestoresMinimizedWindowSelectsTabAndShowsDialogOnScreen()
    {
        using var server = new FakeTlsRdpServer(Log, FakeTlsRdpServer.ProtocolTls);
        // RDPMANAGER_TEST_SHOTS=<cartella> salva gli screenshot dell'avviso e della finestra proprietaria.
        using var watcher = new ModalDialogWatcher(Log, Environment.GetEnvironmentVariable("RDPMANAGER_TEST_SHOTS"));
        var statesDuringWarning = new List<string>();
        RunSta(() =>
        {
            using var form = CreateForm(
                new RdpConnection { Name = "Altro", Host = "host-inesistente.invalid", Username = "x" },
                new RdpConnection { Name = "Certificato", Host = "127.0.0.1", Port = server.Port, Username = "rdpmanager-prova" });
            form.OpenSession(form.Document.Connections[0]);
            var other = form.CurrentView!;
            Assert.True(PumpUntil(() => other.State == SessionState.Disconnected, TimeSpan.FromSeconds(20)));

            form.OpenSession(form.Document.Connections[1]);
            var view = form.CurrentView!;
            view.AttentionRequired += (_, _) => statesDuringWarning.Add(view.StateText);
            // L'utente passa all'altra scheda e minimizza RdpManager prima che arrivi l'avviso.
            form.Tabs.SelectedIndex = 0;
            form.WindowState = FormWindowState.Minimized;

            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(30)), LogText());

            Assert.Equal(1, view.AuthenticationWarningCount);
            Assert.Equal(263, view.LastDisconnectReason); // «No» = certificato non accettato
            Assert.Contains("certificato del server non accettato", view.StateText);
            Assert.Equal(FormWindowState.Normal, form.WindowState);
            Assert.Same(view, form.CurrentView);
            Assert.NotNull(form.LastAttentionResult);
            Assert.True(form.Enabled && WindowAttentionProbe.IsEnabled(form), "la finestra principale deve tornare utilizzabile");

            var dialog = Assert.Single(watcher.Seen);
            Assert.Equal("#32770", dialog.ClassName);
            Assert.True(dialog.InOurProcess);
            Assert.Equal(form.Handle, dialog.Owner);
            Assert.False(dialog.OwnerMinimized, "quando l'avviso è visibile la finestra non deve essere minimizzata");
            Assert.False(dialog.OwnerEnabled, "l'avviso è modale: la proprietaria è disabilitata finché non si risponde");
            Assert.True(dialog.OnScreen, $"l'avviso deve stare dentro lo schermo, era in {dialog.Bounds}");
            Assert.Contains(dialog.Texts, t => t.Contains("certificato", StringComparison.OrdinalIgnoreCase));
            Log($"primo piano: {form.LastAttentionResult}, avviso in primo piano={dialog.ForegroundIsDialog}");
            if (form.LastAttentionResult == WindowAttention.Result.Foreground)
                Assert.True(dialog.ForegroundIsDialog || WindowAttentionProbe.ForegroundIsOurs());
            form.Close();
        });
        Assert.Equal([RdpSessionView.AuthenticationWarningText], statesDuringWarning);
    }

    [Fact]
    public void CertificateWarning_InDetachedWindow_RestoresThatWindow()
    {
        using var server = new FakeTlsRdpServer(Log, FakeTlsRdpServer.ProtocolTls);
        using var watcher = new ModalDialogWatcher(Log);
        RunSta(() =>
        {
            using var form = CreateForm(new RdpConnection { Name = "Staccata", Host = "127.0.0.1", Port = server.Port, Username = "rdpmanager-prova" });
            // Connessione avviata nella scheda, subito staccata e minimizzata: l'avviso arriva dopo.
            form.OpenSession(form.Document.Connections[0]);
            var view = form.CurrentView!;
            form.ToggleDetach(view);
            var detached = form.DetachedForms.Single();
            detached.WindowState = FormWindowState.Minimized;

            Assert.True(PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(30)), LogText());

            Assert.Equal(1, view.AuthenticationWarningCount);
            Assert.Equal(FormWindowState.Normal, detached.WindowState);
            var dialog = Assert.Single(watcher.Seen);
            Assert.True(dialog.OnScreen, $"l'avviso deve stare dentro lo schermo, era in {dialog.Bounds}");
            Assert.False(dialog.OwnerMinimized);
            Assert.Contains(dialog.Owner, new[] { form.Handle, detached.Handle });
            form.Close();
        });
    }

    [Fact]
    public void AuthenticationWarningText_MentionsCertificateAndCredentials()
    {
        Assert.Contains("certificato non verificabile", RdpSessionView.AuthenticationWarningText);
        Assert.Contains("credenziali", RdpSessionView.AuthenticationWarningText);
        Assert.Contains("certificato", DisconnectReasons.Describe(263));
        Assert.Contains("credenziali", DisconnectReasons.Describe(7943));
    }
}

internal static class WindowAttentionProbe
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

    public static bool IsEnabled(Form form) => IsWindowEnabled(form.Handle);

    public static bool ForegroundIsOurs()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Environment.ProcessId;
    }
}
