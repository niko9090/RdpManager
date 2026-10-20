using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Fact eseguito solo se RDPMANAGER_LIVE_HOST è impostata (altrimenti saltato).</summary>
public sealed class LiveRdpFactAttribute : FactAttribute
{
    public LiveRdpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveRdpServerTests.HostVariable)))
            Skip = $"Prova con server RDP reale: imposta {LiveRdpServerTests.HostVariable}=<host> per eseguirla.";
    }
}

/// <summary>
/// Prova con un server RDP reale, SENZA credenziali vere: utente inventato, nessuna password,
/// richiesta di credenziali vietata (PromptForCredentials e AllowPromptingForCredentials, entrambi
/// riletti dal controllo), archivio temporaneo, un solo tentativo per esecuzione.
/// Agli avvisi modali la sorveglianza risponde sempre «No»: non preme mai «Sì».
///
/// Variabili: RDPMANAGER_LIVE_HOST (obbligatoria), RDPMANAGER_LIVE_PORT (3389),
/// RDPMANAGER_LIVE_OUT (cartella per log e screenshot).
///
/// Storia: con un PC Windows reale (NLA), quando era vietato solo PromptForCredentials, Windows
/// apriva comunque «Sicurezza di Windows – Immettere le credenziali» (CredentialUIBroker, altro
/// processo) e RdpManager restava bloccato; la vecchia sorveglianza guardava solo le finestre del
/// proprio processo e non la trovava. Riprodotto senza rete in CertificateWarningTests.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class LiveRdpServerTests
{
    public const string HostVariable = "RDPMANAGER_LIVE_HOST";
    private const string FakeUser = "rdpmanager-prova";

    [LiveRdpFact]
    public void FakeUser_IsRefusedWithoutAnyPromptAndReasonShownInTab()
    {
        var host = Environment.GetEnvironmentVariable(HostVariable)!.Trim();
        var port = int.TryParse(Environment.GetEnvironmentVariable("RDPMANAGER_LIVE_PORT"), out var p) ? p : 3389;
        var outDir = Environment.GetEnvironmentVariable("RDPMANAGER_LIVE_OUT")
                     ?? Path.Combine(Path.GetTempPath(), "RdpManagerLive", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, "live.log");
        var gate = new object();
        void Log(string line) { lock (gate) File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}", Encoding.UTF8); }
        var storeDir = Path.Combine(Path.GetTempPath(), "RdpManagerLiveStore-" + Guid.NewGuid().ToString("N"));

        Exception? failure = null;
        bool loggedIn = false, promptDisabled = false;
        int warnings = 0;
        int? reason = null, extended = null;
        string finalText = "";
        var sw = new Stopwatch();
        using var watcher = new ModalDialogWatcher(Log, outDir, pressNo: true);

        var thread = new Thread(() =>
        {
            try
            {
                var store = new ConnectionStore(Path.Combine(storeDir, "connections.json"));
                store.Save(new ConnectionsDocument
                {
                    Connections = [new RdpConnection { Name = $"Prova {host}", Host = host, Port = port, Username = FakeUser }],
                });
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(20, 20), Size = new Size(1200, 760) };
                form.Show();
                PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

                var conn = form.Document.Connections.Single();
                Assert.Null(conn.Password);
                Log($"Connessione a {host}:{port} utente '{FakeUser}' senza password, richiesta credenziali vietata, avvisi → «No»");
                // Va impostato PRIMA della connessione.
                form.ConfigureNewSession = v =>
                {
                    v.AllowCredentialPrompt = false;
                    v.StateChanged += (_, _) =>
                    {
                        Log($"+{sw.Elapsed.TotalSeconds:0.0}s stato={v.State} «{v.StateText}»");
                        if (v.State == SessionState.Connected)
                        {
                            loggedIn = true; // non deve succedere: si chiude subito
                            v.Disconnect();
                        }
                    };
                };
                sw.Start();
                form.OpenSession(conn);
                var view = form.CurrentView!;
                promptDisabled = view.PromptForCredentialsDisabledVerified;
                Log($"Richiesta credenziali vietata e verificata sul controllo: {promptDisabled}");

                bool done = PumpUntil(() => view.State == SessionState.Disconnected, TimeSpan.FromSeconds(60));
                sw.Stop();
                warnings = view.AuthenticationWarningCount;
                reason = view.LastDisconnectReason;
                extended = view.LastExtendedDisconnectReason;
                finalText = view.StateText;
                Log($"Fine dopo {sw.Elapsed.TotalSeconds:0.0}s: completato={done} codice={reason} esteso={extended} avvisi={warnings}");
                PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
                var shot = Path.Combine(outDir, "scheda-finale.png");
                SaveWindowImage(form.Handle, shot);
                Log("Screenshot: " + shot);
                Assert.True(done, $"Nessuna disconnessione entro 60 s (stato {view.State}: {view.StateText})");
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
                Log("ECCEZIONE: " + ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(TimeSpan.FromSeconds(120));
        try { Directory.Delete(storeDir, recursive: true); } catch (IOException) { }
        Log($"Finestre modali viste: {watcher.Seen.Count}");
        Assert.True(finished, "Interfaccia bloccata da una finestra modale (vedi live.log). Finestre viste: " +
                              string.Join("; ", watcher.Seen.Select(s => $"{s.ProcessName} «{s.Title}»")));
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);

        Assert.True(promptDisabled, "La richiesta di credenziali non risultava vietata sul controllo");
        Assert.False(loggedIn, "Sessione arrivata a «Connessa» con un utente inventato. Disconnessa subito.");
        Assert.DoesNotContain(watcher.Seen, s => !s.InOurProcess); // nessuna «Sicurezza di Windows»
        Assert.NotNull(reason);
        Assert.StartsWith("Disconnessa:", finalText);
        // Il server è stato raggiunto: non deve essere un errore di rete/DNS.
        Assert.DoesNotContain(reason!.Value, new[] { 260, 264, 516, 520, 772 });
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

    private static void SaveWindowImage(IntPtr hwnd, string path)
    {
        if (!ModalDialogWatcher.Native.GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
            return;
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            ModalDialogWatcher.Native.PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT: funziona anche se coperta
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
