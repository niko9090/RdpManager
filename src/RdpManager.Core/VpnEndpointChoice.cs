using System.Net;
using System.Net.Sockets;

namespace RdpManager.Core;

/// <summary>
/// Quale endpoint usare per il server: il nome pubblico, oppure l'indirizzo LAN del server quando il PC sta nella sua rete
/// (il FRITZ!Box e molti router non fanno l'hairpin NAT in UDP: dalla LAN il nome pubblico non risponde mai).
/// </summary>
public static class VpnEndpointChoice
{
    /// <summary>Senza handshake dopo questo tempo dall'accensione si prova l'altro endpoint (pubblico ↔ LAN).</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Indirizzi IPv4/IPv6 delle schede attive di questo PC (vuoto se il sistema non li dà).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Vero se uno degli indirizzi locali sta nella stessa /24 dell'IP del server (endpoint LAN come IP:porta).</summary>
    public static bool OnServerLan(string? lanEndpoint, IEnumerable<IPAddress> localAddresses)
    {
        if (!WireGuardConfig.TryParseEndpoint(lanEndpoint, out var host, out _) || !IPAddress.TryParse(host, out var server)
            || server.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var s = server.GetAddressBytes();
        return localAddresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork
            && a.GetAddressBytes() is { } b && b[0] == s[0] && b[1] == s[1] && b[2] == s[2] && !IPAddress.IsLoopback(a));
    }

    /// <summary>La configurazione con l'endpoint scelto (se non c'è un endpoint LAN resta il pubblico).</summary>
    public static WireGuardConfig Apply(WireGuardConfig config, bool useLan)
        => useLan && config.LanEndpoint.Length > 0 ? config with { Endpoint = config.LanEndpoint } : config;
}
