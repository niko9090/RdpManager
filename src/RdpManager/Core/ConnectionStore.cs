using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>Contenuto di connections.json.</summary>
public sealed partial class ConnectionsDocument
{
    /// <summary>
    /// Versione scritta da questa build: 1 = RdpManager 1.0.x, 2 = 1.1 (campi nuovi e impostazioni),
    /// 3 = 1.2 (profili di credenziali, gateway, testi rapidi), 4 = 1.3 (impostazioni nuove, ordine manuale),
    /// 5 = 1.4 (spazi di lavoro, PIN di blocco, profili di qualità, sessione tenuta sveglia),
    /// 6 = 1.5 (barra a schermo intero personalizzabile).
    /// </summary>
    public const int CurrentVersion = 6;

    /// <summary>Versione letta dal file; i file della 1.0.0 e 1.1.0 hanno 1 e 2 e si caricano con i valori predefiniti per i campi nuovi.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Impostazioni dell'app (assenti nei file della 1.0.0).</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>Cartelle create esplicitamente (anche vuote).</summary>
    public List<string> Groups { get; set; } = [];

    public List<RdpConnection> Connections { get; set; } = [];

    /// <summary>Tutte le cartelle note: quelle esplicite più quelle usate dalle connessioni, con i loro antenati.</summary>
    public IReadOnlyList<string> AllGroups()
    {
        var set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in Groups.Concat(Connections.Select(c => c.Group)))
        {
            var parts = RdpConnection.NormalizeGroup(g).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= parts.Length; i++)
                set.Add(string.Join('/', parts.Take(i)));
        }
        return set.ToList();
    }

    public static bool IsInFolder(string group, string folder) =>
        group.Equals(folder, StringComparison.CurrentCultureIgnoreCase)
        || group.StartsWith(folder + "/", StringComparison.CurrentCultureIgnoreCase);

    public static string ParentFolder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>Crea la cartella <paramref name="name"/> dentro <paramref name="parent"/>; restituisce il percorso o null se il nome è vuoto.</summary>
    public string? AddFolder(string parent, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var path = RdpConnection.NormalizeGroup(parent.Length > 0 ? $"{parent}/{name}" : name);
        if (path.Length == 0)
            return null;
        if (!Groups.Contains(path, StringComparer.CurrentCultureIgnoreCase))
            Groups.Add(path);
        return path;
    }

    /// <summary>Rinomina una cartella (stesso livello) spostando sottocartelle e connessioni; restituisce il nuovo percorso.</summary>
    public string? RenameFolder(string path, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return null;
        var parent = ParentFolder(path);
        var newPath = RdpConnection.NormalizeGroup(parent.Length > 0 ? $"{parent}/{newName}" : newName);
        if (newPath.Length == 0)
            return null;
        string Move(string g) => RdpConnection.NormalizeGroup(newPath + g[path.Length..]);
        foreach (var c in Connections.Where(c => IsInFolder(c.Group, path)))
            c.Group = Move(c.Group);
        Groups = AllGroups().Select(g => IsInFolder(g, path) ? Move(g) : g)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return newPath;
    }

    public RdpConnection? Find(Guid id) => Connections.FirstOrDefault(c => c.Id == id);

    /// <summary>Connessioni della cartella e delle sue sottocartelle, nell'ordine dell'elenco ("Connetti tutta la cartella").</summary>
    public IReadOnlyList<RdpConnection> ConnectionsInFolder(string folder)
    {
        folder = RdpConnection.NormalizeGroup(folder);
        return Connections.Where(c => folder.Length == 0 || IsInFolder(c.Group, folder)).ToList();
    }

    /// <summary>Connessioni nell'ordine dell'elenco: per nome, oppure quello scelto trascinandole.</summary>
    public IEnumerable<RdpConnection> InDisplayOrder(IEnumerable<RdpConnection> connections) =>
        Settings.ConnectionOrder == ConnectionOrder.Manual
            ? connections.OrderBy(c => Connections.IndexOf(c))
            : connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Sposta una connessione trascinata: nella cartella <paramref name="group"/>, prima di <paramref name="before"/>
    /// (o in fondo se null). L'elenco passa all'ordine manuale, partendo dall'ordine che si vedeva, così le altre
    /// connessioni non saltano. Falso se non c'è nulla da spostare.
    /// </summary>
    public bool MoveConnection(Guid id, Guid? before, string group)
    {
        if (Find(id) is not { } moving || before == id)
            return false;
        group = RdpConnection.NormalizeGroup(group);
        if (Settings.ConnectionOrder != ConnectionOrder.Manual)
        {
            Connections = InDisplayOrder(Connections).ToList();
            Settings.ConnectionOrder = ConnectionOrder.Manual;
        }
        Connections.Remove(moving);
        moving.Group = group;
        int index = before is Guid b ? Connections.FindIndex(c => c.Id == b) : -1;
        if (index < 0)
        {
            // In fondo alla cartella: dopo l'ultima connessione della stessa cartella (o in fondo all'elenco).
            int last = Connections.FindLastIndex(c => c.Group.Equals(group, StringComparison.CurrentCultureIgnoreCase));
            index = last < 0 ? Connections.Count : last + 1;
        }
        Connections.Insert(index, moving);
        return true;
    }

    /// <summary>Torna all'ordine per nome (l'ordine del file resta quello manuale finché non lo si cambia).</summary>
    public void SortAlphabetically()
    {
        Connections = Connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        Settings.ConnectionOrder = ConnectionOrder.Alphabetical;
    }

    public IReadOnlyList<RdpConnection> Favorites() =>
        Connections.Where(c => c.IsFavorite)
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Le ultime <paramref name="max"/> connessioni aperte, dalla più recente.</summary>
    public IReadOnlyList<RdpConnection> Recent(int max = 10) =>
        Connections.Where(c => c.LastConnectedUtc is not null)
            .OrderByDescending(c => c.LastConnectedUtc).Take(Math.Max(0, max)).ToList();

    /// <summary>Annota l'apertura di una connessione per l'elenco dei recenti.</summary>
    public void MarkConnected(Guid id, DateTime utcNow)
    {
        if (Find(id) is { } c)
            c.LastConnectedUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    /// <summary>Elimina la cartella e le sottocartelle; le connessioni contenute passano alla cartella superiore (non vengono cancellate).</summary>
    public string DeleteFolder(string path)
    {
        var parent = ParentFolder(path);
        foreach (var c in Connections.Where(c => IsInFolder(c.Group, path)))
            c.Group = parent;
        Groups = AllGroups().Where(g => !IsInFolder(g, path)).ToList();
        return parent;
    }
}

/// <summary>Lettura/scrittura delle connessioni su file JSON; le password vengono cifrate con DPAPI.</summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager", "connections.json");

    public string FilePath { get; }

    public ConnectionStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath;
    }

    /// <exception cref="JsonException">File presente ma non leggibile.</exception>
    public ConnectionsDocument Load() => Load(decryptPasswords: true);

    /// <param name="decryptPasswords">
    /// Falso = le password restano cifrate DPAPI (app bloccata con il PIN): si decifrano dopo lo sblocco con
    /// <see cref="ConnectionsDocument.UnsealPasswords"/>.
    /// </param>
    /// <exception cref="JsonException">File presente ma non leggibile.</exception>
    public ConnectionsDocument Load(bool decryptPasswords)
    {
        if (!File.Exists(FilePath))
            return new ConnectionsDocument();

        var doc = JsonSerializer.Deserialize<ConnectionsDocument>(File.ReadAllText(FilePath), JsonOptions)
                  ?? new ConnectionsDocument();
        doc.Groups ??= [];
        doc.Connections ??= [];
        doc.Settings ??= new AppSettings();
        doc.Settings.Normalize();
        doc.CredentialProfiles ??= [];
        doc.CredentialProfiles.RemoveAll(p => p is null);
        foreach (var p in doc.CredentialProfiles)
        {
            p.Repair();
            if (decryptPasswords && PasswordProtector.TryUnprotect(p.ProtectedPassword, out var profilePassword))
            {
                p.Password = profilePassword;
                p.ProtectedPassword = null;
            }
        }
        doc.QuickTexts ??= [];
        doc.QuickTexts.RemoveAll(t => t is null);
        foreach (var t in doc.QuickTexts)
            t.Repair();
        doc.Workspaces ??= [];
        doc.Workspaces.RemoveAll(w => w is null);
        foreach (var w in doc.Workspaces)
            w.Repair();
        foreach (var c in doc.Connections)
        {
            c.GatewayHost ??= "";
            if (!Enum.IsDefined(c.GatewayCredentials))
                c.GatewayCredentials = GatewayCredentialsSource.SameAsConnection;
            if (!Enum.IsDefined(c.Quality))
                c.Quality = QualityProfile.Automatic;
            c.Group = RdpConnection.NormalizeGroup(c.Group);
            c.Name ??= "";
            c.Host ??= "";
            c.Username ??= "";
            c.Domain ??= "";
            c.Label ??= "";
            c.MacAddress ??= "";
            c.Color = RdpConnection.NormalizeColor(c.Color);
            if (c.AutoReconnectMaxAttempts < 0)
                c.AutoReconnectMaxAttempts = ReconnectPolicy.DefaultMaxAttempts;
            if (decryptPasswords && PasswordProtector.TryUnprotect(c.ProtectedPassword, out var plain))
            {
                c.Password = plain;
                c.ProtectedPassword = null;
            }
            // Se la decifratura fallisce ProtectedPassword resta com'è e verrà riscritta tale e quale.
        }
        return doc;
    }

    public void Save(ConnectionsDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var toWrite = new ConnectionsDocument
        {
            Version = ConnectionsDocument.CurrentVersion,
            Settings = doc.Settings ?? new AppSettings(),
            Groups = doc.Groups.Select(RdpConnection.NormalizeGroup).Where(g => g.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList(),
            Connections = doc.Connections.Select(c =>
            {
                var copy = c.Clone();
                if (!string.IsNullOrEmpty(c.Password))
                    copy.ProtectedPassword = PasswordProtector.Protect(c.Password);
                copy.Password = null;
                return copy;
            }).ToList(),
            CredentialProfiles = doc.CredentialProfiles.Select(p =>
            {
                var copy = p.Clone();
                if (!string.IsNullOrEmpty(p.Password))
                    copy.ProtectedPassword = PasswordProtector.Protect(p.Password);
                copy.Password = null;
                return copy;
            }).ToList(),
            QuickTexts = doc.QuickTexts.Select(t => t.Clone()).ToList(),
            Workspaces = doc.Workspaces.Select(w => w.Clone()).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(toWrite, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
