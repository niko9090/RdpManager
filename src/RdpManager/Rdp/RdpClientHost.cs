using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RdpManager.Rdp;

/// <summary>
/// Ospita il controllo ActiveX Desktop remoto (mstscax.dll, MsRdpClient*NotSafeForScripting)
/// tramite una sottoclasse di AxHost, senza wrapper generati da aximp.
/// Le proprietà si impostano in late binding (IDispatch) attraverso <see cref="Ocx"/>.
/// </summary>
public sealed class RdpClientHost : AxHost
{
    // Dal più recente al più vecchio; si usa il primo registrato sul sistema.
    private static readonly (string Clsid, string Name)[] Candidates =
    [
        ("A0C63C30-F08D-4AB4-907C-34905D770C7D", "MsRdpClient10NotSafeForScripting"),
        ("8B918B82-7985-4C24-89DF-C33AD2BBFBCD", "MsRdpClient9NotSafeForScripting"),
        ("A3BC03A0-041D-42E3-AD22-882B7865C9C5", "MsRdpClient8NotSafeForScripting"),
        ("54D38BF7-B1EF-4479-9674-1BD6EA465258", "MsRdpClient7NotSafeForScripting"),
        ("D2EA46A7-C2BF-426B-AF24-E19C44456399", "MsRdpClient6NotSafeForScripting"),
    ];

    private static readonly Lazy<(string Clsid, string Name)?> Available = new(FindAvailable);

    private readonly EventSink _sink;
    private ConnectionPointCookie? _cookie;
    private object? _ocx;

    public static string? AvailableClassName => Available.Value?.Name;

    /// <exception cref="InvalidOperationException">Controllo RDP non registrato sul sistema.</exception>
    public static RdpClientHost Create()
    {
        var found = Available.Value
            ?? throw new InvalidOperationException("Il controllo Desktop remoto (mstscax.dll) non è registrato su questo PC.");
        return new RdpClientHost(found.Clsid);
    }

    private RdpClientHost(string clsid) : base(clsid)
    {
        _sink = new EventSink(this);
    }

    public event EventHandler? Connecting;
    public event EventHandler? Connected;
    public event EventHandler? LoginComplete;
    public event EventHandler<int>? Disconnected;
    public event EventHandler? EnterFullScreen;
    public event EventHandler? LeaveFullScreen;
    public event EventHandler<int>? FatalError;
    public event EventHandler<int>? Warning;
    public event EventHandler<int>? LogonError;
    /// <summary>Il controllo mostra (o chiude) il proprio avviso sull'identità/certificato del server.</summary>
    public event EventHandler? AuthenticationWarningDisplayed;
    public event EventHandler? AuthenticationWarningDismissed;

    /// <summary>Oggetto COM del controllo (IMsRdpClientN) per l'accesso in late binding.</summary>
    public dynamic Ocx => _ocx ?? GetOcx() ?? throw new InvalidOperationException("Controllo RDP non ancora creato.");

    public bool IsOcxReady => (_ocx ?? GetOcx()) is not null;

    public IMsRdpClientNonScriptable3? NonScriptable => (_ocx ?? GetOcx()) as IMsRdpClientNonScriptable3;

    protected override void AttachInterfaces() => _ocx = GetOcx();

    protected override void CreateSink()
    {
        try
        {
            _cookie = new ConnectionPointCookie(GetOcx()!, _sink, typeof(IMsTscAxEvents));
        }
        catch (Exception)
        {
            _cookie = null; // senza eventi la sessione funziona comunque, ma lo stato non si aggiorna
        }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }

    private static (string, string)? FindAvailable()
    {
        foreach (var candidate in Candidates)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{candidate.Clsid}}}\InprocServer32");
            if (key is not null)
                return candidate;
        }
        return null;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class EventSink(RdpClientHost owner) : IMsTscAxEvents
    {
        public void OnConnecting() => owner.Connecting?.Invoke(owner, EventArgs.Empty);
        public void OnConnected() => owner.Connected?.Invoke(owner, EventArgs.Empty);
        public void OnLoginComplete() => owner.LoginComplete?.Invoke(owner, EventArgs.Empty);
        public void OnDisconnected(int discReason) => owner.Disconnected?.Invoke(owner, discReason);
        public void OnEnterFullScreenMode() => owner.EnterFullScreen?.Invoke(owner, EventArgs.Empty);
        public void OnLeaveFullScreenMode() => owner.LeaveFullScreen?.Invoke(owner, EventArgs.Empty);
        public void OnFatalError(int errorCode) => owner.FatalError?.Invoke(owner, errorCode);
        public void OnWarning(int warningCode) => owner.Warning?.Invoke(owner, warningCode);
        public void OnLogonError(int lError) => owner.LogonError?.Invoke(owner, lError);
        public void OnAuthenticationWarningDisplayed() => owner.AuthenticationWarningDisplayed?.Invoke(owner, EventArgs.Empty);
        public void OnAuthenticationWarningDismissed() => owner.AuthenticationWarningDismissed?.Invoke(owner, EventArgs.Empty);
    }
}
