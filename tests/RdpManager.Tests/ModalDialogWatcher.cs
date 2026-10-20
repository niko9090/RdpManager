using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpManager.Tests;

/// <summary>Una finestra modale vista dalla sorveglianza, con lo stato della sua proprietaria in quel momento.</summary>
internal sealed record SeenDialog(
    IntPtr Handle, string ClassName, string Title, int ProcessId, string ProcessName, bool InOurProcess,
    IntPtr Owner, bool OwnerMinimized, bool OwnerEnabled, Rectangle Bounds, bool OnScreen, bool ForegroundIsDialog,
    IReadOnlyList<string> Texts);

/// <summary>
/// Sorveglia da un thread separato le finestre modali legate al processo dei test. Gli avvisi del
/// controllo RDP bloccano il thread dell'interfaccia, quindi non si possono osservare da lì.
/// Trova sia le #32770 del processo (avviso certificato) sia le finestre di ALTRI processi la cui
/// proprietaria è una nostra finestra (es. «Sicurezza di Windows» di CredentialUIBroker): per
/// questo enumera tutte le finestre di primo livello con EnumWindows e controlla GW_OWNER, non il
/// titolo o la finestra in primo piano.
/// Sicurezza: non preme mai «Sì». Per le #32770 preme «No» (se <see cref="PressNo"/>); le finestre
/// di altri processi le registra soltanto.
/// </summary>
internal sealed class ModalDialogWatcher : IDisposable
{
    private readonly Action<string> _log;
    private readonly string? _screenshotDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly List<SeenDialog> _seen = [];

    public ModalDialogWatcher(Action<string> log, string? screenshotDir = null, bool pressNo = true)
    {
        _log = log;
        _screenshotDir = screenshotDir;
        PressNo = pressNo;
        _thread = new Thread(Run) { IsBackground = true, Name = "ModalDialogWatcher" };
        _thread.Start();
    }

    public bool PressNo { get; }

    public IReadOnlyList<SeenDialog> Seen { get { lock (_gate) return _seen.ToList(); } }

    private void Run()
    {
        var handled = new HashSet<IntPtr>();
        int pid = Environment.ProcessId;
        while (!_cts.IsCancellationRequested)
        {
            foreach (var hwnd in Native.TopLevelWindows())
            {
                if (handled.Contains(hwnd) || !Native.IsWindowVisible(hwnd))
                    continue;
                Native.GetWindowThreadProcessId(hwnd, out var windowPid);
                var owner = Native.GetWindow(hwnd, 4 /* GW_OWNER */);
                int ownerPid = 0;
                if (owner != IntPtr.Zero)
                    Native.GetWindowThreadProcessId(owner, out ownerPid);
                var cls = Native.ClassName(hwnd);
                bool ours = windowPid == pid && cls == "#32770";
                bool ownedByUs = windowPid != pid && owner != IntPtr.Zero && ownerPid == pid;
                if (!ours && !ownedByUs)
                    continue;

                handled.Add(hwnd);
                Thread.Sleep(500); // lascia comparire e disegnare la finestra
                Native.GetWindowRect(hwnd, out var r);
                var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                var texts = Native.Children(hwnd).Select(c => Native.WindowText(c)).Where(t => t.Length > 0).ToList();
                string processName;
                try { processName = Process.GetProcessById(windowPid).ProcessName; } catch (ArgumentException) { processName = "?"; }
                var seen = new SeenDialog(
                    hwnd, cls, Native.WindowText(hwnd), windowPid, processName, windowPid == pid,
                    owner, owner != IntPtr.Zero && Native.IsIconic(owner), owner == IntPtr.Zero || Native.IsWindowEnabled(owner),
                    bounds, Screen.AllScreens.Any(s => s.WorkingArea.Contains(bounds)),
                    Native.GetForegroundWindow() == hwnd, texts);
                lock (_gate)
                    _seen.Add(seen);
                _log($"FINESTRA MODALE: [{cls}] «{seen.Title}» processo={processName}({windowPid}) proprietaria={owner:X} " +
                     $"minimizzata={seen.OwnerMinimized} abilitata={seen.OwnerEnabled} rect={bounds} sullo schermo={seen.OnScreen} primo piano={seen.ForegroundIsDialog}");
                foreach (var t in texts)
                    _log("    «" + t + "»");
                if (_screenshotDir is not null)
                {
                    SaveImage(hwnd, bounds, Path.Combine(_screenshotDir, $"modale-{_seen.Count}.png"));
                    if (owner != IntPtr.Zero && Native.GetWindowRect(owner, out var o))
                        SaveImage(owner, Rectangle.FromLTRB(o.Left, o.Top, o.Right, o.Bottom), Path.Combine(_screenshotDir, $"modale-{_seen.Count}-proprietaria.png"));
                }

                if (ours && PressNo)
                {
                    var no = Native.Children(hwnd).FirstOrDefault(c =>
                        Native.ClassName(c) == "Button" && Native.WindowText(c).Replace("&", "").Trim() == "No");
                    if (no != IntPtr.Zero)
                    {
                        _log("    → premo «No» (mai «Sì»)");
                        Native.PostMessage(no, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                    }
                    else
                    {
                        _log("    → nessun pulsante «No»: chiudo (WM_CLOSE)");
                        Native.PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else if (!ours)
                {
                    _log("    → finestra di un altro processo: solo registrata");
                }
            }
            Thread.Sleep(100);
        }
    }

    private void SaveImage(IntPtr hwnd, Rectangle bounds, string path)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            Native.PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, ImageFormat.Png);
        _log("    screenshot: " + path);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

        public static List<IntPtr> TopLevelWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static List<IntPtr> Children(IntPtr parent)
        {
            var list = new List<IntPtr>();
            EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static string ClassName(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        public static string WindowText(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
    }
}
