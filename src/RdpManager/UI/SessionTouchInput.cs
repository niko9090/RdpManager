using System.Runtime.InteropServices;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Il dito fa da mouse sopra la sessione remota. Il controllo RDP è una finestra ActiveX che non dà eventi a WinForms,
/// quindi si guardano i messaggi di puntatore (WM_POINTER*) diretti alla finestra del controllo o ai suoi figli, solo
/// di questa applicazione (filtro del ciclo dei messaggi, niente hook globali). I messaggi del dito vengono consumati
/// e i gesti (<see cref="TouchGestureRecognizer"/>) diventano clic, trascinamenti e rotella inviati con SendInput.
/// Attivo solo se la modalità è «mouse» oppure se è armato il «Clic destro» della barra.
/// </summary>
internal sealed class SessionTouchInput : IMessageFilter, IDisposable
{
    private const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERCAPTURECHANGED = 0x024C;
    private const int PT_TOUCH = 2;

    private readonly Control _target;
    private readonly TouchGestureRecognizer _recognizer;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private bool _installed;

    /// <summary>Per le prove: riceve le azioni invece di muovere il mouse.</summary>
    internal Action<TouchAction>? ActionSink { get; set; }

    public SessionTouchInput(Control target)
    {
        _target = target;
        _recognizer = new TouchGestureRecognizer(
            tolerance: TouchMetrics.Scale(TouchMetrics.LongPressTolerance, target.DeviceDpi),
            scrollStep: TouchMetrics.Scale(28, target.DeviceDpi),
            pinchStep: TouchMetrics.Scale(28, target.DeviceDpi),
            swipeDistance: TouchMetrics.Scale(90, target.DeviceDpi));
        _timer.Tick += (_, _) => Run(_recognizer.Poll(DateTime.UtcNow));
    }

    /// <summary>Il filtro è in funzione (modalità mouse, o clic destro armato).</summary>
    public bool Enabled
    {
        get => _installed;
        set
        {
            if (value == _installed)
                return;
            _installed = value;
            if (value)
            {
                Application.AddMessageFilter(this);
            }
            else
            {
                Application.RemoveMessageFilter(this);
                _timer.Stop();
                Run(_recognizer.Cancel());
            }
        }
    }

    /// <summary>Il prossimo tocco vale come clic destro.</summary>
    public bool RightClickArmed
    {
        get => _recognizer.NextTapIsRightClick;
        set => _recognizer.NextTapIsRightClick = value;
    }

    public event EventHandler? RightClickUsed;

    /// <summary>
    /// Gesto a più dita che non è mouse: tasti da mandare al PC remoto (Alt+Tab, Win+D, Win+Tab) o comando per la finestra
    /// di RdpManager (scheda precedente o successiva, mostra o nascondi la barra). Lo esegue chi ascolta (la vista della sessione).
    /// </summary>
    public event EventHandler<TouchActionKind>? CommandRequested;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is not (WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP or WM_POINTERCAPTURECHANGED))
            return false;
        if (!_target.IsHandleCreated || !_target.Visible || !BelongsToTarget(m.HWnd))
            return false;
        int id = (int)((long)m.WParam & 0xFFFF);
        if (!GetPointerType((uint)id, out int type) || type != PT_TOUCH)
            return false;
        if (m.Msg == WM_POINTERCAPTURECHANGED)
            return false;
        long l = (long)m.LParam;
        var screen = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
        var now = DateTime.UtcNow;
        bool armed = _recognizer.NextTapIsRightClick;
        switch (m.Msg)
        {
            case WM_POINTERDOWN:
                _timer.Start();
                Run(_recognizer.Down(id, screen, now));
                break;
            case WM_POINTERUPDATE:
                Run(_recognizer.Move(id, screen, now));
                break;
            case WM_POINTERUP:
                Run(_recognizer.Up(id, screen, now));
                if (!_recognizer.IsActive)
                    _timer.Stop();
                break;
        }
        if (armed && !_recognizer.NextTapIsRightClick)
            RightClickUsed?.Invoke(this, EventArgs.Empty);
        return true; // il dito lo gestiamo noi: niente clic automatico di Windows in più
    }

    private bool BelongsToTarget(IntPtr hwnd) => hwnd == _target.Handle || IsChild(_target.Handle, hwnd);

    private void Run(IReadOnlyList<TouchAction> actions)
    {
        foreach (var a in actions)
        {
            if (ActionSink is { } sink)
                sink(a);
            else if (TouchActionKinds.IsWindowCommand(a.Kind) || TouchActionKinds.IsRemoteKeys(a.Kind))
                CommandRequested?.Invoke(this, a.Kind);
            else
                Inject(a);
        }
    }

    // ---------- invio al sistema ----------

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint INPUT_MOUSE = 0;

    /// <summary>Esegue l'azione col mouse del sistema. Il secondo tocco di un doppio tocco è un altro clic nello stesso punto: la sessione remota lo riconosce come doppio clic.</summary>
    internal static void Inject(TouchAction a)
    {
        switch (a.Kind)
        {
            case TouchActionKind.Move:
                Send(MoveInput(a.Location));
                break;
            case TouchActionKind.Click:
                Send(MoveInput(a.Location), Button(MOUSEEVENTF_LEFTDOWN), Button(MOUSEEVENTF_LEFTUP));
                break;
            case TouchActionKind.RightClick:
                Send(MoveInput(a.Location), Button(MOUSEEVENTF_RIGHTDOWN), Button(MOUSEEVENTF_RIGHTUP));
                break;
            case TouchActionKind.LeftDown:
                Send(MoveInput(a.Location), Button(MOUSEEVENTF_LEFTDOWN));
                break;
            case TouchActionKind.LeftUp:
                Send(Button(MOUSEEVENTF_LEFTUP));
                break;
            case TouchActionKind.Scroll:
                Send(MoveInput(a.Location), Wheel(a.Amount));
                break;
            case TouchActionKind.Zoom:
                // Pizzico = Ctrl+rotella: il Ctrl locale passa alla sessione insieme alla rotella.
                Send(MoveInput(a.Location), Key(VK_CONTROL, false), Wheel(a.Amount), Key(VK_CONTROL, true));
                break;
        }
    }

    private static NativeMethods.INPUT MoveInput(Point screen)
    {
        var v = SystemInformation.VirtualScreen;
        int x = (int)Math.Round((screen.X - v.Left) * 65535.0 / Math.Max(1, v.Width - 1));
        int y = (int)Math.Round((screen.Y - v.Top) * 65535.0 / Math.Max(1, v.Height - 1));
        return Mouse(x, y, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    private const ushort VK_CONTROL = 0x11;

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0, dwExtraInfo = NativeMethods.OwnInputMarker } },
    };

    private static NativeMethods.INPUT Button(uint flag) => Mouse(0, 0, 0, flag);

    private static NativeMethods.INPUT Wheel(int delta) => Mouse(0, 0, unchecked((uint)delta), MOUSEEVENTF_WHEEL);

    private static NativeMethods.INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = NativeMethods.OwnInputMarker } },
    };

    private static void Send(params NativeMethods.INPUT[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

    /// <summary>Lo schermo di questo PC è a tocco (SM_MAXIMUMTOUCHES &gt; 0).</summary>
    internal static bool HasTouchScreen => GetSystemMetrics(95) > 0;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetPointerType(uint pointerId, out int pointerType);

    public void Dispose()
    {
        Enabled = false;
        _timer.Dispose();
    }
}
