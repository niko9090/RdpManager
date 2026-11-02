using System.Globalization;
using System.Net;
using System.Text;

namespace RdpManager.Core;

/// <summary>Valori fissi della VPN di RdpManager: rete a stella 10.66.0.0/24, server sul Synology.</summary>
public static class VpnDefaults
{
    public const string DefaultEndpoint = "syncnetpro.com:51820";
    public const string Network = "10.66.0.0/24";
    public const string ServerAddress = "10.66.0.1";
    public const int Keepalive = 25;
    public const string TunnelName = "RdpManager";
}

/// <summary>Configurazione WireGuard del client: sezione [Interface] e un solo [Peer] (il server).</summary>
public sealed record WireGuardConfig
{
    public string PrivateKey { get; init; } = "";
    /// <summary>Indirizzo del PC nel tunnel, in CIDR (per esempio 10.66.0.2/32).</summary>
    public string Address { get; init; } = "";
    /// <summary>Server DNS del tunnel (facoltativo).</summary>
    public string Dns { get; init; } = "";
    /// <summary>Chiave pubblica del server.</summary>
    public string PeerPublicKey { get; init; } = "";
    public string Endpoint { get; init; } = VpnDefaults.DefaultEndpoint;
    public string AllowedIPs { get; init; } = VpnDefaults.Network;
    public int PersistentKeepalive { get; init; } = VpnDefaults.Keepalive;

    /// <summary>Elenco dei problemi (vuoto se la configurazione è valida).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!WireGuardKeys.IsValidKey(PrivateKey))
            errors.Add("La chiave privata non è valida (32 byte in base64).");
        if (!TryParseCidr(Address, out _, out _))
            errors.Add("L'indirizzo del PC deve essere IPv4 con prefisso, per esempio 10.66.0.2/32.");
        if (Dns.Length > 0 && !Dns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .All(d => IPAddress.TryParse(d, out _)))
            errors.Add("Il DNS deve essere uno o più indirizzi IP separati da virgole.");
        if (!WireGuardKeys.IsValidKey(PeerPublicKey))
            errors.Add("La chiave pubblica del server non è valida (32 byte in base64).");
        if (!TryParseEndpoint(Endpoint, out _, out _))
            errors.Add("L'endpoint deve essere nome-o-IP:porta, per esempio syncnetpro.com:51820.");
        var allowed = AllowedIPs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (allowed.Length == 0 || !allowed.All(a => TryParseCidr(a, out _, out _)))
            errors.Add("AllowedIPs deve contenere reti IPv4 con prefisso (per esempio 10.66.0.0/24).");
        if (PersistentKeepalive is < 0 or > 65535)
            errors.Add("PersistentKeepalive deve essere tra 0 e 65535.");
        return errors;
    }

    public bool IsValid => Validate().Count == 0;

    /// <summary>Testo del file .conf.</summary>
    public string ToConf()
    {
        var sb = new StringBuilder();
        sb.Append("[Interface]\r\n");
        sb.Append("PrivateKey = ").Append(PrivateKey).Append("\r\n");
        sb.Append("Address = ").Append(Address).Append("\r\n");
        if (Dns.Length > 0)
            sb.Append("DNS = ").Append(Dns).Append("\r\n");
        sb.Append("\r\n[Peer]\r\n");
        sb.Append("PublicKey = ").Append(PeerPublicKey).Append("\r\n");
        sb.Append("Endpoint = ").Append(Endpoint).Append("\r\n");
        sb.Append("AllowedIPs = ").Append(AllowedIPs).Append("\r\n");
        sb.Append("PersistentKeepalive = ").Append(PersistentKeepalive.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Legge un file .conf con un [Interface] e un [Peer]; <see cref="FormatException"/> se la struttura non torna.</summary>
    public static WireGuardConfig Parse(string conf)
    {
        ArgumentNullException.ThrowIfNull(conf);
        var cfg = new WireGuardConfig { Endpoint = "", AllowedIPs = "", PersistentKeepalive = 0 };
        string section = "";
        int peers = 0;
        foreach (var raw in conf.Split('\n'))
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']'))
                    throw new FormatException($"Sezione non valida: {line}");
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section == "peer" && ++peers > 1)
                    throw new FormatException("Il file ha più di un [Peer]: ne è previsto uno solo (il server).");
                if (section is not ("interface" or "peer"))
                    throw new FormatException($"Sezione sconosciuta: {line}");
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || section.Length == 0)
                throw new FormatException($"Riga non valida: {line}");
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            cfg = (section, key) switch
            {
                ("interface", "privatekey") => cfg with { PrivateKey = value },
                ("interface", "address") => cfg with { Address = value },
                ("interface", "dns") => cfg with { Dns = value },
                ("peer", "publickey") => cfg with { PeerPublicKey = value },
                ("peer", "endpoint") => cfg with { Endpoint = value },
                ("peer", "allowedips") => cfg with { AllowedIPs = value },
                ("peer", "persistentkeepalive") when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ka)
                    => cfg with { PersistentKeepalive = ka },
                ("peer", "persistentkeepalive") => throw new FormatException($"PersistentKeepalive non valido: {value}"),
                _ => cfg, // chiavi che RdpManager non usa (MTU, PresharedKey…): ignorate
            };
        }
        if (peers == 0)
            throw new FormatException("Manca la sezione [Peer].");
        return cfg;
    }

    /// <summary>Frammento [Peer] da aggiungere alla configurazione del server per questo PC.</summary>
    public string ToServerPeerFragment(string pcName = "")
        => BuildServerPeerFragment(WireGuardKeys.PublicKeyFromPrivate(PrivateKey), Address, pcName);

    /// <summary>[Peer] per il server: chiave pubblica del PC e il suo solo indirizzo come /32.</summary>
    public static string BuildServerPeerFragment(string pcPublicKey, string address, string pcName = "")
    {
        if (!WireGuardKeys.IsValidKey(pcPublicKey))
            throw new ArgumentException("Chiave pubblica non valida.", nameof(pcPublicKey));
        if (!TryParseCidr(address, out var ip, out _))
            throw new ArgumentException("Indirizzo non valido.", nameof(address));
        var sb = new StringBuilder();
        sb.Append("[Peer]\n");
        var label = new string((pcName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (label.Length > 0)
            sb.Append("# ").Append(label).Append('\n');
        sb.Append("PublicKey = ").Append(pcPublicKey).Append('\n');
        sb.Append("AllowedIPs = ").Append(ip).Append("/32\n");
        return sb.ToString();
    }

    /// <summary>Prossimo indirizzo libero 10.66.0.N/32 (da .2) dato l'elenco di quelli già in uso; null se la rete è piena.</summary>
    public static string? NextFreeAddress(IEnumerable<string> used)
    {
        var taken = used.Select(u => TryParseCidr(u, out var ip, out _) ? ip : u).ToHashSet();
        for (int n = 2; n <= 254; n++)
            if (!taken.Contains($"10.66.0.{n}"))
                return $"10.66.0.{n}/32";
        return null;
    }

    internal static bool TryParseCidr(string? text, out string ip, out int prefix)
    {
        ip = "";
        prefix = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parts[0].Split('.').Length != 4
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > 32)
            return false;
        ip = addr.ToString();
        return true;
    }

    internal static bool TryParseEndpoint(string? text, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        int colon = text.Trim().LastIndexOf(':');
        if (colon <= 0)
            return false;
        host = text.Trim()[..colon];
        if (!int.TryParse(text.Trim()[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            return false;
        return host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_') && !host.StartsWith('.');
    }
}
