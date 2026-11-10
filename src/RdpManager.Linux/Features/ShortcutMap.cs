using Avalonia.Input;
using RdpManager.Core;

namespace RdpManager.Linux.Features;

/// <summary>Una combinazione di tasti nel formato del programma Windows ("Ctrl+Alt+Destra"), senza dipendere da Windows Forms.</summary>
public readonly record struct KeyCombo(bool Ctrl, bool Alt, bool Shift, Key Key)
{
    private static readonly (Key Key, string[] Names)[] KeyNames =
    [
        (Key.Right, ["Destra", "Right", "→"]),
        (Key.Left, ["Sinistra", "Left", "←"]),
        (Key.Up, ["Su", "Up", "↑"]),
        (Key.Down, ["Giù", "Giu", "Down", "↓"]),
        (Key.Enter, ["Invio", "Enter", "Return"]),
        (Key.Space, ["Spazio", "Space"]),
        (Key.Tab, ["Tab"]),
        (Key.Escape, ["Esc", "Escape"]),
        (Key.Back, ["Backspace", "Back"]),
        (Key.Delete, ["Canc", "Del", "Delete"]),
        (Key.Insert, ["Ins", "Insert"]),
        (Key.Home, ["Home", "Inizio"]),
        (Key.End, ["Fine", "End"]),
        (Key.PageUp, ["PagSu", "PageUp", "PgUp"]),
        (Key.PageDown, ["PagGiù", "PagGiu", "PageDown", "PgDn"]),
        (Key.Pause, ["Pausa", "Pause", "Break"]),
    ];

    public bool IsEmpty => Key == Key.None;

    public bool HasCommandModifier => Ctrl || Alt;

    public override string ToString()
    {
        if (IsEmpty)
            return "";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Maiusc");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    public static string KeyName(Key key)
    {
        foreach (var (k, names) in KeyNames)
            if (k == key)
                return names[0];
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        return key.ToString();
    }

    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        bool ctrl = false, alt = false, shift = false;
        Key? key = null;
        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0)
                return false;
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "maiusc" or "shift": shift = true; continue;
            }
            if (key is not null || !TryParseKey(token, out var k))
                return false;
            key = k;
        }
        if (key is null)
            return false;
        combo = new KeyCombo(ctrl, alt, shift, key.Value);
        return true;
    }

    public static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        foreach (var (k, names) in KeyNames)
            if (names.Any(n => n.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                key = k;
                return true;
            }
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            key = Key.A + (char.ToUpperInvariant(token[0]) - 'A');
            return true;
        }
        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f') && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
        {
            key = Key.F1 + (f - 1);
            return true;
        }
        return false;
    }

    public static KeyCombo From(Key key, KeyModifiers modifiers) =>
        new(modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Shift), key);
}

/// <summary>Azione → combinazione, con i predefiniti di Windows, il controllo dei conflitti e la lettura delle impostazioni.</summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<ShortcutAction, KeyCombo> _map;

    public ShortcutMap(IReadOnlyDictionary<ShortcutAction, KeyCombo>? map = null) =>
        _map = new Dictionary<ShortcutAction, KeyCombo>(map ?? Defaults);

    public static IReadOnlyDictionary<ShortcutAction, KeyCombo> Defaults { get; } = BuildDefaults();

    private static Dictionary<ShortcutAction, KeyCombo> BuildDefaults()
    {
        var d = new Dictionary<ShortcutAction, KeyCombo>
        {
            [ShortcutAction.NextSession] = new(true, true, false, Key.Right),
            [ShortcutAction.PreviousSession] = new(true, true, false, Key.Left),
            [ShortcutAction.ToggleFullScreen] = new(true, true, false, Key.Enter),
            [ShortcutAction.ToggleSidebar] = new(true, true, false, Key.B),
            [ShortcutAction.Overview] = new(true, true, false, Key.G),
            [ShortcutAction.QuickSearch] = new(true, true, false, Key.Space),
            [ShortcutAction.ToggleSplitView] = new(true, true, false, Key.S),
            [ShortcutAction.Lock] = new(true, true, false, Key.L),
        };
        for (int n = 1; n <= 9; n++)
            d[ShortcutAction.Session1 + (n - 1)] = new(true, true, false, Key.D0 + n);
        return d;
    }

    public static int? SessionNumber(ShortcutAction action) =>
        action is >= ShortcutAction.Session1 and <= ShortcutAction.Session9 ? action - ShortcutAction.Session1 + 1 : null;

    public static string Describe(ShortcutAction action) => action switch
    {
        ShortcutAction.NextSession => "Sessione successiva",
        ShortcutAction.PreviousSession => "Sessione precedente",
        ShortcutAction.ToggleFullScreen => "Schermo intero sì/no",
        ShortcutAction.ToggleSidebar => "Mostra/nascondi barra laterale",
        ShortcutAction.Overview => "Panoramica delle sessioni",
        ShortcutAction.QuickSearch => "Ricerca rapida",
        ShortcutAction.ToggleSplitView => "Vista affiancata sì/no",
        ShortcutAction.Lock => "Blocca RdpManager (PIN)",
        _ when SessionNumber(action) is int n => $"Vai alla sessione {n}",
        _ => action.ToString(),
    };

    public KeyCombo this[ShortcutAction action]
    {
        get => _map.TryGetValue(action, out var c) ? c : default;
        set => _map[action] = value;
    }

    public IReadOnlyDictionary<ShortcutAction, KeyCombo> All => _map;

    public bool TryGetAction(KeyCombo combo, out ShortcutAction action)
    {
        action = default;
        if (combo.IsEmpty)
            return false;
        foreach (var (a, c) in _map)
            if (c == combo)
            {
                action = a;
                return true;
            }
        return false;
    }

    public IReadOnlyList<IReadOnlyList<ShortcutAction>> Conflicts() =>
        _map.Where(kv => !kv.Value.IsEmpty)
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<ShortcutAction>)g.Select(kv => kv.Key).OrderBy(a => a).ToList())
            .ToList();

    /// <summary>Azioni assenti, sconosciute o non valide: predefinito. Stringa vuota: azione disattivata.</summary>
    public static ShortcutMap FromSettings(IReadOnlyDictionary<string, string>? saved)
    {
        var map = new ShortcutMap();
        if (saved is null)
            return map;
        foreach (var (name, text) in saved)
        {
            if (!Enum.TryParse<ShortcutAction>(name, out var action) || !Enum.IsDefined(action))
                continue;
            if (string.IsNullOrWhiteSpace(text))
                map[action] = default;
            else if (KeyCombo.TryParse(text, out var combo))
                map[action] = combo;
        }
        return map;
    }

    /// <summary>Solo le differenze dai predefiniti, nel formato salvato in <see cref="AppSettings.Shortcuts"/>.</summary>
    public Dictionary<string, string> ToSettings()
    {
        var result = new Dictionary<string, string>();
        foreach (var (action, combo) in _map)
            if (!Defaults.TryGetValue(action, out var def) || def != combo)
                result[action.ToString()] = combo.ToString();
        return result;
    }

    /// <summary>Testo per la pagina «Scorciatoie» (F1): le combinazioni in vigore.</summary>
    public string SummaryText()
    {
        var lines = new List<string>
        {
            "Scorciatoie dell'applicazione (si possono cambiare in Impostazioni, pagina «Scorciatoie»):",
            "",
        };
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var combo = this[action];
            lines.Add($"  {(combo.IsEmpty ? "(disattivata)" : combo.ToString()),-22} {Describe(action)}");
        }
        lines.AddRange(
        [
            "",
            "Fisse nella finestra principale:",
            "  F1                     Questa pagina",
            "  F5                     Riconnetti la sessione",
            "  F11                    Schermo intero",
            "  Ctrl+F4                Chiudi la scheda",
            "  Ctrl+Tab               Scheda successiva",
            "  Ctrl+Maiusc+Tab        Scheda precedente",
            "  Ctrl+Maiusc+D          Stacca la sessione in una finestra",
            "  Ctrl+F                 Cerca una connessione",
            "  Ctrl+N                 Nuova connessione",
        ]);
        return string.Join('\n', lines);
    }
}
