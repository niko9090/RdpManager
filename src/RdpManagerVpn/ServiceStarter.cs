using System.Runtime.InteropServices;

namespace RdpManagerVpn;

/// <summary>Avvia il servizio passandogli «--manuale» (ScControl di RdpManager lo avvia senza argomenti).</summary>
internal static class ServiceStarter
{
    private const int ScManagerConnect = 1, ServiceStart = 0x10;

    public static void StartManual(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException($"Gestore dei servizi non accessibile (errore {Marshal.GetLastWin32Error()}).");
        try
        {
            var svc = OpenService(scm, serviceName, ServiceStart);
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException($"Servizio non accessibile (errore {Marshal.GetLastWin32Error()}).");
            var arg = Marshal.StringToHGlobalUni(ServicePolicy.ManualArgument);
            var argv = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(argv, arg);
                if (!StartService(svc, 1, argv) && Marshal.GetLastWin32Error() != 1056) // 1056: già avviato
                    throw new InvalidOperationException($"Avvio del servizio non riuscito (errore {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Marshal.FreeHGlobal(argv);
                Marshal.FreeHGlobal(arg);
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
