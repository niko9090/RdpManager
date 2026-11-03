using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RdpManager.Vpn;

/// <summary>Presenza di wireguard.dll (wireguard-nt ufficiale) accanto al programma.</summary>
internal static class WireGuardNative
{
    public static bool DllPresent => File.Exists(Path.Combine(AppContext.BaseDirectory, "wireguard.dll"));
}

/// <summary>Stato e controllo del servizio tramite il gestore dei servizi di Windows (basta l'utente normale, se il servizio gli dà i permessi).</summary>
public sealed class ScControl : IVpnServiceControl
{
    private const int ScManagerConnect = 1;
    private const int ServiceQueryStatus = 4, ServiceStart = 0x10, ServiceStop = 0x20;

    public TunnelState Query(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return TunnelState.Errore;
        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == 1060 ? TunnelState.Spento : TunnelState.Errore; // 1060: servizio inesistente
            try
            {
                return QueryServiceStatus(svc, out var st)
                    ? st.CurrentState switch
                    {
                        1 => TunnelState.Spento,
                        2 => TunnelState.InAvvio,
                        3 => TunnelState.InArresto,
                        4 => TunnelState.Acceso,
                        _ => TunnelState.Errore,
                    }
                    : TunnelState.Errore;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public bool Exists(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;
        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() != 1060; // permesso negato = esiste ma non è leggibile
            CloseServiceHandle(svc);
            return true;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public void Start(string serviceName) => WithService(serviceName, ServiceStart, svc =>
    {
        if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) // 1056: già avviato
            throw new InvalidOperationException($"Avvio del servizio non riuscito (errore {Marshal.GetLastWin32Error()}).");
    });

    public void Stop(string serviceName) => WithService(serviceName, ServiceStop, svc =>
    {
        if (!ControlService(svc, 1, out _) && Marshal.GetLastWin32Error() is not (1062 or 1061)) // già fermo / in transizione
            throw new InvalidOperationException($"Arresto del servizio non riuscito (errore {Marshal.GetLastWin32Error()}).");
    });

    private static void WithService(string serviceName, int access, Action<IntPtr> action)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException($"Gestore dei servizi non accessibile (errore {Marshal.GetLastWin32Error()}).");
        try
        {
            var svc = OpenService(scm, serviceName, access);
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException($"Servizio non accessibile (errore {Marshal.GetLastWin32Error()}).");
            try
            {
                action(svc);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, int control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>Creazione e rimozione dei servizi con sc.exe: solo da processo elevato (l'assistente <see cref="VpnHelper"/>).</summary>
public sealed class ScServiceManager : IServiceManager
{
    // SDDL: SYSTEM e amministratori tutto; gli utenti interattivi leggono lo stato, avviano e fermano il servizio.
    private const string Sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRPWPLORC;;;IU)";

    private readonly ScControl _control = new();

    public TunnelState Query(string serviceName) => _control.Query(serviceName);

    public bool Exists(string serviceName) => _control.Exists(serviceName);

    public void Create(string serviceName, string displayName, string binaryPathWithArgs)
        => Require(Sc("create", serviceName, "binPath=", binaryPathWithArgs, "start=", "demand", "DisplayName=", displayName,
            "depend=", "Nsi/TcpIp"), "create");

    public void GrantUserControl(string serviceName) => Require(Sc("sdset", serviceName, Sddl), "sdset");

    public void Start(string serviceName) => _control.Start(serviceName);

    public void Stop(string serviceName) => _control.Stop(serviceName);

    public void Delete(string serviceName) => Require(Sc("delete", serviceName), "delete");

    private static void Require(int code, string verb)
    {
        if (code != 0)
            throw new InvalidOperationException($"sc {verb} non riuscito (codice {code}).");
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("sc.exe non si avvia.");
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>Rilancia RdpManager.exe con UAC (verbo runas) per le operazioni che richiedono l'amministratore.</summary>
public sealed class ProcessElevator(string exePath) : IVpnElevator
{
    public int RunHelper(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', args.Select(a => '"' + a + '"')),
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new VpnException("Impossibile avviare l'assistente della VPN.");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return WireGuardNtTunnel.ExitCancelled;
        }
    }
}

/// <summary>Permessi dei file della VPN.</summary>
internal static class VpnFiles
{
    /// <summary>Solo SYSTEM e Administrators (la configurazione contiene la chiave privata in chiaro).</summary>
    public static void RestrictToAdministrators(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
