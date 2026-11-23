using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Accensione automatica della VPN all'avvio. Se il servizio non è installato serve l'amministratore (UAC): se l'utente
/// rifiuta, si salva <see cref="VpnProfile.AutoConnectSkipsInstall"/> e agli avvii dopo non si richiede più, ma si avvisa.
/// </summary>
public static class VpnAutoStart
{
    public const string NeedsAdminMessage = "VPN non accesa: serve il permesso di amministratore, premi Collega nella finestra VPN.";

    /// <summary>Restituisce una riga di stato ("" = niente da dire). <paramref name="save"/> salva il profilo.</summary>
    public static string Run(VpnProfile profile, IWireGuardTunnel tunnel, IVpnServiceControl control, Action<VpnProfile> save)
    {
        if (tunnel.Stato is TunnelState.Acceso or TunnelState.InAvvio)
            return "";
        var installed = control.Exists(WireGuardNtTunnel.ServiceName);
        if (!installed && profile.AutoConnectSkipsInstall)
            return NeedsAdminMessage;
        try
        {
            tunnel.Accendi(profile.ToConfig());
            return "VPN collegata.";
        }
        catch (VpnElevationDeclinedException) when (!installed)
        {
            profile.AutoConnectSkipsInstall = true;
            save(profile);
            return NeedsAdminMessage;
        }
    }

    /// <summary>Accensione a mano riuscita: toglie il segno (se c'è) e salva.</summary>
    public static void ManualConnectSucceeded(VpnProfile profile, Action<VpnProfile> save)
    {
        if (!profile.AutoConnectSkipsInstall)
            return;
        profile.AutoConnectSkipsInstall = false;
        save(profile);
    }
}
