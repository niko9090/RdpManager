using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>Contenuto di connections.json.</summary>
public sealed class ConnectionsDocument
{
    public int Version { get; set; } = 1;

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
    public ConnectionsDocument Load()
    {
        if (!File.Exists(FilePath))
            return new ConnectionsDocument();

        var doc = JsonSerializer.Deserialize<ConnectionsDocument>(File.ReadAllText(FilePath), JsonOptions)
                  ?? new ConnectionsDocument();
        doc.Groups ??= [];
        doc.Connections ??= [];
        foreach (var c in doc.Connections)
        {
            c.Group = RdpConnection.NormalizeGroup(c.Group);
            if (PasswordProtector.TryUnprotect(c.ProtectedPassword, out var plain))
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
            Version = doc.Version,
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
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(toWrite, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
