using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RdpManager.Core;

/// <summary>Che tipo di rete c'è dietro una scheda di rete.</summary>
public enum NetworkKind
{
    /// <summary>Rete locale (cavo o Wi-Fi).</summary>
    Local,
    Tailscale,
    ZeroTier,
    WireGuard,
    OpenVPN,
    /// <summary>Un'altra VPN riconosciuta solo in modo generico (PPP, «VPN» nel nome, client aziendali).</summary>
    OtherVpn,
}

/// <summary>Una scheda di rete attiva con un indirizzo IPv4 e la sua sottorete.</summary>
public sealed record AdapterInfo(
    string Name, string Description, NetworkInterfaceType Type, IPAddress Address, int Prefix, NetworkKind Kind)
{
    public bool IsVpn => Kind != NetworkKind.Local;

    public string KindName => VpnProviders.DisplayName(Kind);

    /// <summary>Sottorete della scheda (senza rete e broadcast).</summary>
    public IpRange Subnet => IpRange.FromPrefix(Address, Prefix);

    /// <summary>
    /// Intervallo proposto per la scansione: la sottorete se non supera una /22; altrimenti la /24 attorno al
    /// proprio indirizzo (la rete intera resta scrivibile a mano, con conferma).
    /// </summary>
    public IpRange SuggestedRange => Prefix >= ScanPolicy.MaxPrefixWithoutConfirmation ? Subnet : IpRange.FromPrefix(Address, 24);

    public override string ToString() => $"{Name} ({KindName}) - {Address}/{Prefix}";
}

/// <summary>
/// Riconosce un tipo di VPN dalla scheda di rete. Per aggiungerne una basta un nuovo fornitore in
/// <see cref="VpnProviders.All"/>: si provano in ordine e vince il primo che la riconosce.
/// </summary>
public interface IVpnProvider
{
    NetworkKind Kind { get; }
    string DisplayName { get; }
    bool Matches(string name, string description, NetworkInterfaceType type, IPAddress? address);
}

/// <summary>Fornitore che riconosce la scheda da parole nel nome o nella descrizione (senza maiuscole).</summary>
public sealed class KeywordVpnProvider(NetworkKind kind, string displayName, params string[] keywords) : IVpnProvider
{
    public NetworkKind Kind { get; } = kind;
    public string DisplayName { get; } = displayName;
    public IReadOnlyList<string> Keywords { get; } = keywords;

    public bool Matches(string name, string description, NetworkInterfaceType type, IPAddress? address) =>
        Keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase) || description.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>VPN generiche: collegamenti PPP o tunnel, oppure client VPN noti o con «VPN» nel nome.</summary>
public sealed class GenericVpnProvider : IVpnProvider
{
    private static readonly string[] Keywords =
    [
        "VPN", "AnyConnect", "GlobalProtect", "PANGP", "Fortinet", "FortiClient", "SonicWall", "NetExtender",
        "Juniper", "Pulse Secure", "Ivanti", "Check Point", "Sophos", "WatchGuard", "Zscaler", "NordLynx",
        "Hamachi", "Netbird", "Nebula", "Twingate", "Cloudflare WARP",
    ];

    public NetworkKind Kind => NetworkKind.OtherVpn;
    public string DisplayName => "Altra VPN";

    public bool Matches(string name, string description, NetworkInterfaceType type, IPAddress? address) =>
        type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
        || Keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase) || description.Contains(k, StringComparison.OrdinalIgnoreCase));
}

public static class VpnProviders
{
    /// <summary>Fornitori in ordine di prova: prima quelli specifici, l'ultimo è quello generico.</summary>
    public static IReadOnlyList<IVpnProvider> All { get; } =
    [
        new KeywordVpnProvider(NetworkKind.Tailscale, "Tailscale", "Tailscale"),
        new KeywordVpnProvider(NetworkKind.ZeroTier, "ZeroTier", "ZeroTier"),
        new KeywordVpnProvider(NetworkKind.WireGuard, "WireGuard", "WireGuard"),
        new KeywordVpnProvider(NetworkKind.OpenVPN, "OpenVPN", "OpenVPN", "TAP-Windows", "TAP-Win32", "ovpn-dco", "Data Channel Offload"),
        new GenericVpnProvider(),
    ];

    /// <summary>Tipo di rete della scheda: il primo fornitore che la riconosce, altrimenti rete locale.</summary>
    public static NetworkKind Classify(string? name, string? description, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        IPAddress? address = null, IEnumerable<IVpnProvider>? providers = null)
    {
        name ??= "";
        description ??= "";
        foreach (var p in providers ?? All)
        {
            if (p.Matches(name, description, type, address))
                return p.Kind;
        }
        return NetworkKind.Local;
    }

    public static string DisplayName(NetworkKind kind) => kind switch
    {
        NetworkKind.Local => "Rete locale",
        NetworkKind.OtherVpn => "Altra VPN",
        _ => All.FirstOrDefault(p => p.Kind == kind)?.DisplayName ?? kind.ToString(),
    };
}

public static class NetworkAdapters
{
    /// <summary>
    /// Schede di rete attive con un indirizzo IPv4 (escluse loopback e indirizzi 169.254 automatici), con il tipo
    /// di rete riconosciuto. Solo lettura: nessuna impostazione viene cambiata.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> ReadActive()
    {
        var result = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || AddressScopes.Of(ua.Address) is AddressScope.LinkLocal or AddressScope.Loopback)
                    continue;
                int prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength : 24;
                var kind = VpnProviders.Classify(nic.Name, nic.Description, nic.NetworkInterfaceType, ua.Address);
                result.Add(new AdapterInfo(nic.Name, nic.Description, nic.NetworkInterfaceType, ua.Address, prefix, kind));
            }
        }
        return result;
    }
}
