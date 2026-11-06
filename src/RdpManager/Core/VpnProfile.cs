using System.Text.Json;

namespace RdpManager.Core;

/// <summary>
/// Dati della VPN di questo PC, salvati in %APPDATA%\RdpManager\vpn.json. La chiave privata è cifrata con DPAPI
/// (<see cref="PasswordProtector"/>, ambito utente Windows) come le password delle connessioni: nel file non c'è mai in chiaro.
/// </summary>
public sealed class VpnProfile
{
    public string PrivateKeyProtected { get; set; } = "";
    public string Address { get; set; } = "";
    public string Endpoint { get; set; } = VpnDefaults.DefaultEndpoint;
    public string ServerPublicKey { get; set; } = "";
    public string Dns { get; set; } = "";
    public string PcName { get; set; } = "";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager", "vpn.json");

    public bool HasKey => PrivateKeyProtected.Length > 0;

    /// <summary>Crea una nuova coppia di chiavi e la salva cifrata; restituisce la chiave pubblica.</summary>
    public string GenerateKeys()
    {
        var (priv, pub) = WireGuardKeys.Generate();
        PrivateKeyProtected = PasswordProtector.Protect(priv);
        return pub;
    }

    /// <summary>Chiave pubblica del PC, ricavata dalla privata salvata (null se manca o non si decifra).</summary>
    public string? PublicKey
        => PasswordProtector.TryUnprotect(PrivateKeyProtected, out var priv) && priv is not null && WireGuardKeys.IsValidKey(priv)
            ? WireGuardKeys.PublicKeyFromPrivate(priv)
            : null;

    /// <summary>Configurazione completa (con la chiave privata in chiaro: da non salvare su disco se non per l'avvio del tunnel).</summary>
    public WireGuardConfig ToConfig()
    {
        if (!PasswordProtector.TryUnprotect(PrivateKeyProtected, out var priv) || priv is null)
            throw new InvalidOperationException("Chiave privata assente o non decifrabile: crea di nuovo le chiavi.");
        return new WireGuardConfig
        {
            PrivateKey = priv,
            Address = Address,
            Dns = Dns,
            PeerPublicKey = ServerPublicKey,
            Endpoint = Endpoint,
        };
    }

    /// <summary>Frammento [Peer] per il server di questo PC.</summary>
    public string ServerPeerFragment()
        => WireGuardConfig.BuildServerPeerFragment(PublicKey ?? throw new InvalidOperationException("Nessuna chiave."), Address, PcName);

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static VpnProfile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new VpnProfile();
        try
        {
            return JsonSerializer.Deserialize<VpnProfile>(File.ReadAllText(path)) ?? new VpnProfile();
        }
        catch (JsonException)
        {
            return new VpnProfile();
        }
    }
}
