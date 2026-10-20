using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>
/// Credenziali salvate una volta e richiamate da più connessioni (o dal loro gateway).
/// La password segue le stesse regole di <see cref="RdpConnection.Password"/>: in chiaro solo in memoria,
/// cifrata con DPAPI in connections.json, esclusa o cifrata con la password del file nei .rdpm.
/// </summary>
public sealed class CredentialProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";

    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>Password cifrata con DPAPI (base64); resta impostata solo se non è stato possibile decifrarla.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(ProtectedPassword);

    /// <summary>"Nome (DOMINIO\utente)", per gli elenchi.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var account = Account;
            var name = string.IsNullOrWhiteSpace(Name) ? account : Name;
            return account.Length > 0 && !name.Equals(account, StringComparison.CurrentCultureIgnoreCase)
                ? $"{name} ({account})" : name;
        }
    }

    /// <summary>"DOMINIO\utente" o "utente".</summary>
    [JsonIgnore]
    public string Account => Domain.Length > 0 ? $"{Domain}\\{Username}" : Username;

    public CredentialProfile Clone() => (CredentialProfile)MemberwiseClone();

    /// <summary>Stesso profilo per l'importazione senza doppioni: stesso nome, utente e dominio.</summary>
    public bool IsSameAs(CredentialProfile other) =>
        Name.Trim().Equals(other.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)
        && Username.Trim().Equals(other.Username.Trim(), StringComparison.OrdinalIgnoreCase)
        && Domain.Trim().Equals(other.Domain.Trim(), StringComparison.OrdinalIgnoreCase);

    internal void Repair()
    {
        Name ??= "";
        Username ??= "";
        Domain ??= "";
    }
}

/// <summary>Credenziali effettive di una connessione o del suo gateway.</summary>
/// <param name="ProfileId">Profilo da cui vengono, null se sono quelle della connessione.</param>
public sealed record ResolvedCredentials(string Username, string Domain, string? Password, Guid? ProfileId = null)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

public sealed partial class ConnectionsDocument
{
    /// <summary>Profili di credenziali condivisi (dalla 1.2).</summary>
    public List<CredentialProfile> CredentialProfiles { get; set; } = [];

    public CredentialProfile? FindProfile(Guid? id) =>
        id is Guid g ? CredentialProfiles.FirstOrDefault(p => p.Id == g) : null;

    /// <summary>
    /// Utente, dominio e password con cui collegarsi: quelli del profilo se la connessione ne usa uno esistente,
    /// altrimenti quelli della connessione (anche se il profilo indicato non c'è più).
    /// </summary>
    public ResolvedCredentials ResolveCredentials(RdpConnection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return FindProfile(c.CredentialProfileId) is { } p
            ? new ResolvedCredentials(p.Username, p.Domain, p.Password, p.Id)
            : new ResolvedCredentials(c.Username, c.Domain, c.Password);
    }

    /// <summary>
    /// Credenziali del gateway: null se la connessione non usa un gateway, oppure se deve usare un profilo
    /// che non esiste più (il controllo RDP le chiederà).
    /// </summary>
    public ResolvedCredentials? ResolveGatewayCredentials(RdpConnection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!c.HasGateway)
            return null;
        if (c.GatewayCredentials == GatewayCredentialsSource.SameAsConnection)
            return ResolveCredentials(c);
        return FindProfile(c.GatewayProfileId) is { } p ? new ResolvedCredentials(p.Username, p.Domain, p.Password, p.Id) : null;
    }

    /// <summary>
    /// Copia della connessione con utente, dominio e password effettivi (dal profilo, se c'è): è quella da passare
    /// al controllo RDP o a mstsc, che non conoscono i profili. Il profilo resta indicato nella copia.
    /// </summary>
    public RdpConnection WithResolvedCredentials(RdpConnection c)
    {
        var copy = c.Clone();
        var creds = ResolveCredentials(c);
        copy.Username = creds.Username;
        copy.Domain = creds.Domain;
        copy.Password = creds.Password;
        if (creds.ProfileId is not null)
            copy.ProtectedPassword = null;
        return copy;
    }

    /// <summary>Connessioni che usano il profilo, per sé o per il gateway.</summary>
    public IReadOnlyList<RdpConnection> ConnectionsUsingProfile(Guid profileId) =>
        Connections.Where(c => c.CredentialProfileId == profileId || c.GatewayProfileId == profileId).ToList();

    /// <summary>
    /// Elimina il profilo. Le connessioni che lo usavano ricevono una copia di utente e dominio (non la password,
    /// che resta da reinserire solo se non ne avevano una propria), così non restano senza utente.
    /// </summary>
    public bool DeleteProfile(Guid profileId)
    {
        var profile = FindProfile(profileId);
        if (profile is null)
            return false;
        foreach (var c in Connections)
        {
            if (c.CredentialProfileId == profileId)
            {
                c.CredentialProfileId = null;
                if (c.Username.Length == 0)
                {
                    c.Username = profile.Username;
                    c.Domain = profile.Domain;
                }
            }
            if (c.GatewayProfileId == profileId)
            {
                c.GatewayProfileId = null;
                c.GatewayCredentials = GatewayCredentialsSource.SameAsConnection;
            }
        }
        CredentialProfiles.Remove(profile);
        return true;
    }
}
