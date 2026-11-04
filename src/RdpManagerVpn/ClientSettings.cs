using System.Text.Json;

namespace RdpManagerVpn;

/// <summary>Impostazioni minime del client, salvate in un json semplice (impostazioni.json).</summary>
public sealed record ClientSettings
{
    /// <summary>Il tunnel si accende da solo all'avvio di Windows e l'icona parte all'accesso.</summary>
    public bool AvviaConWindows { get; init; } = true;

    /// <summary>Se il tunnel cade (o il server non risponde più) il servizio lo ricrea da solo.</summary>
    public bool RiconnettiDaSolo { get; init; } = true;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>File assente, vuoto o rovinato: valori predefiniti (le impostazioni non devono mai impedire l'avvio).</summary>
    public static ClientSettings Load(string path, Func<string, string?>? read = null)
    {
        try
        {
            var text = read is null ? (File.Exists(path) ? File.ReadAllText(path) : null) : read(path);
            return string.IsNullOrWhiteSpace(text) ? new ClientSettings() : JsonSerializer.Deserialize<ClientSettings>(text, Options) ?? new ClientSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ClientSettings();
        }
    }

    /// <summary>Scrive il file (via file temporaneo, così non resta mai a metà).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
