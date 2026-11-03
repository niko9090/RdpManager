using System.Runtime.InteropServices;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>Posizione dei file della VPN in %ProgramData%\RdpManager\vpn.</summary>
public static class VpnPaths
{
    public static string Directory(string programData) => Path.Combine(programData, "vpn");
    /// <summary>Configurazione con la chiave privata in chiaro: solo SYSTEM e amministratori.</summary>
    public static string Conf(string programData, string tunnelName) => Path.Combine(Directory(programData), tunnelName + ".conf");
    /// <summary>SHA-256 della configurazione installata (leggibile da tutti): dice all'app se può accendere senza UAC.</summary>
    public static string InstalledHash(string programData) => Path.Combine(Directory(programData), "installato.sha256");
    /// <summary>Stato scritto dal servizio ogni due secondi (handshake, byte): leggibile da tutti.</summary>
    public static string Status(string programData) => Path.Combine(Directory(programData), "stato.txt");
    public static string Log(string programData) => Path.Combine(Directory(programData), "servizio.log");
}

/// <summary>Argomenti del servizio: <c>RdpManager.exe --vpn-service &lt;nome-del-tunnel&gt;</c>.</summary>
public static class VpnServiceArgs
{
    public const string Argument = "--vpn-service";

    public static bool TryParse(string[] args, out string tunnelName)
    {
        tunnelName = "";
        if (args.Length != 2 || !args[0].Equals(Argument, StringComparison.OrdinalIgnoreCase))
            return false;
        var name = args[1].Trim();
        // il nome diventa nome di file e di adattatore: niente separatori di percorso o caratteri strani
        if (name.Length is 0 or > 32 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return false;
        tunnelName = name;
        return true;
    }
}

/// <summary>
/// Il servizio Windows «RdpManagerVpn» (lo stesso eseguibile con <c>--vpn-service</c>): si registra presso il gestore dei
/// servizi, legge la configurazione, tiene aperto l'adattatore wireguard-nt finché non viene fermato e scrive lo stato.
/// </summary>
internal static class VpnServiceHost
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const int AcceptStop = 1, AcceptShutdown = 4;

    private static readonly ManualResetEvent StopEvent = new(false);
    private static IntPtr _statusHandle;
    private static string _tunnelName = "";
    private static string _programData = "";
    private static INtDriver _driver = new WireGuardNtDriver();
    private static int _checkpoint;

    // Tenuti in campi statici: il gestore dei servizi li richiama finché il servizio vive.
    private static readonly ServiceMainFn MainDelegate = ServiceMain;
    private static readonly HandlerFn HandlerDelegate = Handler;

    /// <summary>Entra nel servizio; ritorna quando il servizio è fermato (0) o se non è un servizio avviato da Windows.</summary>
    public static int Run(string tunnelName, string programData)
    {
        _tunnelName = tunnelName;
        _programData = programData;
        var table = new ServiceTableEntry[2];
        table[0] = new ServiceTableEntry { Name = Marshal.StringToHGlobalUni(WireGuardNtTunnel.ServiceName), Proc = Marshal.GetFunctionPointerForDelegate(MainDelegate) };
        try
        {
            return StartServiceCtrlDispatcher(table) ? 0 : 1;
        }
        finally
        {
            Marshal.FreeHGlobal(table[0].Name);
        }
    }

    private static void ServiceMain(uint argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(WireGuardNtTunnel.ServiceName, HandlerDelegate, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
            return;
        int exitCode = 0;
        var runner = new VpnTunnelRunner(_driver);
        try
        {
            SetStatus(StartPending, 0, 20000);
            var conf = WireGuardConfig.Parse(File.ReadAllText(VpnPaths.Conf(_programData, _tunnelName)));
            runner.Start(conf, _tunnelName);
            SetStatus(Running, 0, 0);
            do
            {
                WriteStatus(runner.ReadStatus());
            }
            while (!StopEvent.WaitOne(2000));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            LogError(ex);
        }
        finally
        {
            SetStatus(StopPending, 0, 10000);
            runner.Stop();
            TryDelete(VpnPaths.Status(_programData));
            SetStatus(Stopped, exitCode, 0);
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) // SERVICE_CONTROL_STOP, SHUTDOWN
            StopEvent.Set();
        return 0; // NO_ERROR
    }

    private static void SetStatus(int state, int exitCode, int waitHint)
    {
        var st = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = exitCode == 0 ? 0 : 1066, // ERROR_SERVICE_SPECIFIC_ERROR
            ServiceSpecificExitCode = exitCode,
            CheckPoint = state is StartPending or StopPending ? ++_checkpoint : 0,
            WaitHint = waitHint,
        };
        SetServiceStatus(_statusHandle, ref st);
    }

    private static void WriteStatus(VpnStatus? status)
    {
        if (status is null)
            return;
        try
        {
            var path = VpnPaths.Status(_programData);
            File.WriteAllText(path + ".tmp", status.Format());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(VpnPaths.Log(_programData), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n");
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainFn(uint argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerFn(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerFn handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
