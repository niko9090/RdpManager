using System.Runtime.InteropServices;
using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManagerVpn;

/// <summary>
/// Il servizio «RdpManagerVpnClient» (lo stesso exe con <c>--servizio</c>, account SYSTEM): legge vpn.conf e le impostazioni,
/// tiene aperto il tunnel wireguard-nt con <see cref="ClientSupervisor"/> e scrive lo stato per l'icona. Stessa struttura di
/// <c>VpnServiceHost</c> di RdpManager, ma con profilo, impostazioni e nome propri così i due non si pestano i piedi.
/// </summary>
internal static class ClientServiceHost
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const int AcceptStop = 1, AcceptShutdown = 4;

    private static readonly ManualResetEvent StopEvent = new(false);
    private static IntPtr _statusHandle;
    private static string _programData = "";
    private static string _exeDirectory = "";
    private static int _checkpoint;

    private static readonly ServiceMainFn MainDelegate = ServiceMain;
    private static readonly HandlerFn HandlerDelegate = Handler;

    public static int Run(string programData, string exeDirectory)
    {
        _programData = programData;
        _exeDirectory = exeDirectory;
        var table = new ServiceTableEntry[2];
        table[0] = new ServiceTableEntry { Name = Marshal.StringToHGlobalUni(ClientPaths.ServiceName), Proc = Marshal.GetFunctionPointerForDelegate(MainDelegate) };
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
        _statusHandle = RegisterServiceCtrlHandlerEx(ClientPaths.ServiceName, HandlerDelegate, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
            return;
        bool manual = false;
        for (int i = 1; i < argc; i++) // argv[0] è il nome del servizio
            manual |= string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)), ServicePolicy.ManualArgument, StringComparison.OrdinalIgnoreCase);

        int exitCode = 0;
        var settingsPath = ClientPaths.Settings(_programData);
        try
        {
            SetStatus(StartPending, 0, 20000);
            if (!ServicePolicy.ShouldConnectAtStart(ClientSettings.Load(settingsPath), manual))
            {
                // «Avvia con Windows» = no: all'avvio il servizio non fa nulla e si ferma (resta installato, si accende con «Connetti»)
                Log("Avvio automatico disattivato: il tunnel non viene acceso.");
                return;
            }
            using var runner = new VpnTunnelRunner(new WireGuardNtDriver());
            var supervisor = new ClientSupervisor(runner,
                () => ClientProfile.Load(ClientPaths.ProfileCandidates(_exeDirectory, _programData)),
                () => ClientSettings.Load(settingsPath), () => DateTime.UtcNow, Log);
            SetStatus(Running, 0, 0);
            try
            {
                do
                {
                    if (!supervisor.Tick())
                    {
                        exitCode = 1;
                        break;
                    }
                    WriteStatus(supervisor.LastStatus);
                }
                while (!StopEvent.WaitOne(2000));
            }
            finally
            {
                SetStatus(StopPending, 0, 10000);
                supervisor.Shutdown();
                TryDelete(ClientPaths.Status(_programData));
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Log(ex.ToString());
        }
        finally
        {
            SetStatus(Stopped, exitCode, 0);
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) // STOP, SHUTDOWN
            StopEvent.Set();
        return 0;
    }

    private static void SetStatus(int state, int exitCode, int waitHint)
    {
        var st = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = exitCode == 0 ? 0 : 1066,
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
            var path = ClientPaths.Status(_programData);
            File.WriteAllText(path + ".tmp", status.Format());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.DataDirectory(_programData));
            File.AppendAllText(ClientPaths.Log(_programData), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n");
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
