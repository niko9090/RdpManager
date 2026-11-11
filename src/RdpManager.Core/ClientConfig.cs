using System.Text.Json;

namespace RdpManager.Core;

/// <summary>
/// File di configurazione del client (<c>rdpmanager-config.json</c>): indirizzo del server, codice di registrazione ed
/// eventuale endpoint VPN. Lo prepara chi gestisce il server; chi installa lo importa dal dialogo dell'account, oppure
/// l'app lo trova da sola accanto all'eseguibile o nella cartella dei dati. Le chiavi che cominciano con «_» sono commenti.
/// </summary>
public sealed record ClientConfig(string ServerUrl, string RegistrationCode, string VpnEndpoint)
{
    public const string FileName = "rdpmanager-config.json";

    /// <summary>Cartella dei dati dell'app (<c>%APPDATA%\RdpManager</c>).</summary>
    public static string DataFolder => AppPaths.ConfigFolder;

    /// <exception cref="FormatException">Il testo non è un file di configurazione valido.</exception>
    public static ClientConfig Parse(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException("Il file non è un JSON valido: " + ex.Message, ex);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Il file di configurazione deve contenere un oggetto JSON.");

        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

        var server = Str("server");
        if (server.Length > 0)
        {
            try { server = SyncHttp.NormalizeUrl(server); }
            catch (ArgumentException ex) { throw new FormatException("Indirizzo del server non valido: " + ex.Message, ex); }
        }
        var cfg = new ClientConfig(server, Str("codiceRegistrazione"), Str("vpnEndpoint"));
        if (cfg.ServerUrl.Length == 0 && cfg.RegistrationCode.Length == 0 && cfg.VpnEndpoint.Length == 0)
            throw new FormatException("Il file non contiene nessun valore utile (server, codiceRegistrazione, vpnEndpoint).");
        return cfg;
    }

    /// <exception cref="FileNotFoundException">Il file non esiste.</exception>
    /// <exception cref="FormatException">Il file non è valido.</exception>
    public static ClientConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File di configurazione non trovato: " + path, path);
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex) { throw new FormatException("Non riesco a leggere il file: " + ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new FormatException("Non riesco a leggere il file: " + ex.Message, ex); }
        return Parse(text);
    }

    /// <summary>Cerca il file accanto all'eseguibile e poi nella cartella dei dati; null se non c'è o non è valido.</summary>
    public static ClientConfig? FindDefault(string? exeFolder = null, string? dataFolder = null)
    {
        foreach (var folder in new[] { exeFolder ?? AppContext.BaseDirectory, dataFolder ?? DataFolder })
        {
            if (folder.Length == 0)
                continue; // cartella esclusa (per esempio «solo i dati dell'app»)
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
                continue;
            try { return Load(path); }
            catch (Exception ex) when (ex is FormatException or FileNotFoundException)
            {
                AppLog.Default?.Warning($"Configurazione ignorata ({path}): {ex.Message}");
            }
        }
        return null;
    }
}
