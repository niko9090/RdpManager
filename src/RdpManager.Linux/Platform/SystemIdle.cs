using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RdpManager.Linux.Platform;

/// <summary>
/// Da quanto tempo l'utente non tocca tastiera e mouse, misurato dal sistema (vale per tutto il desktop, anche dentro le
/// sessioni FreeRDP incorporate, di cui la finestra non vede gli eventi). Prima XScreenSaverQueryInfo (X11/XWayland), poi
/// <c>org.freedesktop.ScreenSaver.GetSessionIdleTime</c> via DBus. Se nessuno dei due risponde restituisce null e il
/// chiamante ripiega sugli eventi della finestra.
/// </summary>
public static class SystemIdle
{
    private static IntPtr _display;
    private static bool _xFailed;
    private static bool _dbusFailed;

    public static TimeSpan? Query()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        return QueryX11() ?? QueryDbus();
    }

    private static TimeSpan? QueryX11()
    {
        if (_xFailed)
            return null;
        try
        {
            if (_display == IntPtr.Zero)
                _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                _xFailed = true;
                return null;
            }
            var info = XScreenSaverAllocInfo();
            if (info == IntPtr.Zero)
                return null;
            try
            {
                if (XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), info) == 0)
                    return null;
                // XScreenSaverInfo: window, state, kind, til_or_since, idle (unsigned long = IntPtr.Size).
                int offset = IntPtr.Size == 8 ? 24 : 16;
                var ms = Marshal.ReadIntPtr(info, offset).ToInt64();
                return ms < 0 ? null : TimeSpan.FromMilliseconds(ms);
            }
            finally
            {
                XFree(info);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _xFailed = true;
            return null;
        }
    }

    private static TimeSpan? QueryDbus()
    {
        if (_dbusFailed)
            return null;
        try
        {
            var psi = new ProcessStartInfo("gdbus")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.ScreenSaver", "--object-path", "/org/freedesktop/ScreenSaver",
                         "--method", "org.freedesktop.ScreenSaver.GetSessionIdleTime" })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0)
            {
                _dbusFailed = true;
                return null;
            }
            return ParseDbusSeconds(output.Result);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _dbusFailed = true;
            return null;
        }
    }

    /// <summary>Legge «(uint32 123,)» di gdbus: il valore è in secondi.</summary>
    internal static TimeSpan? ParseDbusSeconds(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"(?:uint32\s+)?(\d+)\s*,?\s*\)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libXss.so.1")] private static extern IntPtr XScreenSaverAllocInfo();
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
}
