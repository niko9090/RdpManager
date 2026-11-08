namespace RdpManager.Core;

/// <summary>Azioni su più connessioni selezionate insieme nell'elenco.</summary>
public static class GroupActions
{
    /// <summary>Le connessioni con gli Id indicati, nell'ordine dell'elenco (gli Id sconosciuti si ignorano).</summary>
    public static IReadOnlyList<RdpConnection> Resolve(ConnectionsDocument doc, IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return doc.Connections.Where(c => set.Contains(c.Id)).ToList();
    }

    /// <summary>Sposta le connessioni nella cartella (creandola); restituisce quante sono cambiate.</summary>
    public static int MoveToFolder(ConnectionsDocument doc, IEnumerable<Guid> ids, string folder)
    {
        folder = RdpConnection.NormalizeGroup(folder);
        int n = 0;
        foreach (var c in Resolve(doc, ids).Where(c => !c.Group.Equals(folder, StringComparison.CurrentCultureIgnoreCase)))
        {
            c.Group = folder;
            n++;
        }
        if (folder.Length > 0)
            doc.AddFolder("", folder);
        return n;
    }

    /// <summary>Assegna un profilo di credenziali (null = credenziali proprie); restituisce quante sono cambiate.</summary>
    /// <exception cref="ArgumentException">Il profilo non esiste.</exception>
    public static int AssignProfile(ConnectionsDocument doc, IEnumerable<Guid> ids, Guid? profileId)
    {
        if (profileId is Guid p && doc.FindProfile(p) is null)
            throw new ArgumentException("Profilo di credenziali inesistente.", nameof(profileId));
        int n = 0;
        foreach (var c in Resolve(doc, ids).Where(c => c.CredentialProfileId != profileId))
        {
            c.CredentialProfileId = profileId;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Elimina le connessioni (anche dagli spazi di lavoro e dalle sessioni da riaprire); restituisce quante.
    /// La conferma all'utente spetta all'interfaccia.
    /// </summary>
    public static int Delete(ConnectionsDocument doc, IEnumerable<Guid> ids)
    {
        var list = Resolve(doc, ids);
        foreach (var c in list)
        {
            doc.Connections.Remove(c);
            doc.RemoveFromWorkspaces(c.Id);
            doc.Settings.OpenSessions.Remove(c.Id);
        }
        return list.Count;
    }

    /// <summary>Testo della conferma di eliminazione («3 connessioni: A, B, C»), con al massimo 8 nomi.</summary>
    public static string DescribeForConfirmation(IReadOnlyList<RdpConnection> list)
    {
        var names = list.Take(8).Select(c => c.DisplayName).ToList();
        var more = list.Count > 8 ? $" e altre {list.Count - 8}" : "";
        return $"{list.Count} connessioni: {string.Join(", ", names)}{more}";
    }
}
