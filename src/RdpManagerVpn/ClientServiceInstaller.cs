using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RdpManagerVpn;

/// <summary>Esegue un comando e ne dà il codice di uscita (nella versione vera: sc.exe).</summary>
public interface ICommandRunner
{
    int Run(string file, params string[] args);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} non si avvia.");
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>
/// Registra e rimuove il servizio Windows «RdpManagerVpnClient»: avvio automatico, account SYSTEM (wireguard-nt vuole
/// l'amministratore), riavvio da solo se si ferma per un errore, permesso agli utenti di avviarlo e fermarlo dall'icona.
/// Serve l'amministratore: la chiama solo l'installazione (o <c>RdpManagerVpn.exe --installa-servizio</c> con UAC).
/// </summary>
public sealed class ClientServiceInstaller(ICommandRunner sc)
{
    // SDDL: SYSTEM e amministratori tutto; gli utenti interattivi leggono lo stato, avviano e fermano il servizio.
    internal const string Sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRPWPLORC;;;IU)";

    public static string ServiceCommandLine(string exePath) => $"\"{exePath}\" --servizio";

    public void Install(string exePath)
    {
        var name = ClientPaths.ServiceName;
        string verb = sc.Run("sc.exe", "query", name) == 0 ? "config" : "create"; // già presente (reinstallazione): si aggiorna
        Require(sc.Run("sc.exe", verb, name, "binPath=", ServiceCommandLine(exePath), "start=", "auto",
            "DisplayName=", "RdpManager VPN", "depend=", "Nsi/TcpIp"), verb);
        Require(sc.Run("sc.exe", "description", name, "Tunnel VPN WireGuard di RdpManager VPN"), "description");
        Require(sc.Run("sc.exe", "failure", name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000"), "failure");
        Require(sc.Run("sc.exe", "sdset", name, Sddl), "sdset");
    }

    /// <summary>Ferma e cancella il servizio; se non c'è non fa nulla.</summary>
    public void Uninstall()
    {
        var name = ClientPaths.ServiceName;
        if (sc.Run("sc.exe", "query", name) != 0)
            return;
        sc.Run("sc.exe", "stop", name); // se è già fermo dà un errore innocuo
        Require(sc.Run("sc.exe", "delete", name), "delete");
    }

    private static void Require(int code, string verb)
    {
        if (code != 0)
            throw new InvalidOperationException($"sc {verb} non riuscito (codice {code}).");
    }

    /// <summary>Crea %ProgramData%\RdpManagerVPN e lascia agli utenti la modifica (per impostazioni.json: l'icona non ha l'amministratore).</summary>
    public static void PrepareDataDirectory(string dataDirectory)
    {
        var dir = Directory.CreateDirectory(dataDirectory);
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        dir.SetAccessControl(security);
    }
}
