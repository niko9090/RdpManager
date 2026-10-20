using System.ComponentModel;
using System.Runtime.InteropServices;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Hook di tastiera a basso livello (WH_KEYBOARD_LL) per le scorciatoie che devono funzionare anche col
/// fuoco nella sessione remota: il controllo RDP riceve i tasti prima di WinForms, quindi i filtri
/// dei messaggi di WinForms non li vedrebbero. L'hook si installa solo mentre l'app è in primo piano (WM_ACTIVATEAPP) e, per
/// sicurezza, ogni tasto viene esaminato solo se la finestra in primo piano è di questo processo.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Func<KeyCombo, bool> _onKeyDown;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // tenuto in vita: il sistema chiama questo delegato
    private IntPtr _handle;

    /// <param name="onKeyDown">Chiamata sul thread dell'interfaccia; restituisce vero se il tasto va trattenuto (non arriva alla sessione).</param>
    public KeyboardHook(Func<KeyCombo, bool> onKeyDown)
    {
        _onKeyDown = onKeyDown;
        _proc = HookProc;
    }

    public bool IsInstalled => _handle != IntPtr.Zero;

    /// <summary>Con vero l'hook lascia passare tutto (es. mentre si registra una nuova scorciatoia nelle impostazioni).</summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// Installa l'hook o lo reinstalla: l'ultimo hook installato è il primo a essere chiamato, quindi
    /// reinstallarlo dopo la connessione di una sessione lo rimette davanti a quello del controllo RDP.
    /// </summary>
    public void Install()
    {
        Uninstall();
        _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Hook di tastiera non installato");
    }

    public void Uninstall()
    {
        if (_handle == IntPtr.Zero)
            return;
        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Suspended && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != NativeMethods.OwnInputMarker && NativeMethods.IsOwnProcessForeground())
            {
                var combo = KeyCombo.FromVirtualKey((int)data.vkCode,
                    NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
                    NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
                    NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT));
                bool handled;
                try { handled = !combo.IsEmpty && combo.HasCommandModifier && _onKeyDown(combo); }
                catch (Exception) { handled = false; } // un errore qui non deve bloccare la tastiera
                if (handled)
                    return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
