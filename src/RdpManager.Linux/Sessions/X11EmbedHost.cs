using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace RdpManager.Linux.Sessions;

/// <summary>
/// Superficie nativa X11 dentro la scheda: una finestra figlia vuota il cui handle (XID) si passa a FreeRDP con
/// /parent-window. Funziona solo se Avalonia gira su X11 (anche via XWayland); altrimenti <see cref="Created"/> non scatta.
/// </summary>
public sealed class X11EmbedHost : NativeControlHost
{
    private IntPtr _display;
    private IntPtr _window;

    /// <summary>Handle X11 creato (finestra figlia pronta, già mostrata).</summary>
    public event Action<long>? Created;

    /// <summary>Vero se Avalonia sta usando X11 (l'handle della finestra principale è un XID).</summary>
    public static bool IsX11(TopLevel? top) =>
        OperatingSystem.IsLinux() && top?.TryGetPlatformHandle() is { } h && h.HandleDescriptor == "XID";

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsLinux() || parent.HandleDescriptor != "XID")
            return base.CreateNativeControlCore(parent);
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
                return base.CreateNativeControlCore(parent);
            _window = XCreateSimpleWindow(_display, parent.Handle, 0, 0, 16, 16, 0, 0, 0);
            XMapWindow(_display, _window);
            XSync(_display, false);
            var handle = new PlatformHandle(_window, "XID");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Created?.Invoke(_window.ToInt64()));
            return handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_display != IntPtr.Zero && _window != IntPtr.Zero && control.Handle == _window)
        {
            XDestroyWindow(_display, _window);
            XSync(_display, false);
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
            _window = IntPtr.Zero;
            return;
        }
        base.DestroyNativeControlCore(control);
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, ulong border, ulong background);
    [DllImport("libX11.so.6")] private static extern int XMapWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
