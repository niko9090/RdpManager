using System.Runtime.InteropServices;

namespace RdpManager.Rdp;

// Dichiarazioni COM minime di mstscax.dll scritte a mano (niente aximp/interop generati).
// DISPID e posizioni nella vtable verificati sulla type library di mstscax.dll.

/// <summary>Interfaccia eventi (dispinterface) del controllo Desktop remoto.</summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(18)] void OnAuthenticationWarningDisplayed();
    [DispId(19)] void OnAuthenticationWarningDismissed();
    [DispId(22)] void OnLogonError(int lError);
}

/// <summary>
/// IMsRdpClientNonScriptable3 (solo vtable, non accessibile via IDispatch).
/// Serve per PromptForCredentials; gli slot precedenti non usati sono segnaposto.
/// </summary>
[ComImport]
[Guid("B3378D90-0728-45C7-8ED7-B6159FB92219")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable3
{
    // IMsTscNonScriptable (10 slot), IMsRdpClientNonScriptable (2), IMsRdpClientNonScriptable2 (2)
    void Slot03(); void Slot04(); void Slot05(); void Slot06(); void Slot07();
    void Slot08(); void Slot09(); void Slot10(); void Slot11(); void Slot12();
    void Slot13(); void Slot14(); void Slot15(); void Slot16();

    void put_ShowRedirectionWarningDialog([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ShowRedirectionWarningDialog();
    void put_PromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PromptForCredentials();
}

/// <summary>
/// IMsRdpClientNonScriptable5 (solo vtable). Serve per AllowPromptingForCredentials: con NLA
/// PromptForCredentials=false NON basta, il controllo apre comunque «Sicurezza di Windows»
/// (CredentialUIBroker, processo separato). Slot 63/64 verificati sulla type library.
/// </summary>
[ComImport]
[Guid("4F6996D5-D7B1-412C-B0FF-063718566907")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable5
{
    // Slot 3-62: IMsTscNonScriptable … IMsRdpClientNonScriptable5.WarnAboutDirectXRedirection (non usati)
    void Slot03(); void Slot04(); void Slot05(); void Slot06(); void Slot07(); void Slot08();
    void Slot09(); void Slot10(); void Slot11(); void Slot12(); void Slot13(); void Slot14();
    void Slot15(); void Slot16(); void Slot17(); void Slot18(); void Slot19(); void Slot20();
    void Slot21(); void Slot22(); void Slot23(); void Slot24(); void Slot25(); void Slot26();
    void Slot27(); void Slot28(); void Slot29(); void Slot30(); void Slot31(); void Slot32();
    void Slot33(); void Slot34(); void Slot35(); void Slot36(); void Slot37(); void Slot38();
    void Slot39(); void Slot40(); void Slot41(); void Slot42(); void Slot43(); void Slot44();
    void Slot45(); void Slot46(); void Slot47(); void Slot48(); void Slot49(); void Slot50();
    void Slot51(); void Slot52(); void Slot53(); void Slot54(); void Slot55(); void Slot56();
    void Slot57(); void Slot58(); void Slot59(); void Slot60(); void Slot61(); void Slot62();
    

    void put_AllowPromptingForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_AllowPromptingForCredentials();
}
