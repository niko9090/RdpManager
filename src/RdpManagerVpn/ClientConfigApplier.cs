using System.Security.Cryptography;
using RdpManager.Core;

namespace RdpManagerVpn;

/// <summary>Mette il profilo WireGuard di questo PC dove lo legge il servizio e accende il tunnel (nella versione vera: vpn.conf protetto + servizio).</summary>
internal interface IClientProfileInstaller
{
    /// <summary>Restituisce null se il profilo è installato e il tunnel acceso, altrimenti il motivo.</summary>
    string? Install(WireGuardConfig config);
}

/// <summary>
/// Applica un file <c>.rdpmconfig</c> su un PC dove c'è solo RdpManager VPN: stesso codice di RdpManager
/// (<see cref="ConfigFileImporter"/>: chiave creata qui, PC registrato sul server, profilo scritto, tunnel acceso).
/// Chiavi e stato dell'account stanno in una cartella privata dell'utente (<c>workFolder</c>), mai nella cartella di RdpManager.
/// </summary>
internal static class ClientConfigApplier
{
    public const string AccountFileName = "account.sync";
    public const string ProfileFileName = "vpn.json";

    /// <summary>Cartella privata dell'utente per chiavi e account: %LOCALAPPDATA%\RdpManagerVPN.</summary>
    public static string DefaultWorkFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManagerVPN");

    /// <summary>Legge il file e lo applica. Se il file non è valido non si modifica niente.</summary>
    /// <exception cref="ConfigFileException">Il file non è valido (l'elenco dei problemi è in <c>Errors</c>).</exception>
    /// <exception cref="FileNotFoundException">Il file non esiste.</exception>
    public static Task<ConfigImportReport> ApplyFileAsync(string path, string workFolder, IClientProfileInstaller installer,
        string? pcName = null, CancellationToken ct = default)
        => ApplyAsync(RdpmConfigFile.Load(path), workFolder, installer, pcName, ct);

    public static async Task<ConfigImportReport> ApplyAsync(RdpmConfigFile file, string workFolder, IClientProfileInstaller installer,
        string? pcName = null, CancellationToken ct = default)
    {
        if (file.Vpn is null)
            throw new ConfigFileException(["Il file non contiene la parte VPN («vpn»): serve un file preparato per la VPN (new-client-config.ps1)."]);

        Directory.CreateDirectory(workFolder);
        using var sync = new SyncService(Path.Combine(workFolder, AccountFileName));
        file = WithDeviceAccount(file, sync, pcName ?? Environment.MachineName);
        var options = new ConfigImportOptions
        {
            ProfilePath = Path.Combine(workFolder, ProfileFileName),
            DataFolder = Path.Combine(workFolder, "dati"),
            PcName = pcName,
            DeleteSource = false, // il file lo cancella chi l'ha messo lì: qui si avvisa soltanto
        };
        var report = await ConfigFileImporter.ImportAsync(file, sync,
            (config, _) => Task.FromResult(installer.Install(config)), options, ct);
        return file.HasSecrets
            ? report with { SourceWarning = "Il file di configurazione contiene dati riservati (codice, chiavi o password): ora che è applicato, cancellalo o tienilo al sicuro." }
            : report;
    }

    /// <summary>
    /// Un file con il solo codice di registrazione non ha un account: il PC ne crea uno suo («pc-nome») con una password casuale,
    /// custodita cifrata nello stato locale, e lo usa per farsi assegnare l'indirizzo VPN. Se è già collegato lo riusa.
    /// </summary>
    internal static RdpmConfigFile WithDeviceAccount(RdpmConfigFile file, SyncService sync, string pcName)
    {
        if (file.Username.Length > 0 || file.Vpn is not { RequestKeyFromServer: true } || file.RegistrationCode.Length == 0
            || !file.ServerCandidates().Any())
            return file;
        if (sync.SignedIn)
            return file with { Username = sync.State.Username, Password = "gia-collegato" };
        var name = "pc-" + VpnProvisioning.SafeName(pcName).Replace(' ', '-').ToLowerInvariant();
        return file with { Username = name, Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() };
    }
}
