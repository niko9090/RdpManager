using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RdpmSync;

/// <summary>
/// Comando <c>export-client-config</c>: scrive il file di configurazione dei client (<c>.rdpmconfig</c>) con i valori veri
/// del server (endpoint WireGuard, chiave pubblica, codice di registrazione, DNS). Il formato è lo stesso che legge
/// l'app (RdpManager.Core, <c>RdpmConfigFile</c>): le due parti si accordano solo sul JSON, il server non dipende dal Core.
/// </summary>
public static class ClientConfigExport
{
    public const string Network = VpnEndpoints.Network;

    /// <summary>Chiave pubblica del server WireGuard: da RDPM_VPN_SERVER_PUBLIC_KEY o da server.pub accanto a wg0.conf.</summary>
    public static string ServerKey(SyncOptions options)
    {
        if (options.VpnServerPublicKey.Length > 0)
            return options.VpnServerPublicKey;
        if (options.VpnConfigPath.Length > 0)
        {
            var pub = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.VpnConfigPath))!, "server.pub");
            if (File.Exists(pub))
                return File.ReadAllText(pub).Trim();
        }
        return "";
    }

    /// <summary>Testo del file, o gli errori per cui non si può produrre (manca l'endpoint, la chiave…).</summary>
    public static (string? Json, IReadOnlyList<string> Errors) Build(SyncOptions options, string publicUrl, string? insideUrl,
        string? username, string? password, bool requestKeyFromServer = true)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("Indica l'indirizzo pubblico del server con --url (per esempio https://vpn.example.com:8443).");
        if (options.VpnEndpoint.Length == 0)
            errors.Add("Manca RDPM_VPN_ENDPOINT (endpoint pubblico WireGuard, nome:porta).");
        var key = ServerKey(options);
        if (!VpnEndpoints.ValidKey(key))
            errors.Add("Manca la chiave pubblica del server (RDPM_VPN_SERVER_PUBLIC_KEY o server.pub accanto a wg0.conf).");
        if ((username is null) != (password is null))
            errors.Add("Servono insieme --utente e --password (o nessuno dei due).");
        if (errors.Count > 0)
            return (null, errors);

        var server = new JsonObject { ["url"] = publicUrl.TrimEnd('/') };
        server["urlInterno"] = (insideUrl is { Length: > 0 } ? insideUrl : $"http://10.66.0.1:{options.Port}").TrimEnd('/');
        if (options.RegistrationCode.Length > 0)
            server["codiceRegistrazione"] = options.RegistrationCode;
        var vpn = new JsonObject
        {
            ["endpoint"] = options.VpnEndpoint,
            ["chiavePubblicaServer"] = key,
            ["rete"] = Network,
            ["allowedIPs"] = Network,
        };
        if (options.VpnDns.Length > 0)
            vpn["dns"] = options.VpnDns;
        vpn["keepalive"] = VpnEndpoints.Keepalive;
        vpn["richiediChiaveAlServer"] = requestKeyFromServer;
        var root = new JsonObject
        {
            ["_commento"] = "File di configurazione di RdpManager: caricalo con «Carica file di configurazione…» o con un doppio clic. Contiene segreti: non pubblicarlo.",
            ["formato"] = "rdpmconfig",
            ["versione"] = 1,
            ["server"] = server,
        };
        if (username is not null)
            root["account"] = new JsonObject { ["utente"] = username, ["password"] = password };
        root["vpn"] = vpn;
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n") + "\n";
        return (json, errors);
    }
}
