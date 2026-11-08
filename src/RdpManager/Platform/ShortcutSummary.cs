namespace RdpManager.Core;

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
