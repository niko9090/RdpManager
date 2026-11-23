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

/// <summary>
/// La VPN nostra (WireGuard): «WireGuard» nel nome o nella descrizione (Windows), il nome del tunnel di RdpManager
/// oppure un nome tipo «wg0» (Linux, dove la scheda porta solo il nome dell'interfaccia).
/// </summary>
public sealed class WireGuardVpnProvider : IVpnProvider
{
    public NetworkKind Kind => NetworkKind.WireGuard;
    public string DisplayName => "WireGuard";

    public bool Matches(string name, string description, NetworkInterfaceType type, IPAddress? address) =>
        name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, VpnDefaults.TunnelName, StringComparison.OrdinalIgnoreCase)
        || IsWgInterfaceName(name);

    private static bool IsWgInterfaceName(string name) =>
        name.Length is >= 2 and <= 6 && name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) && name.Skip(2).All(char.IsDigit);
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
        new WireGuardVpnProvider(),
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

/// <summary>Una scheda di rete come la si mostra all'utente: collegata o no, con IP del PC e rete (es. 10.66.0.0/24).</summary>
public sealed record AdapterCard(string Name, string Description, NetworkKind Kind, bool Connected, IPAddress? Address, int Prefix)
{
    public bool IsVpn => Kind != NetworkKind.Local;

    /// <summary>La VPN nostra (WireGuard).</summary>
    public bool IsOurVpn => Kind == NetworkKind.WireGuard;

    public string StateText => Connected ? "collegata" : "non collegata";

    /// <summary>Rete della scheda nella forma «10.66.0.0/24» (vuota se la scheda non ha un indirizzo).</summary>
    public string NetworkText => Address is null ? "" : NetworkOf(Address, Prefix);

    public string AddressText => Address?.ToString() ?? "";

    public static string NetworkOf(IPAddress address, int prefix)
    {
        var b = address.GetAddressBytes();
        uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = prefix <= 0 ? 0 : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
        uint net = ip & mask;
        return $"{net >> 24}.{net >> 16 & 255}.{net >> 8 & 255}.{net & 255}/{prefix}";
    }

    public static AdapterCard From(AdapterInfo a) => new(a.Name, a.Description, a.Kind, true, a.Address, a.Prefix);
}

/// <summary>
/// Quadro delle schede di rete per la ricerca reti: le schede VPN (la nostra per prima) in cima, poi le locali, e lo
/// stato della VPN nostra. La rete da proporre è quella della VPN solo quando è collegata.
/// </summary>
public sealed class NetworkOverview
{
    public const string VpnLabel = "VPN";

    private NetworkOverview(IReadOnlyList<AdapterCard> cards, AdapterInfo? suggested)
    {
        Cards = cards;
        Suggested = suggested;
        Vpn = cards.FirstOrDefault(c => c.IsOurVpn && c.Connected) ?? cards.FirstOrDefault(c => c.IsOurVpn);
    }

    public IReadOnlyList<AdapterCard> Cards { get; }

    /// <summary>La scheda della VPN nostra (collegata se c'è, altrimenti una non collegata); null se non esiste nessuna.</summary>
    public AdapterCard? Vpn { get; }

    public bool VpnConnected => Vpn is { Connected: true };

    /// <summary>La scheda collegata della VPN, da proporre per la scansione (null se la VPN non è collegata).</summary>
    public AdapterInfo? Suggested { get; }

    /// <summary>Frase di stato ben visibile: collegata con IP e rete, oppure non collegata.</summary>
    public string Headline => Vpn switch
    {
        { Connected: true } v => $"{VpnLabel} COLLEGATA - il tuo IP nella VPN è {v.AddressText}, rete {v.NetworkText} (scheda «{v.Name}»).",
        { } v => $"{VpnLabel} NON COLLEGATA - la scheda «{v.Name}» c'è ma è spenta: collega la VPN per cercare i PC nella sua rete.",
        null => $"{VpnLabel} NON COLLEGATA - su questo PC non si vede nessuna scheda della VPN: collega la VPN (o configurala) per cercare i PC nella sua rete.",
    };

    public static NetworkOverview Build(IReadOnlyList<AdapterInfo> active, IReadOnlyList<AdapterCard>? inactive = null)
    {
        var all = active.Select(AdapterCard.From)
            .Concat((inactive ?? []).Where(c => !active.Any(a => a.Name == c.Name)))
            .ToList();
        // Prima la VPN nostra (collegate prima), poi le altre VPN, poi le reti locali; a parità si tiene l'ordine di lettura.
        var ordered = all
            .Select((c, i) => (c, i))
            .OrderBy(x => x.c.IsOurVpn ? 0 : x.c.IsVpn ? 1 : 2)
            .ThenBy(x => x.c.Connected ? 0 : 1)
            .ThenBy(x => x.i)
            .Select(x => x.c)
            .ToList();
        var suggested = active.FirstOrDefault(a => a.Kind == NetworkKind.WireGuard);
        return new NetworkOverview(ordered, suggested);
    }

    /// <summary>Come <see cref="Build"/>, leggendo anche le schede VPN spente dal sistema.</summary>
    public static NetworkOverview Read(IReadOnlyList<AdapterInfo> active) => Build(active, NetworkAdapters.ReadInactiveVpn());
}

public static class NetworkAdapters
{
    /// <summary>
    /// Schede VPN presenti ma spente o senza indirizzo IPv4 (per dire «VPN non collegata»). Solo lettura.
    /// </summary>
    public static IReadOnlyList<AdapterCard> ReadInactiveVpn()
    {
        var result = new List<AdapterCard>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var kind = VpnProviders.Classify(nic.Name, nic.Description, nic.NetworkInterfaceType);
                if (kind != NetworkKind.WireGuard)
                    continue; // basta la VPN nostra: le schede spente delle altre non interessano
                bool hasIpv4 = false;
                if (nic.OperationalStatus == OperationalStatus.Up)
                {
                    try
                    {
                        hasIpv4 = nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                    }
                    catch (NetworkInformationException)
                    {
                    }
                }
                if (!hasIpv4)
                    result.Add(new AdapterCard(nic.Name, nic.Description, kind, false, null, 0));
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result;
    }

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
