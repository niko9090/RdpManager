namespace RdpManager.Core;

/// <summary>Esito di una rotazione o ri-registrazione della chiave: <see cref="Message"/> è già pronto per l'utente (mai chiavi nel testo).</summary>
public sealed record VpnKeyResult(bool Ok, string Message, bool RegisteredOnServer = false);

/// <summary>Registra la chiave pubblica di questo PC sul server: <c>(nomePc, chiavePubblica)</c> → profilo assegnato.</summary>
public delegate Task<VpnGrant> VpnRegistrar(string pcName, string publicKey, CancellationToken ct);

/// <summary>
/// Chiavi VPN e server d'accordo. Se il PC è già registrato (ha indirizzo 10.66.0.N e chiave del server) il server conosce la
/// vecchia chiave pubblica e scarta i pacchetti della nuova: ogni cambio di chiave va quindi mandato al server (stesso nome del PC =
/// stesso indirizzo, il server sostituisce la chiave). Le chiavi locali cambiano solo se il server ha accettato.
/// </summary>
public static class VpnKeySync
{
    /// <summary>Il PC risulta già registrato presso il server (profilo completo con indirizzo assegnato).</summary>
    public static bool IsRegistered(VpnProfile p)
        => p.HasKey && p.ServerPublicKey.Length > 0 && WireGuardConfig.TryParseCidr(p.Address, out _, out _);

    private static string NameOf(VpnProfile p)
        => p.PcName.Length > 0 ? p.PcName : VpnProvisioning.SafeName(Environment.MachineName);

    /// <summary>
    /// «Crea chiavi»: genera la nuova coppia e, se il PC è già registrato e c'è un modo di parlare col server
    /// (<paramref name="register"/> non nullo), la registra. Se il server rifiuta o non risponde il profilo in memoria torna
    /// com'era e nulla viene salvato; in caso di successo il profilo è salvato in <paramref name="profilePath"/>.
    /// </summary>
    public static async Task<VpnKeyResult> RotateAsync(VpnProfile profile, string? profilePath, VpnRegistrar? register, CancellationToken ct = default)
    {
        if (register is null || !IsRegistered(profile))
        {
            profile.GenerateKeys();
            profile.Save(profilePath);
            return new VpnKeyResult(true, "Chiavi create. Aggiungi il nuovo frammento sul server per collegare questo PC.");
        }

        var oldKey = profile.PrivateKeyProtected;
        var oldName = profile.PcName;
        var done = false;
        try
        {
            var pub = profile.GenerateKeys();
            profile.PcName = NameOf(profile);
            var grant = await register(profile.PcName, pub, ct);
            VpnProvisioning.Apply(profile, grant);
            profile.Save(profilePath);
            done = true;
            return new VpnKeyResult(true, "Chiavi nuove create e registrate sul server (indirizzo " + profile.Address + ").", true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new VpnKeyResult(false, "Il server non ha accettato la nuova chiave: " + Reason(ex)
                + " Le chiavi di questo PC non sono state cambiate: riprova quando il server risponde.");
        }
        finally
        {
            if (!done)
            {
                profile.PrivateKeyProtected = oldKey;
                profile.PcName = oldName;
            }
        }
    }

    /// <summary>Rimanda al server la chiave pubblica attuale (nessuna chiave nuova): rimedio al tunnel «su» senza handshake.</summary>
    public static async Task<VpnKeyResult> ReRegisterAsync(VpnProfile profile, VpnRegistrar register, CancellationToken ct = default)
    {
        if (!IsRegistered(profile) || profile.PublicKey is not { } pub)
            return new VpnKeyResult(false, "Profilo VPN incompleto: niente da registrare.");
        try
        {
            var grant = await register(NameOf(profile), pub, ct);
            if (grant.Address != profile.Address || grant.ServerPublicKey != profile.ServerPublicKey)
                return new VpnKeyResult(false, "Il server ha assegnato dati diversi da quelli del profilo: lo lascio com'è.", true);
            return new VpnKeyResult(true, "Chiave pubblica di questo PC registrata di nuovo sul server.", true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new VpnKeyResult(false, "Registrazione della chiave sul server non riuscita: " + Reason(ex));
        }
    }

    private static bool IsExpected(Exception ex)
        => ex is SyncServerException or SyncUnreachableException or FormatException or IOException or InvalidOperationException;

    private static string Reason(Exception ex)
    {
        var m = ex.Message.Trim();
        return m.Length == 0 ? "errore sconosciuto." : m.EndsWith('.') ? m : m + ".";
    }
}

/// <summary>
/// Quando riprovare la registrazione automatica: solo con il tunnel acceso ma senza handshake, e al massimo
/// <see cref="MaxAttempts"/> volte (una) finché il programma resta aperto; un handshake riuscito non azzera il conto.
/// </summary>
public sealed class VpnKeyWatchdog(int maxAttempts = 1)
{
    private int _attempts;
    public int MaxAttempts { get; } = maxAttempts;
    public int Attempts => _attempts;

    /// <summary>Vero (e conta il tentativo) se il server non risponde e ci sono ancora tentativi.</summary>
    public bool TryBegin(bool serverUnreachable)
    {
        if (!serverUnreachable || _attempts >= MaxAttempts)
            return false;
        _attempts++;
        return true;
    }
}
