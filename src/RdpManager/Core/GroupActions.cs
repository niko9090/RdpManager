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

/// <summary>Una riga del riepilogo delle scorciatoie (F1).</summary>
public sealed record ShortcutLine(string Section, string Keys, string Action);

/// <summary>Riepilogo di tutte le scorciatoie (quelle configurabili con i valori in uso e quelle fisse dell'app).</summary>
public static class ShortcutSummary
{
    public static IReadOnlyList<ShortcutLine> Build(ShortcutMap map, string globalHotkey, IEnumerable<Workspace>? workspaces = null)
    {
        const string inSession = "Anche dentro la sessione remota (con RdpManager in primo piano)";
        var lines = Enum.GetValues<ShortcutAction>()
            .Select(a => new ShortcutLine(inSession, map[a].IsEmpty ? "(nessuna)" : map[a].ToString(), ShortcutMap.Describe(a)))
            .ToList();
        var global = GlobalHotkey.FromSettings(globalHotkey);
        lines.Add(new ShortcutLine("Da qualunque programma", global.IsEmpty ? "(nessuna)" : global.ToString(), "Richiama RdpManager"));
        foreach (var w in workspaces ?? [])
            if (w.Shortcut.Length > 0)
                lines.Add(new ShortcutLine("Spazi di lavoro", w.Shortcut, $"Apri lo spazio «{w.Name}»"));
        const string app = "Nella finestra di RdpManager";
        (string, string)[] fixedKeys =
        [
            ("F1", "Questo riepilogo delle scorciatoie"),
            ("Ctrl+Tab / Ctrl+Maiusc+Tab", "Scheda successiva / precedente"),
            ("Ctrl+N / Ctrl+Maiusc+N", "Nuova connessione / nuova cartella"),
            ("Ctrl+E o F2", "Modifica"),
            ("Ctrl+D", "Duplica"),
            ("Canc", "Elimina (anche più connessioni selezionate)"),
            ("Ctrl+clic, Maiusc+clic", "Selezione multipla nell'elenco"),
            ("Ctrl+Maiusc+F", "Preferita sì/no"),
            ("Ctrl+F", "Cerca nell'elenco"),
            ("Ctrl+I", "Importa file .rdp"),
            ("Ctrl+H", "Cronologia delle connessioni"),
            ("Ctrl+,", "Impostazioni"),
            ("Invio", "Connetti (anche più connessioni selezionate)"),
            ("F5 / F11", "Riconnetti / schermo intero"),
            ("Ctrl+Maiusc+D", "Stacca in una finestra"),
            ("Ctrl+F4", "Chiudi scheda"),
        ];
        lines.AddRange(fixedKeys.Select(k => new ShortcutLine(app, k.Item1, k.Item2)));
        return lines;
    }
}
