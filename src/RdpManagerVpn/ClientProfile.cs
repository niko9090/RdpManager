using RdpManager.Core;

namespace RdpManagerVpn;

/// <summary>Esito della lettura di vpn.conf: configurazione pronta oppure l'elenco dei problemi, in italiano.</summary>
public sealed record ClientProfileResult(WireGuardConfig? Config, string? Path, IReadOnlyList<string> Errors)
{
    public bool Ok => Config is not null && Errors.Count == 0;

    /// <summary>Indirizzo del PC nel tunnel senza prefisso (10.66.0.2), vuoto se il profilo non c'è.</summary>
    public string IpAddress => Config is not null && Config.Address.Contains('/') ? Config.Address[..Config.Address.IndexOf('/')] : "";

    public string ErrorText => string.Join(" ", Errors);
}

/// <summary>Lettura e controllo del profilo <c>vpn.conf</c> (formato .conf di WireGuard).</summary>
public static class ClientProfile
{
    /// <summary>Prende il primo file esistente fra i candidati. <paramref name="read"/> restituisce il testo o null se il file non c'è.</summary>
    public static ClientProfileResult Load(IEnumerable<string> candidates, Func<string, string?>? read = null)
    {
        read ??= ReadIfExists;
        var list = candidates.ToList();
        foreach (var path in list)
        {
            string? text;
            try
            {
                text = read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ClientProfileResult(null, path, [$"Il profilo {path} non si riesce a leggere: {ex.Message}"]);
            }
            if (text is not null)
                return Check(text, path);
        }
        return new ClientProfileResult(null, null,
            [$"Profilo {ClientPaths.ProfileFileName} non trovato. Cercato in: {string.Join("; ", list)}."]);
    }

    /// <summary>Analizza il testo di un .conf: struttura, campi obbligatori mancanti, poi la validazione completa.</summary>
    public static ClientProfileResult Check(string text, string? path = null)
    {
        WireGuardConfig cfg;
        try
        {
            cfg = WireGuardConfig.Parse(text);
        }
        catch (FormatException ex)
        {
            return new ClientProfileResult(null, path, [$"Il profilo non è un file .conf valido: {ex.Message}"]);
        }

        var missing = new List<string>();
        if (cfg.PrivateKey.Length == 0) missing.Add("PrivateKey (sezione [Interface])");
        if (cfg.Address.Length == 0) missing.Add("Address (sezione [Interface])");
        if (cfg.PeerPublicKey.Length == 0) missing.Add("PublicKey (sezione [Peer])");
        if (cfg.Endpoint.Length == 0) missing.Add("Endpoint (sezione [Peer])");
        if (missing.Count > 0)
            return new ClientProfileResult(null, path, [$"Nel profilo mancano dei campi: {string.Join(", ", missing)}."]);

        // rete della VPN e keepalive standard se il profilo non li indica
        if (cfg.AllowedIPs.Length == 0)
            cfg = cfg with { AllowedIPs = VpnDefaults.Network };
        if (cfg.PersistentKeepalive == 0)
            cfg = cfg with { PersistentKeepalive = VpnDefaults.Keepalive };

        var errors = cfg.Validate();
        return errors.Count == 0
            ? new ClientProfileResult(cfg, path, [])
            : new ClientProfileResult(null, path, errors);
    }

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
