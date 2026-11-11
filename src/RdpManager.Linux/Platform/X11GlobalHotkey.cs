using System.Runtime.InteropServices;
using Avalonia.Input;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.Platform;

/// <summary>
/// Scorciatoia globale su X11 (XGrabKey sulla finestra radice): richiama l'app da qualunque programma.
/// Su Wayland il sistema non lo permette: <see cref="TryStart"/> restituisce falso e non fa niente (ripiego silenzioso).
/// Gira su una connessione X propria, in un thread suo; la pressione arriva dal thread dell'interfaccia tramite <c>post</c>.
/// </summary>
public sealed class X11GlobalHotkey : IDisposable
{
    private const int KeyPress = 2;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16;

    private readonly Action<Action> _post;
    private readonly Action _pressed;
    private IntPtr _display;
    private IntPtr _root;
    private int _keycode;
    private uint _mods;
    private Thread? _thread;
    private volatile bool _stop;
    private static int _grabError;

    public X11GlobalHotkey(Action<Action> post, Action pressed)
    {
        _post = post;
        _pressed = pressed;
    }

    public bool IsActive => _thread is not null;

    /// <summary>Nome del tasto per X («Right», «a», «F5»); null se non c'è una corrispondenza.</summary>
    public static string? KeysymName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Right => "Right",
        Key.Left => "Left",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Enter => "Return",
        Key.Space => "space",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Back => "BackSpace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "Prior",
        Key.PageDown => "Next",
        Key.Pause => "Pause",
        _ => null,
    };

    public static uint ModifierMask(KeyCombo combo) =>
        (combo.Ctrl ? ControlMask : 0) | (combo.Alt ? Mod1Mask : 0) | (combo.Shift ? ShiftMask : 0);

    /// <summary>Registra la scorciatoia. Falso (senza errori) se il desktop non è X11, se manca libX11 o se la combinazione è già presa.</summary>
    public bool TryStart(string combinationText, DisplayServerKind kind)
    {
        Stop();
        if (!OperatingSystem.IsLinux() || !DisplayServer.CanGlobalHotkey(kind) || !KeyCombo.TryParse(combinationText, out var combo)
            || !combo.HasCommandModifier || KeysymName(combo.Key) is not { } keysymName)
            return false;
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
                return false;
            _root = XDefaultRootWindow(_display);
            var keysym = XStringToKeysym(keysymName);
            _keycode = keysym == IntPtr.Zero ? 0 : XKeysymToKeycode(_display, keysym);
            if (_keycode == 0)
            {
                Close();
                return false;
            }
            _mods = ModifierMask(combo);
            _grabError = 0;
            var previous = XSetErrorHandler(OnError);
            // Con Bloc Maiusc e Bloc Num accesi la combinazione deve funzionare lo stesso: si prenotano tutte le varianti.
            foreach (var extra in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
                XGrabKey(_display, _keycode, _mods | extra, _root, 0, 1, 1);
            XSync(_display, 0);
            XSetErrorHandler(previous);
            if (_grabError != 0)
            {
                Release();
                Close();
                return false;
            }
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "RdpManager hotkey" };
            _thread.Start();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Close();
            return false;
        }
    }

    private static readonly XErrorHandler OnError = (_, _) =>
    {
        _grabError = 1;
        return 0;
    };

    private void Loop()
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            while (!_stop)
            {
                while (!_stop && XPending(_display) > 0)
                {
                    XNextEvent(_display, buffer);
                    if (Marshal.ReadInt32(buffer) == KeyPress)
                        _post(_pressed);
                }
                Thread.Sleep(80);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Release()
    {
        foreach (var extra in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
            XUngrabKey(_display, _keycode, _mods | extra, _root);
        XSync(_display, 0);
    }

    private void Close()
    {
        if (_display != IntPtr.Zero)
            XCloseDisplay(_display);
        _display = IntPtr.Zero;
    }

    public void Stop()
    {
        if (_thread is null)
            return;
        _stop = true;
        _thread.Join(500);
        _thread = null;
        try { Release(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        Close();
    }

    public void Dispose() => Stop();

    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libX11.so.6")] private static extern int XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, int discard);
    [DllImport("libX11.so.6")] private static extern XErrorHandler XSetErrorHandler(XErrorHandler handler);
}
