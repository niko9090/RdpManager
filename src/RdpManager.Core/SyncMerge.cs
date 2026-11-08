using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>
/// Impronte (HMAC con una chiave segreta di questo PC) di ogni elemento sincronizzato: connessioni, cartelle, profili di
/// credenziali, testi rapidi e impostazioni delle cartelle. Servono a sapere cosa è cambiato dall'ultima sincronizzazione
/// senza tenere su disco una copia dei dati; la chiave le rende inutili per indovinare le password.
/// </summary>
public sealed class SyncFingerprints(byte[] key)
{
    private string H(string text) => Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)))[..22];

    public static string Key(RdpConnection c) => "c:" + c.Id;
    public static string Key(CredentialProfile p) => "p:" + p.Id;
    public static string Key(QuickText t) => "q:" + t.Id;
    public static string Key(FolderSettings f) => "f:" + f.Path.ToLowerInvariant();
    public static string GroupKey(string g) => "g:" + g.ToLowerInvariant();

    public string Of(RdpConnection c)
    {
        var copy = c.Clone();
        copy.Password = null;
        copy.ProtectedPassword = null;
        copy.LastConnectedUtc = null; // vale solo per questo PC
        return H(JsonSerializer.Serialize(copy) + "\n" + (c.Password ?? ""));
    }

    public string Of(CredentialProfile p)
    {
        var copy = p.Clone();
        copy.Password = null;
        copy.ProtectedPassword = null;
        return H(JsonSerializer.Serialize(copy) + "\n" + (p.Password ?? ""));
    }

    public string Of(QuickText t) => H(JsonSerializer.Serialize(t));
    public string Of(FolderSettings f) => H(JsonSerializer.Serialize(f));
    public string OfGroup(string g) => H(g.ToLowerInvariant());

    /// <summary>Chiave elemento → impronta, per tutto ciò che si sincronizza.</summary>
    public Dictionary<string, string> Of(ConnectionsDocument d)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in d.Connections)
            map[Key(c)] = Of(c);
        foreach (var p in d.CredentialProfiles)
            map[Key(p)] = Of(p);
        foreach (var t in d.QuickTexts)
            map[Key(t)] = Of(t);
        foreach (var f in d.FolderSettings)
            map[Key(f)] = Of(f);
        foreach (var g in d.AllGroups())
            map[GroupKey(g)] = OfGroup(g);
        return map;
    }
}

/// <summary>
/// Unione a tre vie fra il documento locale, quello del server e l'ultimo stato sincronizzato (le impronte):
/// ciò che è nuovo da una parte si aggiunge all'altra; ciò che manca da una parte ma c'era all'ultima sincronizzazione
/// è stato cancellato e si cancella anche di là (salvo sia stato modificato nel frattempo); se lo stesso elemento è
/// cambiato da entrambe le parti vince quello locale (poi viene rimandato al server).
/// </summary>
public static class SyncMerge
{
    /// <summary>Copia profonda dei soli dati che si sincronizzano (le impostazioni dell'app non servono).</summary>
    public static ConnectionsDocument Snapshot(ConnectionsDocument d) => new()
    {
        Groups = [.. d.Groups],
        Connections = d.Connections.Select(c => c.Clone()).ToList(),
        CredentialProfiles = d.CredentialProfiles.Select(p => p.Clone()).ToList(),
        QuickTexts = d.QuickTexts.Select(t => t.Clone()).ToList(),
        FolderSettings = d.FolderSettings.Select(f => f.Clone()).ToList(),
    };

    /// <summary>Porta in <paramref name="local"/> la fusione con <paramref name="remote"/>. Vero se <paramref name="local"/> è cambiato.</summary>
    /// <param name="baseHashes">Impronte dell'ultima sincronizzazione; vuote = primo collegamento (unione senza cancellazioni).</param>
    public static bool MergeInto(ConnectionsDocument local, ConnectionsDocument remote,
        IReadOnlyDictionary<string, string> baseHashes, SyncFingerprints fp)
    {
        bool firstLink = baseHashes.Count == 0;
        bool changed = false;

        // Profili prima delle connessioni: una connessione importata trova già il suo profilo.
        changed |= MergeList(local.CredentialProfiles, remote.CredentialProfiles, baseHashes,
            SyncFingerprints.Key, fp.Of, CopyProfile, p => p.Clone(), null);

        changed |= MergeList(local.Connections, remote.Connections, baseHashes,
            SyncFingerprints.Key, fp.Of, CopyConnection, c => c.Clone(),
            // Al primo collegamento due PC possono avere già la stessa destinazione con Id diversi: si tiene una sola.
            firstLink ? r => local.Connections.Any(l => l.Id != r.Id && l.IsSameTarget(r)) : null);

        changed |= MergeList(local.QuickTexts, remote.QuickTexts, baseHashes,
            SyncFingerprints.Key, fp.Of, CopyProperties, t => t.Clone(), null);

        changed |= MergeList(local.FolderSettings, remote.FolderSettings, baseHashes,
            SyncFingerprints.Key, fp.Of, CopyProperties, f => f.Clone(), null);

        // Cartelle: solo quelle esplicite di entrambe le parti (le altre vengono dalle connessioni).
        var remoteGroups = remote.AllGroups();
        var localGroups = local.AllGroups();
        var localKeys = localGroups.Select(SyncFingerprints.GroupKey).ToHashSet();
        foreach (var g in remoteGroups)
        {
            var k = SyncFingerprints.GroupKey(g);
            if (localKeys.Contains(k) || baseHashes.ContainsKey(k))
                continue; // c'è già, oppure l'abbiamo cancellata noi
            local.AddFolder("", g);
            changed = true;
        }
        var remoteKeys = remoteGroups.Select(SyncFingerprints.GroupKey).ToHashSet();
        foreach (var g in localGroups)
        {
            var k = SyncFingerprints.GroupKey(g);
            if (remoteKeys.Contains(k) || !baseHashes.ContainsKey(k))
                continue;
            // Cancellata sul server: si toglie qui se non contiene più connessioni.
            if (local.Connections.Any(c => ConnectionsDocument.IsInFolder(c.Group, g)))
                continue;
            if (local.Groups.RemoveAll(x => SyncFingerprints.GroupKey(x) == k) > 0)
                changed = true;
        }
        return changed;
    }

    private static bool MergeList<T>(List<T> local, List<T> remote, IReadOnlyDictionary<string, string> baseHashes,
        Func<T, string> key, Func<T, string> hash, Action<T, T> copyInto, Func<T, T> clone, Func<T, bool>? skipRemote)
    {
        bool changed = false;
        var remoteByKey = new Dictionary<string, T>();
        foreach (var r in remote)
            remoteByKey.TryAdd(key(r), r);
        var localKeys = new HashSet<string>();

        foreach (var l in local.ToList())
        {
            var k = key(l);
            if (!localKeys.Add(k))
                continue;
            baseHashes.TryGetValue(k, out var bh);
            if (remoteByKey.TryGetValue(k, out var r))
            {
                if (bh is not null && hash(l) == bh && hash(r) != bh)
                {
                    copyInto(l, r); // modificato solo sul server
                    changed = true;
                }
            }
            else if (bh is not null && hash(l) == bh)
            {
                local.Remove(l); // cancellato sul server e qui non toccato
                changed = true;
            }
        }

        foreach (var (k, r) in remoteByKey)
        {
            if (localKeys.Contains(k))
                continue;
            // Presente all'ultima sincronizzazione e non modificato sul server: l'abbiamo cancellato noi.
            if (baseHashes.TryGetValue(k, out var bh) && hash(r) == bh)
                continue;
            if (skipRemote?.Invoke(r) == true)
                continue;
            local.Add(clone(r));
            changed = true;
        }
        return changed;
    }

    private static void CopyConnection(RdpConnection to, RdpConnection from)
    {
        var lastUsed = to.LastConnectedUtc;
        CopyProperties(to, from);
        to.LastConnectedUtc = lastUsed;
        to.ProtectedPassword = null;
    }

    private static void CopyProfile(CredentialProfile to, CredentialProfile from)
    {
        CopyProperties(to, from);
        to.ProtectedPassword = null;
    }

    private static readonly Dictionary<Type, PropertyInfo[]> Properties = [];

    /// <summary>Copia tutte le proprietà scrivibili: l'oggetto locale resta lo stesso (la finestra lo tiene per riferimento).</summary>
    private static void CopyProperties<T>(T to, T from)
    {
        PropertyInfo[] props;
        lock (Properties)
        {
            if (!Properties.TryGetValue(typeof(T), out props!))
                Properties[typeof(T)] = props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
        }
        foreach (var p in props)
            p.SetValue(to, p.GetValue(from));
    }
}
