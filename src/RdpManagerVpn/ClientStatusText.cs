using RdpManager.Vpn;

namespace RdpManagerVpn;

/// <summary>Le scritte dell'icona, in italiano.</summary>
public static class ClientStatusText
{
    public static string Describe(TunnelState state, VpnStatus? details, DateTime nowUtc, bool serviceInstalled = true)
    {
        if (!serviceInstalled)
            return "Servizio non installato";
        return state switch
        {
            TunnelState.Acceso => details?.LastHandshakeUtc is { } hs
                ? (nowUtc - hs < ServicePolicy.HandshakeTimeout ? "Connesso" : "Server non raggiungibile")
                : "Connessione in corso…",
            TunnelState.InAvvio => "Connessione in corso…",
            TunnelState.InArresto => "Disconnessione in corso…",
            TunnelState.Errore => "Errore del servizio",
            _ => "Non connesso",
        };
    }

    /// <summary>Indirizzo mostrato nel menu: quello del profilo, o un trattino se non c'è.</summary>
    public static string Address(string ip) => ip.Length > 0 ? ip : "—";
}
