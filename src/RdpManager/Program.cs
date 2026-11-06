using RdpManager.Core;
using RdpManager.UI;
using RdpManager.Vpn;

namespace RdpManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
        Application.ThreadException += (_, e) =>
        {
            AppLog.Default?.Error("Errore imprevisto", e.Exception);
            MessageBox.Show("Errore imprevisto:\n" + e.Exception.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
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
