using System.Text.Json.Serialization;

namespace RdpManager.Core;

public enum ResolutionMode
{
    /// <summary>La risoluzione remota segue la dimensione della scheda/finestra.</summary>
    FitToWindow,
    /// <summary>Risoluzione fissa (DesktopWidth x DesktopHeight).</summary>
    Fixed,
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

    /// <summary>Cartella dell'elenco; le sottocartelle si separano con '/', vuoto = radice.</summary>
    public string Group { get; set; } = "";

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

    /// <summary>Ricerca veloce: tutte le parole devono comparire in nome, host, utente, dominio o cartella.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var haystack = string.Join('\n', Name, Host, Username, Domain, Group, Port.ToString());
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeGroup(string? group) =>
        string.Join('/', (group ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
