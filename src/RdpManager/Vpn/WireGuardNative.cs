using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RdpManager.Vpn;

/// <summary>
/// Tutta la parte che tocca Windows e le DLL di WireGuard, isolata qui perché i test usano le finte (<see cref="IServiceManager"/>,
/// <see cref="IVpnElevator"/>). Le DLL (tunnel.dll, wireguard.dll) vanno nella cartella del programma, vedi Vpn\dll\LEGGIMI.txt.
/// </summary>
internal static class WireGuardNative
{
    /// <summary>
    /// Cuore del servizio (<c>RdpManager.exe --wg-service file.conf</c>): chiama <c>WireGuardTunnelService</c> di tunnel.dll,
    /// che si registra presso il gestore dei servizi e resta qui finché il servizio non viene fermato. Falso = non è riuscito.
    /// </summary>
    public static bool RunTunnelService(string confPath)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "tunnel.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "wireguard.dll")))
            return false;
        // wireguard.dll è cercata da tunnel.dll nella sua stessa cartella.
        if (NativeLibrary.Load(dll) is var handle && handle == IntPtr.Zero)
            return false;
        var export = NativeLibrary.GetExport(handle, "WireGuardTunnelService");
        var fn = Marshal.GetDelegateForFunctionPointer<TunnelServiceFn>(export);
        return fn(confPath);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool TunnelServiceFn([MarshalAs(UnmanagedType.LPWStr)] string confFile);

    public static bool DllsPresent
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "tunnel.dll"))
           && File.Exists(Path.Combine(AppContext.BaseDirectory, "wireguard.dll"));
}

/// <summary>Stato dei servizi tramite il gestore dei servizi di Windows (basta l'utente normale).</summary>
public sealed class ScQuery : IVpnServiceQuery
{
    public TunnelState Query(string serviceName)
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero)
            return TunnelState.Errore;
        try
        {
            var svc = OpenService(scm, serviceName, 4);
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
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>Creazione e controllo dei servizi con sc.exe: solo da processo elevato (l'assistente <see cref="VpnHelper"/>).</summary>
public sealed class ScServiceManager : IServiceManager
{
    private readonly ScQuery _query = new();

    public TunnelState Query(string serviceName) => _query.Query(serviceName);

    public bool Exists(string serviceName) => Sc("query", serviceName) == 0;

    public void Create(string serviceName, string displayName, string binaryPathWithArgs)
    {
        Require(Sc("create", serviceName, "binPath=", binaryPathWithArgs, "start=", "demand", "DisplayName=", displayName,
            "depend=", "Nsi/TcpIp"), "create");
        Sc("sidtype", serviceName, "unrestricted"); // come il servizio ufficiale di WireGuard
    }

    public void Start(string serviceName) => Require(Sc("start", serviceName), "start");

    public void Stop(string serviceName) => Sc("stop", serviceName);

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
