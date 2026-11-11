using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Opzioni dell'import (le prove puntano a cartelle temporanee).</summary>
public sealed record ConfigImportOptions
{
    /// <summary>Profilo VPN (null = quello predefinito).</summary>
    public string? ProfilePath { get; init; }
    /// <summary>Cartella dove salvare <c>rdpmanager-config.json</c> (null = cartella dei dati dell'app).</summary>
    public string? DataFolder { get; init; }
    /// <summary>Nome di questo PC nella VPN (null = nome del computer).</summary>
    public string? PcName { get; init; }
    /// <summary>
    /// Cancellare il file importato dopo l'uso (contiene segreti). Null = solo se sta nella cartella temporanea: un file
    /// scelto dall'utente non si cancella mai senza che lo abbia chiesto.
    /// </summary>
    public bool? DeleteSource { get; init; }
}

/// <summary>Esito dell'import: <see cref="Lines"/> è il riepilogo da mostrare («Fatto» se tutto è andato a posto).</summary>
public sealed record ConfigImportReport(bool Complete, IReadOnlyList<string> Lines, bool VpnConnected, bool SourceDeleted, string? SourceWarning)
{
    public string Title => Complete ? "Fatto" : "Da completare";

    public string Text => Title + "\n\n" + string.Join("\n", Lines.Select(l => "• " + l))
                          + (SourceWarning is { Length: > 0 } w ? "\n\n" + w : "");
}

/// <summary>
/// Import del file di configurazione: salva indirizzo del server e codice, scrive il profilo VPN, registra il PC e accende
/// la VPN con i meccanismi della piattaforma (<c>connect</c>), senza chiedere campi. Non parla con servizi veri da sé:
/// rete e tunnel passano da <see cref="SyncService"/> e dal delegato.
/// </summary>
public static class ConfigFileImporter
{
    /// <summary>Accende il tunnel; restituisce null se è acceso (o già acceso), altrimenti il motivo.</summary>
    public delegate Task<string?> ConnectTunnel(WireGuardConfig config, CancellationToken ct);

    /// <summary>Legge il file, lo importa e (se va fatto) lo cancella.</summary>
    /// <exception cref="ConfigFileException">Il file non è valido.</exception>
    /// <exception cref="FileNotFoundException">Il file non esiste.</exception>
    public static async Task<ConfigImportReport> ImportFileAsync(string path, SyncService sync, ConnectTunnel? connect,
        ConfigImportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ConfigImportOptions();
        var file = RdpmConfigFile.Load(path);
        var report = await ImportAsync(file, sync, connect, options, ct);
        if (!file.HasSecrets)
            return report;
        bool delete = options.DeleteSource ?? IsInTempFolder(path);
        if (delete)
        {
            try
            {
                File.Delete(path);
                return report with { SourceDeleted = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return report with { SourceWarning = "Non sono riuscito a cancellare il file di configurazione (" + ex.Message + "): cancellalo tu, contiene dati riservati." };
            }
        }
        return report with
        {
            SourceWarning = "Il file di configurazione contiene dati riservati (codice, chiavi o password): ora che è importato, cancellalo o tienilo al sicuro.",
        };
    }

    private static bool IsInTempFolder(string path)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static async Task<ConfigImportReport> ImportAsync(RdpmConfigFile file, SyncService sync, ConnectTunnel? connect,
        ConfigImportOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ConfigImportOptions();
        var lines = new List<string>();
        bool complete = true, connected = false;

        // 1. Server e codice di registrazione: li ritrova il dialogo dell'account (stesso file di sempre, rdpmanager-config.json).
        if (file.ServerUrl.Length > 0 || file.ServerUrlInside.Length > 0 || file.RegistrationCode.Length > 0)
        {
            try
            {
                SaveClientConfig(file, options.DataFolder ?? ClientConfig.DataFolder);
                lines.Add("Indirizzo del server e codice di registrazione salvati.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                lines.Add("Non sono riuscito a salvare l'indirizzo del server: " + ex.Message);
            }
        }

        // 2. Profilo VPN.
        VpnProfile? profile = null;
        if (file.Vpn is { } vpn)
        {
            try
            {
                profile = WriteProfile(file, vpn, options);
                lines.Add(vpn.PrivateKey.Length > 0
                    ? "Profilo VPN salvato (la chiave privata è custodita cifrata, non resta in chiaro)."
                    : "Profilo VPN salvato (server " + vpn.Endpoint + ").");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                complete = false;
                lines.Add("Non sono riuscito a salvare il profilo VPN: " + ex.Message);
            }
        }

        // 3. Account, solo se il file porta utente e password.
        if (file.Username.Length > 0 && file.Password.Length > 0)
        {
            var (ok, line) = await SignInAsync(file, sync, ct);
            lines.Add(line);
            complete &= ok;
        }
        else if (file.ServerCandidates().Any() && !sync.SignedIn)
            lines.Add("Per sincronizzare le connessioni accedi (o registrati) dalla finestra «Account»: l'indirizzo del server e il codice sono già pronti.");

        // 4. Registrazione del PC nella VPN (la chiave privata la crea questo programma).
        if (profile is not null && file.Vpn is { } v)
        {
            if (v.RequestKeyFromServer && VpnProvisioning.NeedsProvisioning(profile))
            {
                if (!sync.SignedIn)
                {
                    complete = false;
                    lines.Add("La VPN si completa appena accedi all'account: il PC si registra sul server e la VPN parte da sola.");
                }
                else
                {
                    try
                    {
                        profile = await VpnProvisioning.EnsureAsync(sync, options.ProfilePath, options.PcName, ct) ?? profile;
                        lines.Add("PC registrato sul server (indirizzo VPN " + profile.Address + ", la chiave privata è rimasta su questo PC).");
                    }
                    catch (SyncServerException ex) when (ex.Code == "vpn_disabled")
                    {
                        complete = false;
                        lines.Add("Questo server non offre la VPN automatica: chiedi a chi lo gestisce un file con la chiave già pronta.");
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        complete = false;
                        lines.Add("Registrazione del PC nella VPN non riuscita: " + Explain(ex));
                    }
                }
            }
            else if (v.RequestKeyFromServer)
                lines.Add("Questo PC aveva già il suo profilo VPN: l'ho tenuto.");
            else if (!profile.HasKey)
            {
                try
                {
                    profile.GenerateKeys();
                    profile.Save(options.ProfilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    lines.Add("Non sono riuscito a creare le chiavi: " + ex.Message);
                }
                complete = false;
                lines.Add("Il file non chiede la chiave al server: l'amministratore deve aggiungere questo PC al server (il frammento è nella finestra VPN, «Avanzate»).");
            }
        }

        // 5. Accensione del tunnel.
        if (profile is not null && connect is not null && !VpnProvisioning.NeedsProvisioning(profile))
        {
            try
            {
                var failure = await connect(profile.ToConfig(), ct);
                if (failure is null)
                {
                    connected = true;
                    lines.Add("VPN accesa (indirizzo " + profile.Address + ").");
                }
                else
                {
                    complete = false;
                    lines.Add("VPN configurata ma non accesa: " + failure);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                complete = false;
                lines.Add("VPN configurata ma non accesa: " + ex.Message);
            }
        }
        else if (profile is not null && connect is null)
            lines.Add("VPN configurata: accendila dalla finestra VPN.");

        return new ConfigImportReport(complete, lines, connected, false, null);
    }

    private static VpnProfile WriteProfile(RdpmConfigFile file, RdpmConfigVpn vpn, ConfigImportOptions options)
    {
        var profile = VpnProfile.Load(options.ProfilePath);
        bool serverChanged = profile.ServerPublicKey.Length > 0
                             && (profile.ServerPublicKey != vpn.ServerPublicKey || profile.Endpoint != vpn.Endpoint);
        profile.Endpoint = vpn.Endpoint;
        profile.ServerPublicKey = vpn.ServerPublicKey;
        profile.AllowedIPs = vpn.EffectiveAllowedIPs;
        profile.Dns = vpn.Dns;
        profile.PersistentKeepalive = vpn.Keepalive;
        if (vpn.PrivateKey.Length > 0)
            profile.PrivateKeyProtected = PasswordProtector.Protect(vpn.PrivateKey);
        if (vpn.Address.Length > 0)
            profile.Address = vpn.Address;
        else if (serverChanged || (vpn.RequestKeyFromServer && !profile.HasKey))
            profile.Address = ""; // altro server, o un indirizzo segnaposto senza chiave: lo assegna il server alla registrazione
        if (profile.PcName.Length == 0)
            profile.PcName = VpnProvisioning.SafeName(options.PcName ?? Environment.MachineName);
        if (profile.Address.Length == 0 && !vpn.RequestKeyFromServer)
            profile.Address = WireGuardConfig.NextFreeAddress([]) ?? "";
        profile.Save(options.ProfilePath);
        return profile;
    }

    private static async Task<(bool Ok, string Line)> SignInAsync(RdpmConfigFile file, SyncService sync, CancellationToken ct)
    {
        var candidates = file.ServerCandidates().ToList();
        if (candidates.Count == 0)
            return (false, "Il file ha utente e password ma non l'indirizzo del server: non posso accedere.");
        if (sync.SignedIn && SyncCrypto.NormalizeUser(sync.State.Username) == SyncCrypto.NormalizeUser(file.Username)
            && candidates.Contains(sync.State.ServerUrl))
            return (true, "Account «" + sync.State.Username + "» già collegato.");
        Exception? last = null;
        foreach (var url in candidates)
        {
            try
            {
                try
                {
                    await sync.RegisterAsync(url, file.Username, file.Password, file.RegistrationCode, ct);
                }
                catch (SyncServerException ex) when (ex.Code is "account_exists" or "registration_disabled")
                {
                    // l'account c'è già (o lo crea solo l'amministratore): si prova ad accedere
                }
                await sync.SignInAsync(url, file.Username, file.Password, ct);
                return (true, "Accesso eseguito come «" + file.Username + "».");
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                last = ex;
                if (ex is not SyncUnreachableException)
                    break; // il server ha risposto: l'altro indirizzo non cambierebbe niente
            }
        }
        return (false, "Accesso all'account non riuscito: " + Explain(last!));
    }

    private static bool IsExpected(Exception ex) =>
        ex is SyncServerException or SyncUnreachableException or ArgumentException or InvalidOperationException
            or FormatException or IOException or UnauthorizedAccessException;

    public static string Explain(Exception ex) => ex switch
    {
        SyncUnreachableException { ServerDown: true } => "il server è spento o si sta riavviando, riprova fra qualche minuto.",
        SyncUnreachableException => "il server non risponde (controlla la connessione a Internet).",
        SyncServerException { Code: "invalid_credentials" } => "utente o password sbagliati.",
        SyncServerException { Code: "invalid_registration_code" } => "il codice di registrazione del file non è accettato dal server: chiedi un file nuovo.",
        SyncServerException s => s.Message,
        _ => ex.Message,
    };

    private static void SaveClientConfig(RdpmConfigFile file, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ClientConfig.FileName);
        var data = new Dictionary<string, string>();
        if (file.ServerCandidates().FirstOrDefault() is { } server)
            data["server"] = server;
        if (file.RegistrationCode.Length > 0)
            data["codiceRegistrazione"] = file.RegistrationCode;
        if (file.Vpn is { Endpoint.Length: > 0 } v)
            data["vpnEndpoint"] = v.Endpoint;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
