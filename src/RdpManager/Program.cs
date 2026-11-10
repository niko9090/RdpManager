using RdpManager.Core;
using RdpManager.UI;
using RdpManager.Vpn;

namespace RdpManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        PlatformInit.Register();
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RdpManager");
        // «--vpn-service <nome>»: questo processo è il servizio del tunnel WireGuard (wireguard-nt); nessuna interfaccia.
        if (VpnServiceArgs.TryParse(args, out var tunnelName))
        {
            Environment.Exit(VpnServiceHost.Run(tunnelName, programData));
            return;
        }
        // «--vpn-helper su|giu|rimuovi …»: assistente elevato (UAC) che registra, ferma o rimuove il servizio del tunnel.
        if (VpnHelper.IsHelperMode(args))
        {
            var helper = new VpnHelper(new ScServiceManager(), programData, Environment.ProcessPath ?? Application.ExecutablePath);
            Environment.Exit(helper.Run(args));
            return;
        }
        ApplicationConfiguration.Initialize();
        // «--aggiorna …»: questa è la copia di riserva avviata come programma di aggiornamento (vedi UpdateRunner).
        if (UpdateJob.IsUpdateMode(args))
        {
            UpdateProgressForm.RunUpdate(args);
            return;
        }
        AppLog.Default = new AppLog(AppLog.DefaultDirectory);
        AppLog.Default.Info($"Avvio di RdpManager {AppInfo.VersionText} ({(InstallLocation.IsPortable(Application.ExecutablePath) ? "portatile" : "installato")})");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // Rete di sicurezza: ogni eccezione non gestita (interfaccia, altri thread, attività) va in crash.log con lo stack
        // completo; l'interfaccia continua e l'utente è avvisato (al più ogni 30 secondi, mai finestre a raffica).
        var crash = CrashLog.Default = new CrashLog(AppLog.DefaultDirectory);
        crash.Attach(ex => MessageBox.Show(
            "Si è verificato un errore imprevisto, ma RdpManager continua a funzionare.\n\n" + ex.Message +
            "\n\nI dettagli sono in " + crash.CurrentPath, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        Application.ThreadException += (_, e) => crash.UiHandler(e.Exception);
        Application.ApplicationExit += (_, _) => AppLog.Default?.Info("Uscita da RdpManager");
        // «--dati <file>»: un elenco di connessioni diverso da quello predefinito (prove, PC condivisi).
        int i = Array.FindIndex(args, a => a.Equals("--dati", StringComparison.OrdinalIgnoreCase));
        var form = i >= 0 && i + 1 < args.Length
            ? new MainForm(new ConnectionStore(Path.GetFullPath(args[i + 1])), checkUpdatesOnStartup: true, backgroundChecks: true)
            : new MainForm();
        // «--aggiornamento-fallito <messaggio>»: riaperta dal programma di aggiornamento dopo un errore.
        int f = Array.FindIndex(args, a => a.Equals(UpdateJob.FailedArgument, StringComparison.OrdinalIgnoreCase));
        if (f >= 0 && f + 1 < args.Length)
            form.UpdateFailureMessage = args[f + 1];
        ShutdownWatchdog.Enabled = true; // solo qui il cane da guardia della chiusura può far uscire il processo
        Application.Run(form);
    }
}
