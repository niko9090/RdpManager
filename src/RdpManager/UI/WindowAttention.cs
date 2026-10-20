using System.Runtime.InteropServices;

namespace RdpManager.UI;

/// <summary>
/// Porta in primo piano la finestra che contiene una sessione quando il controllo RDP apre una
/// finestra modale (avviso certificato o richiesta credenziali): finché non si risponde la finestra
/// proprietaria è disabilitata, e se resta minimizzata o dietro altre app l'utente vede RdpManager
/// «bloccato» senza capire perché.
/// </summary>
internal static class WindowAttention
{
    public enum Result { Foreground, Flashed }

    public static Result BringToFront(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        if (!form.Visible)
            form.Show();
        form.Activate();
        SetForegroundWindow(form.Handle);

        // Le finestre di proprietà (l'avviso) stanno sempre sopra la proprietaria; se era
        // minimizzata, Windows le ha messe nell'angolo dello schermo: le si ricentra.
        form.BeginInvoke(() => CenterOwnedDialogs(form));

        if (IsOurs(GetForegroundWindow()))
            return Result.Foreground;
        // Windows può negare il primo piano a un'app che non è quella in uso: lampeggia nella barra.
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = form.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = 0, dwTimeout = 0,
        };
        FlashWindowEx(ref info);
        return Result.Flashed;
    }

    /// <summary>Finestre di primo livello visibili, di qualunque processo, il cui proprietario è <paramref name="owner"/>.</summary>
    public static List<IntPtr> OwnedWindows(IntPtr owner)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == owner)
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static void CenterOwnedDialogs(Form form)
    {
        if (form.IsDisposed)
            return;
        var area = Screen.FromControl(form).WorkingArea;
        foreach (var h in OwnedWindows(form.Handle))
        {
            if (!GetWindowRect(h, out var r))
                continue;
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (area.Contains(rect))
                continue;
            int x = form.Left + (form.Width - rect.Width) / 2;
            int y = form.Top + (form.Height - rect.Height) / 2;
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - rect.Width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - rect.Height));
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private static bool IsOurs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
            return true;
        // La richiesta credenziali di Windows gira in un altro processo ma appartiene a una nostra finestra.
        var owner = GetWindow(hwnd, GW_OWNER);
        if (owner == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(owner, out var ownerPid);
        return ownerPid == Environment.ProcessId;
    }

    private const uint GW_OWNER = 4;
    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
}
