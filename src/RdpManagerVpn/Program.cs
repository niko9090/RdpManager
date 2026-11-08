using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RdpManagerVpn;

internal static class Program
{
    private const string InstallArg = "--installa-servizio";
    private const string RemoveArg = "--rimuovi";
    private const string RemoveAllArg = "--tutto";

    [STAThread]
    private static int Main(string[] args)
    {
        RdpManager.Core.PlatformInit.Register();
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var exeDir = Path.GetDirectoryName(exePath)!;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // «--servizio»: questo processo è il servizio Windows del tunnel; nessuna interfaccia.
        if (args.Length > 0 && args[0].Equals("--servizio", StringComparison.OrdinalIgnoreCase))
            return ClientServiceHost.Run(programData, exeDir);

        // «--installa-servizio» e «--rimuovi [--tutto]»: servono l'amministratore, si rilanciano con UAC se mancano i diritti.
        if (args.Length > 0 && (args[0].Equals(InstallArg, StringComparison.OrdinalIgnoreCase) || args[0].Equals(RemoveArg, StringComparison.OrdinalIgnoreCase)))
            return RunAdminCommand(args, exePath, programData);

        using var single = new Mutex(true, @"Local\RdpManagerVPN.Tray", out bool first);
        if (!first)
            return 0; // l'icona c'è già

        // niente ApplicationConfiguration.Initialize(): il tipo generato esiste anche in RdpManager (visibile per il tunnel)
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(programData, exePath));
        return 0;
    }

    private static int RunAdminCommand(string[] args, string exePath, string programData)
    {
        if (!IsAdministrator())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, Arguments = string.Join(' ', args) });
                p!.WaitForExit();
                return p.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return 1223; // UAC rifiutato
            }
        }

        try
        {
            var installer = new ClientServiceInstaller(new ProcessCommandRunner());
            if (args[0].Equals(InstallArg, StringComparison.OrdinalIgnoreCase))
            {
                ClientServiceInstaller.PrepareDataDirectory(ClientPaths.DataDirectory(programData));
                installer.Install(exePath);
                // il servizio parte subito: con «Avvia con Windows» = no non accende nulla, si accende con «Connetti»
                try
                {
                    new RdpManager.Vpn.ScControl().Start(ClientPaths.ServiceName); // senza «--manuale»: decidono le impostazioni
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
            {
                installer.Uninstall();
                try
                {
                    new AutoStartManager(new RegistryRunStore(), exePath).Apply(false); // voce dell'utente che esegue il comando
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                }
                if (args.Skip(1).Any(a => a.Equals(RemoveAllArg, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        Directory.Delete(ClientPaths.DataDirectory(programData), recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 2;
        }
    }

    private static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
