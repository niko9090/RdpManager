using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Ruolo scelto per questa installazione.</summary>
public enum AppRole
{
    /// <summary>Client RDP con la VPN: si apre la finestra con le schede (predefinito).</summary>
    DesktopAndVpn,
    /// <summary>Gestore della VPN: all'avvio si apre la Plancia VPN in primo piano.</summary>
    VpnManager,
}

/// <summary>
/// Il ruolo, salvato in un file a parte (role.json nella cartella di configurazione) e non in connections.json, così
/// l'installer di Windows può scriverlo senza toccare l'archivio. File assente, vuoto o rovinato = predefinito.
/// Il file contiene {"role":"VpnManager"} oppure {"role":"DesktopAndVpn"}.
/// </summary>
public static class AppRoleStore
{
    public const string FileName = "role.json";

    public static AppRole Default => AppRole.DesktopAndVpn;

    public static string DefaultPath => Path.Combine(AppPaths.ConfigFolder, FileName);

    /// <summary>Titolo mostrato all'utente.</summary>
    public static string Describe(AppRole role) => role == AppRole.VpnManager ? "Gestore marshmelloVPN" : "Desktop remoto + marshmelloVPN";

    /// <summary>Vero se il file esiste e contiene un ruolo valido (a Linux serve a sapere se chiedere la scelta).</summary>
    public static bool IsChosen(string? path = null) => TryRead(path ?? DefaultPath, out _);

    public static AppRole Load(string? path = null) =>
        TryRead(path ?? DefaultPath, out var role) ? role : Default;

    /// <summary>Scrive il ruolo; false se il disco non lo permette (l'avvio non deve mai fallire per questo).</summary>
    public static bool Save(AppRole role, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dto { Role = role.ToString() }));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Interpreta il testo del file; stringhe come «vpn», «VpnManager» o «gestore-vpn» valgono il gestore.</summary>
    public static bool TryParse(string? text, out AppRole role)
    {
        role = Default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(text);
            return TryParseName(dto?.Role, out role);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Nome del ruolo (anche abbreviato, per i parametri dell'installer): false se non lo riconosce.</summary>
    public static bool TryParseName(string? name, out AppRole role)
    {
        role = Default;
        switch (name?.Trim().ToLowerInvariant())
        {
            case "vpnmanager" or "vpn" or "gestore-vpn":
                role = AppRole.VpnManager;
                return true;
            case "desktopandvpn" or "desktop" or "rdp":
                role = AppRole.DesktopAndVpn;
                return true;
            default:
                return false;
        }
    }

    private static bool TryRead(string path, out AppRole role)
    {
        role = Default;
        try
        {
            return File.Exists(path) && TryParse(File.ReadAllText(path), out role);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Dto
    {
        [System.Text.Json.Serialization.JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}
