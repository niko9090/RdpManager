using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Se sul PC c'è il client marshmelloVPN (servizio «RdpManagerVpnClient») la VPN la gestisce già lui: RdpManager non
/// accende né registra un secondo tunnel (due tunnel e due registrazioni dello stesso PC).
/// </summary>
public static class VpnClientGuard
{
    public const string SkipMessage = "VPN gestita da marshmelloVPN (servizio " + VpnExitHandler.ClientServiceName
        + "): RdpManager non accende né registra il suo tunnel.";

    /// <summary>Vero se il client marshmelloVPN è installato (nel dubbio sì: meglio non creare un secondo tunnel).</summary>
    public static bool IsManagedByClient(IVpnServiceControl control, Action<string>? log = null) =>
        new VpnExitHandler(new NullTunnel(), control, log).MarshmelloVpnInstalled();

    private sealed class NullTunnel : IWireGuardTunnel
    {
        public TunnelState Stato => TunnelState.Spento;
        public void Accendi(WireGuardConfig config) { }
        public void Spegni() { }
    }
}

/// <summary>
/// Controllo periodico (Windows) che riaccende la VPN integrata se il profilo ha «collegati all'avvio» e il tunnel è giù.
/// Se l'utente rifiuta l'elevazione (UAC, codice 1223) non si riprova finché non riparte il programma o l'utente agisce
/// (<see cref="Reset"/>): niente richieste UAC in ciclo.
/// </summary>
public sealed class VpnAutoReconnect(IWireGuardTunnel tunnel, IVpnServiceControl control, Action<string>? log = null)
{
    private bool _declined;

    public bool Declined => _declined;

    /// <summary>L'utente ha agito (per esempio ha premuto Collega): si può riprovare.</summary>
    public void Reset() => _declined = false;

    /// <summary>Un giro del controllo. Restituisce una riga di stato ("" = niente da dire).</summary>
    public string Check(VpnProfile profile, Action<VpnProfile> save)
    {
        if (_declined || !VpnProvisioning.ShouldAutoConnect(profile))
            return "";
        if (tunnel.Stato is TunnelState.Acceso or TunnelState.InAvvio)
            return "";
        if (VpnClientGuard.IsManagedByClient(control, log))
            return "";
        try
        {
            var line = VpnAutoStart.Run(profile, tunnel, control, save);
            if (line == VpnAutoStart.NeedsAdminMessage)
            {
                _declined = true;
                return "";
            }
            if (line.Length > 0)
                log?.Invoke("VPN riaccesa dal controllo periodico");
            return line;
        }
        catch (VpnElevationDeclinedException ex)
        {
            _declined = true;
            log?.Invoke("VPN non riaccesa: permesso di amministratore rifiutato, non richiedo più fino al prossimo avvio. " + ex.Message);
            return VpnAutoStart.NeedsAdminMessage;
        }
    }
}
