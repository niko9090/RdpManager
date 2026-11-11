using System.Text.Json;
using System.Text.Json.Nodes;

namespace RdpManager.Core;

/// <summary>Il file di configurazione non è valido: <see cref="Errors"/> elenca tutti i problemi, in italiano.</summary>
public sealed class ConfigFileException(IReadOnlyList<string> errors)
    : FormatException(errors.Count == 1 ? errors[0] : string.Join(" ", errors.Select((e, i) => $"{i + 1}) {e}")))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Parte VPN del file di configurazione.</summary>
public sealed record RdpmConfigVpn
{
    /// <summary>Endpoint pubblico del server WireGuard, nome-o-IP:porta (per esempio vpn.example.com:51820).</summary>
    public string Endpoint { get; init; } = "";
    public string ServerPublicKey { get; init; } = "";
    /// <summary>Rete della VPN (informativa; è il valore predefinito di <see cref="AllowedIPs"/>).</summary>
    public string Network { get; init; } = VpnDefaults.Network;
    public string AllowedIPs { get; init; } = "";
    public string Dns { get; init; } = "";
    public int Keepalive { get; init; } = VpnDefaults.Keepalive;
    /// <summary>Se vero la chiave privata la crea il programma e al server va solo la pubblica (provisioning).</summary>
    public bool RequestKeyFromServer { get; init; }
    /// <summary>Indirizzo del PC nel tunnel (CIDR): serve solo senza richiesta automatica, o in un .conf.</summary>
    public string Address { get; init; } = "";
    /// <summary>Chiave privata: presente solo se il file è un .conf WireGuard.</summary>
    public string PrivateKey { get; init; } = "";

    public string EffectiveAllowedIPs => AllowedIPs.Length > 0 ? AllowedIPs : Network;
}

/// <summary>
/// File di configurazione unico di RdpManager (<c>.rdpmconfig</c>, JSON leggibile): tutto ciò che serve per partire senza
/// compilare niente. Lo prepara chi gestisce il server (<c>scripts/new-client-config.ps1</c>) e chi lo riceve lo carica
/// con «Carica file di configurazione…», col doppio clic o da riga di comando. Accetta anche un normale .conf WireGuard.
/// Le chiavi che cominciano con «_» sono commenti. Contiene segreti (codice di registrazione, a volte la password): non si pubblica.
/// </summary>
public sealed record RdpmConfigFile
{
    public const string Extension = ".rdpmconfig";
    public const string FormatName = "rdpmconfig";
    public const int CurrentVersion = 1;

    /// <summary>Indirizzo pubblico (HTTPS) del server di sincronizzazione.</summary>
    public string ServerUrl { get; init; } = "";
    /// <summary>Indirizzo del server dentro la VPN (per esempio http://10.66.0.1:8787).</summary>
    public string ServerUrlInside { get; init; } = "";
    public string RegistrationCode { get; init; } = "";
    /// <summary>Account da usare (facoltativo): con utente e password il PC si registra e accede da solo.</summary>
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public RdpmConfigVpn? Vpn { get; init; }
    /// <summary>Vero se il contenuto veniva da un .conf WireGuard.</summary>
    public bool FromWireGuardConf { get; init; }

    /// <summary>Il file porta segreti che è meglio non lasciare in giro (chiave privata o password).</summary>
    public bool HasSecrets => Password.Length > 0 || Vpn?.PrivateKey.Length > 0 || RegistrationCode.Length > 0;

    /// <summary>Indirizzo da usare per registrarsi: il pubblico, o quello interno se manca.</summary>
    public IEnumerable<string> ServerCandidates()
    {
        if (ServerUrl.Length > 0)
            yield return ServerUrl;
        if (ServerUrlInside.Length > 0 && ServerUrlInside != ServerUrl)
            yield return ServerUrlInside;
    }

    public static bool HasConfigExtension(string path)
        => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vero se l'argomento di avvio sembra un file di configurazione (.rdpmconfig o .conf) che esiste.</summary>
    public static bool LooksLikeConfigArgument(string? arg)
        => !string.IsNullOrWhiteSpace(arg) && !arg.StartsWith('-')
           && (HasConfigExtension(arg) || arg.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
           && File.Exists(arg);

    /// <summary>Primo argomento di riga di comando che è un file di configurazione, o null.</summary>
    public static string? FindArgument(IEnumerable<string> args)
        => args.FirstOrDefault(LooksLikeConfigArgument) is { } a ? Path.GetFullPath(a) : null;

    /// <exception cref="ConfigFileException">Il testo non è un file di configurazione valido.</exception>
    public static RdpmConfigFile Parse(string text)
    {
        if (text is null)
            throw new ConfigFileException(["Il file è vuoto."]);
        text = text.TrimStart('﻿').Trim();
        if (text.Length == 0)
            throw new ConfigFileException(["Il file è vuoto."]);
        return text.StartsWith('{') ? ParseJson(text) : ParseWireGuard(text);
    }

    /// <exception cref="FileNotFoundException">Il file non esiste.</exception>
    /// <exception cref="ConfigFileException">Il file non è valido.</exception>
    public static RdpmConfigFile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File di configurazione non trovato: " + path, path);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigFileException(["Non riesco a leggere il file: " + ex.Message]);
        }
        return Parse(text);
    }

    // ---------- JSON ----------

    private static RdpmConfigFile ParseJson(string text)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ConfigFileException(["Il file non è un JSON valido (" + ex.Message + "). Chiedi di nuovo il file a chi gestisce il server."]);
        }
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
            throw new ConfigFileException(["Il file di configurazione deve contenere un oggetto JSON."]);

        if (Str(root, "formato") is { Length: > 0 } fmt && !fmt.Equals(FormatName, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Il campo «formato» vale «{fmt}» ma deve essere «{FormatName}»: non è un file di configurazione di RdpManager.");
        if (root.TryGetProperty("versione", out var ver))
        {
            if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var v) || v < 1)
                errors.Add("Il campo «versione» deve essere un numero intero (1).");
            else if (v > CurrentVersion)
                errors.Add($"Il file è della versione {v}, più nuova di quelle che questo programma conosce ({CurrentVersion}): aggiorna RdpManager.");
        }

        var server = Obj(root, "server", errors);
        var account = Obj(root, "account", errors);
        var vpnEl = Obj(root, "vpn", errors);

        string url = ServerAddress(server, "url", "server.url", errors);
        string inside = ServerAddress(server, "urlInterno", "server.urlInterno", errors);
        var code = Str(server, "codiceRegistrazione");
        var user = Str(account, "utente");
        var pass = account is { } a && a.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        if ((user.Length == 0) != (pass.Length == 0))
            errors.Add("Nella sezione «account» servono sia «utente» sia «password» (o nessuno dei due).");

        RdpmConfigVpn? vpn = null;
        if (vpnEl is { } v2)
            vpn = ReadVpn(v2, errors);

        if (url.Length == 0 && inside.Length == 0 && vpn is null)
            errors.Add("Il file non contiene niente di utile: mancano l'indirizzo del server («server.url») e i dati della VPN («vpn»).");
        if (vpn is { RequestKeyFromServer: true } && url.Length == 0 && inside.Length == 0)
            errors.Add("Con «richiediChiaveAlServer» serve l'indirizzo del server («server.url»).");

        if (errors.Count > 0)
            throw new ConfigFileException(errors);
        return new RdpmConfigFile
        {
            ServerUrl = url, ServerUrlInside = inside, RegistrationCode = code,
            Username = user, Password = pass, Vpn = vpn,
        };
    }

    private static RdpmConfigVpn ReadVpn(JsonElement el, List<string> errors)
    {
        var endpoint = Str(el, "endpoint");
        var key = Str(el, "chiavePubblicaServer");
        var net = Str(el, "rete");
        var allowed = Str(el, "allowedIPs");
        var dns = Str(el, "dns");
        var address = Str(el, "indirizzo");
        bool auto = false;
        if (el.TryGetProperty("richiediChiaveAlServer", out var r))
        {
            if (r.ValueKind is JsonValueKind.True or JsonValueKind.False)
                auto = r.GetBoolean();
            else
                errors.Add("«vpn.richiediChiaveAlServer» deve essere true o false.");
        }
        int keepalive = VpnDefaults.Keepalive;
        if (el.TryGetProperty("keepalive", out var k))
        {
            if (k.ValueKind != JsonValueKind.Number || !k.TryGetInt32(out keepalive) || keepalive is < 0 or > 65535)
                errors.Add("«vpn.keepalive» deve essere un numero di secondi tra 0 e 65535 (di solito 25).");
        }

        if (endpoint.Length == 0)
            errors.Add("Manca «vpn.endpoint» (indirizzo pubblico del server WireGuard, per esempio vpn.example.com:51820).");
        else if (!WireGuardConfig.TryParseEndpoint(endpoint, out _, out _))
            errors.Add($"«vpn.endpoint» (\"{endpoint}\") deve essere nome-o-IP:porta, per esempio vpn.example.com:51820.");
        if (key.Length == 0)
            errors.Add("Manca «vpn.chiavePubblicaServer» (la chiave pubblica del server WireGuard, 44 caratteri).");
        else if (!WireGuardKeys.IsValidKey(key))
            errors.Add("«vpn.chiavePubblicaServer» non è una chiave WireGuard valida (32 byte in base64, 44 caratteri che finiscono con «=»).");
        if (net.Length == 0)
            net = VpnDefaults.Network;
        else if (!WireGuardConfig.TryParseCidr(net, out _, out _))
            errors.Add($"«vpn.rete» (\"{net}\") deve essere una rete IPv4 con prefisso, per esempio 10.66.0.0/24.");
        if (allowed.Length > 0 && !CidrList(allowed))
            errors.Add($"«vpn.allowedIPs» (\"{allowed}\") deve contenere reti IPv4 con prefisso separate da virgole, per esempio 10.66.0.0/24.");
        if (dns.Length > 0 && !dns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(d => System.Net.IPAddress.TryParse(d, out _)))
            errors.Add("«vpn.dns» deve essere uno o più indirizzi IP separati da virgole.");
        if (address.Length > 0 && !WireGuardConfig.TryParseCidr(address, out _, out _))
            errors.Add($"«vpn.indirizzo» (\"{address}\") deve essere IPv4 con prefisso, per esempio 10.66.0.2/32.");
        return new RdpmConfigVpn
        {
            Endpoint = endpoint, ServerPublicKey = key, Network = net, AllowedIPs = allowed, Dns = dns,
            Keepalive = keepalive, RequestKeyFromServer = auto, Address = address,
        };
    }

    private static bool CidrList(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(p => WireGuardConfig.TryParseCidr(p, out _, out _));
    }

    private static string ServerAddress(JsonElement? parent, string name, string label, List<string> errors)
    {
        var raw = Str(parent, name);
        if (raw.Length == 0)
            return "";
        try { return SyncHttp.NormalizeUrl(raw); }
        catch (ArgumentException)
        {
            errors.Add($"«{label}» (\"{raw}\") non è un indirizzo valido: scrivi per esempio https://vpn.example.com:8443.");
            return "";
        }
    }

    private static string Str(JsonElement? el, string name)
        => el is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private static JsonElement? Obj(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"«{name}» deve essere un oggetto JSON ({{ ... }}).");
            return null;
        }
        return v;
    }

    // ---------- .conf WireGuard ----------

    private static RdpmConfigFile ParseWireGuard(string text)
    {
        if (!text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            throw new ConfigFileException(["Il file non è né un file di configurazione RdpManager (.rdpmconfig, JSON) né un .conf WireGuard."]);
        WireGuardConfig wg;
        try { wg = WireGuardConfig.Parse(text); }
        catch (FormatException ex)
        {
            throw new ConfigFileException(["Il file .conf di WireGuard non è leggibile: " + ex.Message]);
        }
        var problems = wg.Validate().ToList();
        if (problems.Count > 0)
            throw new ConfigFileException(problems);
        return new RdpmConfigFile
        {
            FromWireGuardConf = true,
            Vpn = new RdpmConfigVpn
            {
                Endpoint = wg.Endpoint, ServerPublicKey = wg.PeerPublicKey, Network = VpnDefaults.Network,
                AllowedIPs = wg.AllowedIPs, Dns = wg.Dns, Keepalive = wg.PersistentKeepalive,
                Address = wg.Address, PrivateKey = wg.PrivateKey, RequestKeyFromServer = false,
            },
        };
    }

    // ---------- scrittura ----------

    /// <summary>Testo JSON del file (leggibile, con commenti in chiavi «_»), usato dal generatore e dalle prove.</summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["_commento"] = "File di configurazione di RdpManager: caricalo con «Carica file di configurazione…» o con un doppio clic. Contiene segreti: non pubblicarlo.",
            ["formato"] = FormatName,
            ["versione"] = CurrentVersion,
        };
        var server = new JsonObject();
        if (ServerUrl.Length > 0) server["url"] = ServerUrl;
        if (ServerUrlInside.Length > 0) server["urlInterno"] = ServerUrlInside;
        if (RegistrationCode.Length > 0) server["codiceRegistrazione"] = RegistrationCode;
        if (server.Count > 0) root["server"] = server;
        if (Username.Length > 0)
            root["account"] = new JsonObject { ["utente"] = Username, ["password"] = Password };
        if (Vpn is { } v)
        {
            var vpn = new JsonObject
            {
                ["endpoint"] = v.Endpoint,
                ["chiavePubblicaServer"] = v.ServerPublicKey,
                ["rete"] = v.Network,
            };
            if (v.AllowedIPs.Length > 0) vpn["allowedIPs"] = v.AllowedIPs;
            if (v.Dns.Length > 0) vpn["dns"] = v.Dns;
            vpn["keepalive"] = v.Keepalive;
            vpn["richiediChiaveAlServer"] = v.RequestKeyFromServer;
            if (v.Address.Length > 0) vpn["indirizzo"] = v.Address;
            root["vpn"] = vpn;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
               .Replace("\r\n", "\n") + "\n";
    }
}
