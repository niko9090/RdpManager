namespace RdpManager.Core;

/// <summary>
/// Impostazioni comuni di una cartella (gruppo) di connessioni, ereditate dalle connessioni contenute e dalle
/// sottocartelle (vale quella della cartella più vicina). Dalla 1.6.2; assenti nei file precedenti.
/// </summary>
public sealed class FolderSettings
{
    /// <summary>Percorso della cartella, con '/' fra i livelli (vedi <see cref="RdpConnection.NormalizeGroup"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>Profilo di credenziali per le connessioni della cartella che non hanno credenziali proprie.</summary>
    public Guid? CredentialProfileId { get; set; }

    /// <summary>Risoluzione per le connessioni che la ereditano (<see cref="RdpConnection.InheritFolderResolution"/>); null = nessuna.</summary>
    public ResolutionMode? Resolution { get; set; }

    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;

    public bool IsEmpty => CredentialProfileId is null && Resolution is null;

    public FolderSettings Clone() => (FolderSettings)MemberwiseClone();

    internal void Repair()
    {
        Path = RdpConnection.NormalizeGroup(Path ?? "");
        if (Resolution is { } r && !Enum.IsDefined(r))
            Resolution = null;
        DesktopWidth = Math.Clamp(DesktopWidth, 640, 8192);
        DesktopHeight = Math.Clamp(DesktopHeight, 480, 8192);
    }
}

/// <summary>Impostazioni effettive di una cartella: quelle proprie o, campo per campo, della cartella superiore più vicina.</summary>
public sealed record EffectiveFolderSettings(Guid? CredentialProfileId, ResolutionMode? Resolution, int DesktopWidth, int DesktopHeight);

public sealed partial class ConnectionsDocument
{
    /// <summary>Impostazioni delle cartelle (dalla 1.6.2).</summary>
    public List<FolderSettings> FolderSettings { get; set; } = [];

    public FolderSettings? FindFolderSettings(string path)
    {
        path = RdpConnection.NormalizeGroup(path);
        return FolderSettings.FirstOrDefault(f => f.Path.Equals(path, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Imposta (o, se vuote, toglie) le impostazioni di una cartella; la radice non ne ha.</summary>
    public void SetFolderSettings(string path, Guid? profileId, ResolutionMode? resolution, int width = 1920, int height = 1080)
    {
        path = RdpConnection.NormalizeGroup(path);
        if (path.Length == 0)
            return;
        FolderSettings.RemoveAll(f => f.Path.Equals(path, StringComparison.CurrentCultureIgnoreCase));
        var s = new FolderSettings { Path = path, CredentialProfileId = profileId, Resolution = resolution, DesktopWidth = width, DesktopHeight = height };
        s.Repair();
        if (!s.IsEmpty)
        {
            FolderSettings.Add(s);
            AddFolder("", path);
        }
    }

    /// <summary>Impostazioni valide per la cartella: ogni campo viene dalla cartella più vicina (lei compresa) che lo imposta.</summary>
    public EffectiveFolderSettings FolderSettingsFor(string path)
    {
        path = RdpConnection.NormalizeGroup(path);
        Guid? profile = null;
        FolderSettings? res = null;
        for (var p = path; p.Length > 0; p = ParentFolder(p))
        {
            var f = FindFolderSettings(p);
            if (f is null)
                continue;
            if (profile is null && FindProfile(f.CredentialProfileId) is not null)
                profile = f.CredentialProfileId;
            if (res is null && f.Resolution is not null)
                res = f;
        }
        return new EffectiveFolderSettings(profile, res?.Resolution, res?.DesktopWidth ?? 1920, res?.DesktopHeight ?? 1080);
    }

    /// <summary>
    /// Profilo che la connessione eredita dalla sua cartella: solo se non ha un profilo suo né utente o password propri.
    /// </summary>
    public CredentialProfile? InheritedProfile(RdpConnection c)
    {
        if (c.CredentialProfileId is not null && FindProfile(c.CredentialProfileId) is not null)
            return null;
        if (c.Username.Length > 0 || c.HasPassword || c.Group.Length == 0)
            return null;
        return FindProfile(FolderSettingsFor(c.Group).CredentialProfileId);
    }

    private void MoveFolderSettings(string oldPath, string newPath)
    {
        foreach (var f in FolderSettings.Where(f => IsInFolder(f.Path, oldPath)))
            f.Path = RdpConnection.NormalizeGroup(newPath + f.Path[oldPath.Length..]);
    }

    private void DropFolderSettings(string path) =>
        FolderSettings.RemoveAll(f => IsInFolder(f.Path, path));

    /// <summary>
    /// Sposta una cartella (con sottocartelle e connessioni) dentro un'altra; "" = radice.
    /// Restituisce il nuovo percorso, o null se non è possibile (dentro se stessa, o esiste già una cartella con quel nome).
    /// </summary>
    public string? MoveFolder(string path, string? newParent)
    {
        path = RdpConnection.NormalizeGroup(path);
        newParent = RdpConnection.NormalizeGroup(newParent);
        if (path.Length == 0 || IsInFolder(newParent, path))
            return null;
        var name = path[(path.LastIndexOf('/') + 1)..];
        var newPath = newParent.Length > 0 ? $"{newParent}/{name}" : name;
        if (newPath.Equals(path, StringComparison.CurrentCultureIgnoreCase))
            return path;
        if (AllGroups().Any(g => g.Equals(newPath, StringComparison.CurrentCultureIgnoreCase)))
            return null;
        string Move(string g) => RdpConnection.NormalizeGroup(newPath + g[path.Length..]);
        foreach (var c in Connections.Where(c => IsInFolder(c.Group, path)))
            c.Group = Move(c.Group);
        var all = AllGroups();
        MoveFolderSettings(path, newPath);
        Groups = all.Select(g => IsInFolder(g, path) ? Move(g) : g)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return newPath;
    }
}
