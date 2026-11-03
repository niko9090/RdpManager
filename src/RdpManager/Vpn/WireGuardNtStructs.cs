using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>Stato del tunnel letto dal driver: ultimo handshake e byte scambiati (somma dei peer).</summary>
public sealed record VpnStatus(DateTime? LastHandshakeUtc, ulong TxBytes, ulong RxBytes, DateTime UpdatedUtc)
{
    private const long MaxFileTime = 2650467743999999999L; // limite di DateTime.FromFileTimeUtc

    /// <summary>Formato del file di stato scritto dal servizio (righe chiave=valore).</summary>
    public string Format() => string.Join("\n",
        "handshake=" + (LastHandshakeUtc?.ToFileTimeUtc() ?? 0),
        "tx=" + TxBytes,
        "rx=" + RxBytes,
        "aggiornato=" + UpdatedUtc.ToFileTimeUtc()) + "\n";

    /// <summary>Legge il file di stato; null se è incompleto o rovinato.</summary>
    public static VpnStatus? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var values = new Dictionary<string, long>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || !long.TryParse(line[(eq + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                return null;
            values[line[..eq]] = v;
        }
        if (!values.TryGetValue("handshake", out var hs) || !values.TryGetValue("tx", out var tx)
            || !values.TryGetValue("rx", out var rx) || !values.TryGetValue("aggiornato", out var up)
            || up is <= 0 or > MaxFileTime || hs is < 0 or > MaxFileTime)
            return null;
        return new VpnStatus(hs == 0 ? null : DateTime.FromFileTimeUtc(hs), (ulong)tx, (ulong)rx, DateTime.FromFileTimeUtc(up));
    }

    internal static DateTime? FromFileTime(long ft) => ft > 0 && ft <= MaxFileTime ? DateTime.FromFileTimeUtc(ft) : null;
}

/// <summary>
/// Strutture binarie di wireguard-nt (wireguard.h 1.1) costruite a mano, perché il tunnel le passa a
/// <c>WireGuardSetConfiguration</c> come un unico blocco: WIREGUARD_INTERFACE (80 byte) seguita da ogni WIREGUARD_PEER
/// (136 byte) e dai suoi WIREGUARD_ALLOWED_IP (24 byte ciascuno), tutto allineato a 8 byte. Qui anche le righe di
/// MIB_UNICASTIPADDRESS_ROW (80 byte) e MIB_IPFORWARD_ROW2 (104 byte) di iphlpapi.
/// </summary>
public static class WireGuardNtStructs
{
    public const int KeyLength = 32;
    public const int InterfaceSize = 80;
    public const int PeerSize = 136;
    public const int AllowedIpSize = 24;
    public const int UnicastRowSize = 80;
    public const int ForwardRowSize = 104;

    // WIREGUARD_INTERFACE_FLAG
    public const uint InterfaceHasPrivateKey = 1 << 1;
    public const uint InterfaceReplacePeers = 1 << 3;
    // WIREGUARD_PEER_FLAG
    public const uint PeerHasPublicKey = 1 << 0;
    public const uint PeerHasKeepalive = 1 << 2;
    public const uint PeerHasEndpoint = 1 << 3;
    public const uint PeerReplaceAllowedIps = 1 << 5;

    public const ushort AfInet = 2;
    public const ushort AfInet6 = 23;

    // offset dei campi
    public const int IfPrivateKey = 6, IfPeersCount = 72;
    public const int PeerPublicKey = 8, PeerKeepalive = 72, PeerEndpoint = 76, PeerTx = 104, PeerRx = 112,
        PeerHandshake = 120, PeerAllowedCount = 128;

    /// <summary>Costruisce il blocco per WireGuardSetConfiguration: interfaccia con chiave privata, un peer con endpoint già risolto.</summary>
    public static byte[] BuildConfiguration(WireGuardConfig config, IPAddress endpointIp)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpointIp);
        if (!WireGuardKeys.TryDecode(config.PrivateKey, out var priv))
            throw new ArgumentException("Chiave privata non valida.", nameof(config));
        if (!WireGuardKeys.TryDecode(config.PeerPublicKey, out var peerPub))
            throw new ArgumentException("Chiave pubblica del server non valida.", nameof(config));
        if (!WireGuardConfig.TryParseEndpoint(config.Endpoint, out _, out var port))
            throw new ArgumentException("Endpoint non valido.", nameof(config));

        var allowed = new List<(IPAddress Ip, int Cidr)>();
        foreach (var a in config.AllowedIPs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!WireGuardConfig.TryParseCidr(a, out var ip, out var cidr))
                throw new ArgumentException($"AllowedIPs non valido: {a}", nameof(config));
            allowed.Add((IPAddress.Parse(ip), cidr));
        }

        var buf = new byte[InterfaceSize + PeerSize + allowed.Count * AllowedIpSize];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, InterfaceHasPrivateKey | InterfaceReplacePeers);
        priv.CopyTo(s[IfPrivateKey..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[IfPeersCount..], 1);

        var p = s[InterfaceSize..];
        var flags = PeerHasPublicKey | PeerHasEndpoint | PeerReplaceAllowedIps | (config.PersistentKeepalive > 0 ? PeerHasKeepalive : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p, flags);
        peerPub.CopyTo(p[PeerPublicKey..]);
        BinaryPrimitives.WriteUInt16LittleEndian(p[PeerKeepalive..], (ushort)config.PersistentKeepalive);
        WriteSockaddr(p[PeerEndpoint..], endpointIp, port);
        BinaryPrimitives.WriteUInt32LittleEndian(p[PeerAllowedCount..], (uint)allowed.Count);

        for (int i = 0; i < allowed.Count; i++)
        {
            var a = p[(PeerSize + i * AllowedIpSize)..];
            allowed[i].Ip.GetAddressBytes().CopyTo(a);
            BinaryPrimitives.WriteUInt16LittleEndian(a[16..], AfInet);
            a[18] = (byte)allowed[i].Cidr;
        }
        return buf;
    }

    /// <summary>Scrive un SOCKADDR_INET (28 byte): famiglia, porta in network order, indirizzo.</summary>
    public static void WriteSockaddr(Span<byte> dest, IPAddress ip, int port)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, AfInet);
            BinaryPrimitives.WriteUInt16BigEndian(dest[2..], (ushort)port);
            ip.GetAddressBytes().CopyTo(dest[4..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, AfInet6);
            BinaryPrimitives.WriteUInt16BigEndian(dest[2..], (ushort)port);
            ip.GetAddressBytes().CopyTo(dest[8..]);
        }
    }

    /// <summary>Somma i peer del blocco restituito da WireGuardGetConfiguration; null se il blocco è troppo corto o incoerente.</summary>
    public static VpnStatus? ParseStatus(ReadOnlySpan<byte> buf, DateTime nowUtc)
    {
        if (buf.Length < InterfaceSize)
            return null;
        uint peers = BinaryPrimitives.ReadUInt32LittleEndian(buf[IfPeersCount..]);
        long offset = InterfaceSize;
        ulong tx = 0, rx = 0;
        long last = 0;
        for (uint i = 0; i < peers; i++)
        {
            if (buf.Length < offset + PeerSize)
                return null;
            var p = buf[(int)offset..];
            tx += BinaryPrimitives.ReadUInt64LittleEndian(p[PeerTx..]);
            rx += BinaryPrimitives.ReadUInt64LittleEndian(p[PeerRx..]);
            last = Math.Max(last, BinaryPrimitives.ReadInt64LittleEndian(p[PeerHandshake..]));
            offset += PeerSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(p[PeerAllowedCount..]) * AllowedIpSize;
            if (offset > buf.Length)
                return null;
        }
        return new VpnStatus(VpnStatus.FromFileTime(last), tx, rx, nowUtc);
    }

    /// <summary>MIB_UNICASTIPADDRESS_ROW per un indirizzo IPv4 fisso sull'adattatore.</summary>
    public static byte[] BuildUnicastRow(ulong luid, IPAddress ip, int prefix)
    {
        var row = new byte[UnicastRowSize];
        var s = row.AsSpan();
        WriteSockaddr(s, ip, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(s[32..], luid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 1);             // PrefixOrigin = manual
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 1);             // SuffixOrigin = manual
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], uint.MaxValue); // ValidLifetime: infinita
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], uint.MaxValue); // PreferredLifetime: infinita
        row[60] = (byte)prefix;                                           // OnLinkPrefixLength
        return row;
    }

    /// <summary>MIB_IPFORWARD_ROW2 per una rotta IPv4 sull'adattatore (prossimo salto 0.0.0.0 = direttamente sul collegamento).</summary>
    public static byte[] BuildForwardRow(ulong luid, IPAddress network, int prefix, uint metric = 0)
    {
        var row = new byte[ForwardRowSize];
        var s = row.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s, luid);
        WriteSockaddr(s[12..], network, 0);
        s[40] = (byte)prefix;
        BinaryPrimitives.WriteUInt16LittleEndian(s[44..], AfInet);        // NextHop 0.0.0.0
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], metric);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 3);             // Protocol = NetMgmt
        return row;
    }
}
