using System.Text.Json.Serialization;

namespace RdpManager.Core;

public enum ResolutionMode
{
    /// <summary>La risoluzione remota segue la dimensione della scheda/finestra.</summary>
    FitToWindow,
    /// <summary>Risoluzione fissa (DesktopWidth x DesktopHeight).</summary>
    Fixed,
}

/// <summary>Dove riprodurre l'audio della sessione remota (stessi valori di "audiomode" nei file .rdp).</summary>
public enum AudioMode
{
    /// <summary>Audio riprodotto su questo PC.</summary>
    Local = 0,
    /// <summary>Audio lasciato sul PC remoto.</summary>
    Remote = 1,
    /// <summary>Nessun audio.</summary>
    None = 2,
}

/// <summary>Credenziali per il Remote Desktop Gateway.</summary>
public enum GatewayCredentialsSource
{
    /// <summary>Le stesse della connessione (o del suo profilo).</summary>
    SameAsConnection,
    /// <summary>Quelle del profilo <see cref="RdpConnection.GatewayProfileId"/>.</summary>
    Profile,
}

/// <summary>Una connessione desktop remoto salvata.</summary>
public sealed class RdpConnection
{
    public const int DefaultPort = 3389;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";

    /// <summary>Password in chiaro, solo in memoria: non viene mai serializzata.</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>
    /// Password cifrata con DPAPI (base64). La valorizza <see cref="ConnectionStore"/> al salvataggio;
    /// resta impostata in memoria solo se non è stato possibile decifrarla (es. altro utente Windows),
    /// così non va persa al salvataggio successivo.
    /// </summary>
    public string? ProtectedPassword { get; set; }

    public ResolutionMode ResolutionMode { get; set; } = ResolutionMode.FitToWindow;
    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }

    // --- Dalla 1.1: i valori predefiniti sono quelli usati per i file della 1.0.0 che non li contengono. ---

    public AudioMode AudioMode { get; set; } = AudioMode.Local;
    public bool RedirectPrinters { get; set; }
    public bool RedirectMicrophone { get; set; }

    /// <summary>La sessione usa tutti i monitor locali (come "use multimon" di mstsc).</summary>
    public bool UseAllMonitors { get; set; }

    /// <summary>Riconnessione automatica dopo una disconnessione non voluta, al massimo <see cref="AutoReconnectMaxAttempts"/> volte.</summary>
    public bool AutoReconnect { get; set; }
    public int AutoReconnectMaxAttempts { get; set; } = ReconnectPolicy.DefaultMaxAttempts;

    public bool IsFavorite { get; set; }

    /// <summary>Ultima connessione aperta (UTC), per l'elenco dei recenti; null = mai.</summary>
    public DateTime? LastConnectedUtc { get; set; }

    /// <summary>Colore della connessione, "#RRGGBB" (vedi <see cref="NormalizeColor"/>); null = nessuno.</summary>
    public string? Color { get; set; }

    /// <summary>Etichetta breve mostrata accanto al nome (es. "PROD").</summary>
    public string Label { get; set; } = "";

    /// <summary>Indirizzo MAC per il Wake-on-LAN, nella forma "AA:BB:CC:DD:EE:FF"; vuoto = non impostato.</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>Cartella dell'elenco; le sottocartelle si separano con '/', vuoto = radice.</summary>
    public string Group { get; set; } = "";

    // --- Dalla 1.2: assenti nei file 1.0/1.1, i predefiniti lasciano la connessione com'era. ---

    /// <summary>
    /// Profilo di credenziali condiviso (<see cref="CredentialProfile"/>): se impostato e presente, utente, dominio e
    /// password vengono dal profilo invece che dalla connessione (vedi <see cref="ConnectionsDocument.ResolveCredentials"/>).
    /// </summary>
    public Guid? CredentialProfileId { get; set; }

    /// <summary>Passa da un Remote Desktop Gateway (<see cref="GatewayHost"/>).</summary>
    public bool UseGateway { get; set; }

    /// <summary>Host del gateway, anche "host:porta".</summary>
    public string GatewayHost { get; set; } = "";

    public GatewayCredentialsSource GatewayCredentials { get; set; } = GatewayCredentialsSource.SameAsConnection;

    /// <summary>Profilo per il gateway quando <see cref="GatewayCredentials"/> è <see cref="GatewayCredentialsSource.Profile"/>.</summary>
    public Guid? GatewayProfileId { get; set; }

    /// <summary>Vero se il gateway è attivo e ha un host.</summary>
    [JsonIgnore]
    public bool HasGateway => UseGateway && !string.IsNullOrWhiteSpace(GatewayHost);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(ProtectedPassword);

    public RdpConnection Clone() => (RdpConnection)MemberwiseClone();

    /// <summary>Copia con nuovo Id e nome "(copia)", per il comando Duplica.</summary>
    public RdpConnection Duplicate()
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = DisplayName + " (copia)";
        return copy;
    }

    /// <summary>Ricerca veloce: tutte le parole devono comparire in nome, host, utente, dominio, cartella o etichetta.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var haystack = string.Join('\n', Name, Host, Username, Domain, Group, Label, Port.ToString());
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeGroup(string? group) =>
        string.Join('/', (group ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>"#RRGGBB" maiuscolo da "#rgb", "#rrggbb" o "rrggbb"; null se vuoto o non valido.</summary>
    public static string? NormalizeColor(string? color)
    {
        var hex = (color ?? "").Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>Stessa destinazione: host (senza distinzione di maiuscole), porta e utente.</summary>
    public bool IsSameTarget(RdpConnection other) =>
        Host.Trim().Equals(other.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && Username.Trim().Equals(other.Username.Trim(), StringComparison.OrdinalIgnoreCase)
        && Domain.Trim().Equals(other.Domain.Trim(), StringComparison.OrdinalIgnoreCase);
}
