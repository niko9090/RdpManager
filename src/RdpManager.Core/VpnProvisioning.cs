using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Risposta del server a <c>POST /v1/vpn/devices</c>: tutto ciò che serve per il profilo (la chiave privata resta sul PC).</summary>
public sealed record VpnGrant(
    string Name, string Address, string ServerPublicKey, string Endpoint, string AllowedIPs, string Dns, int PersistentKeepalive)
{
    public static VpnGrant Parse(JsonElement json)
    {
        string Str(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var grant = new VpnGrant(
            Str("name"), Str("address"), Str("serverPublicKey"), Str("endpoint"),
            Str("allowedIPs") is { Length: > 0 } a ? a : VpnDefaults.Network, Str("dns"),
            json.TryGetProperty("persistentKeepalive", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : VpnDefaults.Keepalive);
        if (!WireGuardKeys.IsValidKey(grant.ServerPublicKey)
            || !WireGuardConfig.TryParseCidr(grant.Address, out _, out _)
            || grant.Endpoint.Length == 0)
            throw new FormatException("Risposta VPN del server incompleta o non valida.");
        return grant;
    }
}

/// <summary>
/// VPN automatica: dopo l'accesso all'account, se questo PC non ha ancora un profilo genera le chiavi, le chiede al server
/// (mandando solo la chiave pubblica) e completa <see cref="VpnProfile"/>. Nessun file da copiare, nessun passo manuale.
/// </summary>
public static class VpnProvisioning
{
    /// <summary>Nome accettato dal server: lettere, cifre, spazio e . _ - (il resto diventa «-»), al massimo 64 caratteri.</summary>
    public static string SafeName(string? name)
    {
        var s = new string((name ?? "").Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' ? c : '-').ToArray());
        return s.Length == 0 ? "PC" : s[..Math.Min(64, s.Length)];
    }

    /// <summary>Vero se manca qualcosa per accendere il tunnel (chiavi, indirizzo assegnato, chiave del server).</summary>
    public static bool NeedsProvisioning(VpnProfile p)
        => !p.HasKey || p.PublicKey is null || p.ServerPublicKey.Length == 0 || p.Endpoint.Length == 0
           || !WireGuardConfig.TryParseCidr(p.Address, out _, out _);

    /// <summary>Riporta nel profilo i dati assegnati dal server (la chiave privata non si tocca).</summary>
    public static void Apply(VpnProfile profile, VpnGrant grant)
    {
        profile.Address = grant.Address;
        profile.Endpoint = grant.Endpoint;
        profile.ServerPublicKey = grant.ServerPublicKey;
        profile.Dns = grant.Dns;
        if (grant.Name.Length > 0)
            profile.PcName = grant.Name;
    }

    /// <summary>
    /// Se serve, ottiene il profilo dal server e lo salva. Restituisce il profilo pronto, o null se non si è nell'account.
    /// Le chiavi si salvano prima della chiamata: se la rete cade, al tentativo dopo si rimanda la stessa chiave
    /// e il server risponde con lo stesso indirizzo.
    /// </summary>
    public static async Task<VpnProfile?> EnsureAsync(SyncService sync, string? profilePath = null, string? pcName = null, CancellationToken ct = default)
    {
        var profile = VpnProfile.Load(profilePath);
        if (!NeedsProvisioning(profile))
            return profile;
        if (!sync.SignedIn)
            return null;
        if (!profile.HasKey || profile.PublicKey is null)
            profile.GenerateKeys();
        profile.PcName = SafeName(!string.IsNullOrWhiteSpace(pcName) ? pcName
            : profile.PcName.Length > 0 ? profile.PcName : Environment.MachineName);
        profile.Save(profilePath);

        var grant = await sync.RequestVpnAsync(profile.PcName, profile.PublicKey!, ct);
        Apply(profile, grant);
        profile.Save(profilePath);
        return profile;
    }
}
