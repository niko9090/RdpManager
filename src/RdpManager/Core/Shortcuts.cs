using System.Windows.Forms;

namespace RdpManager.Core;

/// <summary>Azioni raggiungibili da tastiera anche col fuoco nella sessione remota.</summary>
public enum ShortcutAction
{
    NextSession,
    PreviousSession,
    Session1, Session2, Session3, Session4, Session5, Session6, Session7, Session8, Session9,
    ToggleFullScreen,
    ToggleSidebar,
    // Dalla 1.2
    Overview,
    QuickSearch,
    ToggleSplitView,
}

/// <summary>Una combinazione di tasti: modificatori Ctrl/Alt/Maiusc più un tasto.</summary>
public readonly record struct KeyCombo(bool Ctrl, bool Alt, bool Shift, Keys Key)
{
    /// <summary>Nomi italiani usati in formattazione (il primo) e accettati in lettura insieme ai nomi inglesi.</summary>
    private static readonly (Keys Key, string[] Names)[] KeyNames =
    [
        (Keys.Right, ["Destra", "Right", "→"]),
        (Keys.Left, ["Sinistra", "Left", "←"]),
        (Keys.Up, ["Su", "Up", "↑"]),
        (Keys.Down, ["Giù", "Giu", "Down", "↓"]),
        (Keys.Enter, ["Invio", "Enter", "Return"]),
        (Keys.Space, ["Spazio", "Space"]),
        (Keys.Tab, ["Tab"]),
        (Keys.Escape, ["Esc", "Escape"]),
        (Keys.Back, ["Backspace", "Back"]),
        (Keys.Delete, ["Canc", "Del", "Delete"]),
        (Keys.Insert, ["Ins", "Insert"]),
        (Keys.Home, ["Home", "Inizio"]),
        (Keys.End, ["Fine", "End"]),
        (Keys.PageUp, ["PagSu", "PageUp", "PgUp"]),
        (Keys.PageDown, ["PagGiù", "PagGiu", "PageDown", "PgDn"]),
        (Keys.Pause, ["Pausa", "Pause", "Break"]),
    ];

    public bool IsEmpty => Key == Keys.None;

    /// <summary>Vero se ha almeno Ctrl o Alt: una scorciatoia globale senza questi rovinerebbe la digitazione.</summary>
    public bool HasCommandModifier => Ctrl || Alt;

    /// <summary>"Ctrl+Alt+Destra".</summary>
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

    public static string KeyName(Keys key)
    {
        foreach (var (k, names) in KeyNames)
            if (k == key)
                return names[0];
        if (key is >= Keys.D0 and <= Keys.D9)
            return ((char)('0' + (key - Keys.D0))).ToString();
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
            return "Num" + (key - Keys.NumPad0);
        return key.ToString();
    }

    /// <summary>
    /// Legge "Ctrl+Alt+Destra", "ctrl + alt + right", "Ctrl+Alt+1", "Ctrl+Maiusc+F5"...
    /// Modificatori: Ctrl/Control, Alt, Maiusc/Shift. Serve esattamente un tasto non modificatore.
    /// </summary>
    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        bool ctrl = false, alt = false, shift = false;
        Keys? key = null;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        // "Ctrl++" non è supportato: un token vuoto è un errore.
        foreach (var token in tokens)
        {
            if (token.Length == 0)
                return false;
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl":
                    ctrl = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "maiusc" or "shift":
                    shift = true;
                    continue;
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

    public static KeyCombo Parse(string text) =>
        TryParse(text, out var combo) ? combo : throw new FormatException($"Combinazione di tasti non valida: «{text}».");

    public static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        foreach (var (k, names) in KeyNames)
            if (names.Any(n => n.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                key = k;
                return true;
            }
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            key = Keys.A + (char.ToUpperInvariant(token[0]) - 'A');
            return true;
        }
        if (token.Length == 4 && token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(token[3]))
        {
            key = Keys.NumPad0 + (token[3] - '0');
            return true;
        }
        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f') && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
        {
            key = Keys.F1 + (f - 1);
            return true;
        }
        // Ripiego sui nomi dell'enum, gli stessi che KeyName produce per i tasti senza nome italiano
        // (es. "OemPeriod", "Oem3", "Multiply"): solo nomi di un singolo tasto, non numeri né modificatori.
        if (char.IsAsciiLetter(token[0]) && !token.Contains(',')
            && Enum.TryParse<Keys>(token, ignoreCase: true, out var named)
            && Enum.IsDefined(named) && (named & ~Keys.KeyCode) == 0 && named != Keys.None
            && named is not (Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu
                or Keys.RMenu or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LWin or Keys.RWin))
        {
            key = named;
            return true;
        }
        return false;
    }

    /// <summary>Da un valore WinForms con modificatori (KeyEventArgs.KeyData, ProcessCmdKey).</summary>
    public static KeyCombo FromKeys(Keys keyData) =>
        new((keyData & Keys.Control) != 0, (keyData & Keys.Alt) != 0, (keyData & Keys.Shift) != 0,
            NormalizeKey(keyData & Keys.KeyCode));

    /// <summary>Da un hook di tastiera: codice del tasto virtuale e stato dei modificatori.</summary>
    public static KeyCombo FromVirtualKey(int virtualKey, bool ctrl, bool alt, bool shift) =>
        new(ctrl, alt, shift, NormalizeKey((Keys)virtualKey & Keys.KeyCode));

    public Keys ToKeys() =>
        Key | (Ctrl ? Keys.Control : 0) | (Alt ? Keys.Alt : 0) | (Shift ? Keys.Shift : 0);

    /// <summary>Il tasto stesso di un modificatore (es. LControlKey) non conta come tasto; Return e Enter coincidono.</summary>
    private static Keys NormalizeKey(Keys key) => key switch
    {
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LWin or Keys.RWin => Keys.None,
        _ => key,
    };
}

/// <summary>Associazione azione → combinazione, con valori predefiniti e controllo dei conflitti.</summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<ShortcutAction, KeyCombo> _map;

    public ShortcutMap(IReadOnlyDictionary<ShortcutAction, KeyCombo>? map = null)
    {
        _map = new Dictionary<ShortcutAction, KeyCombo>(map ?? Defaults);
    }

    public static IReadOnlyDictionary<ShortcutAction, KeyCombo> Defaults { get; } = BuildDefaults();

    private static Dictionary<ShortcutAction, KeyCombo> BuildDefaults()
    {
        var d = new Dictionary<ShortcutAction, KeyCombo>
        {
            [ShortcutAction.NextSession] = new(true, true, false, Keys.Right),
            [ShortcutAction.PreviousSession] = new(true, true, false, Keys.Left),
            [ShortcutAction.ToggleFullScreen] = new(true, true, false, Keys.Enter),
            [ShortcutAction.ToggleSidebar] = new(true, true, false, Keys.B),
            [ShortcutAction.Overview] = new(true, true, false, Keys.G),
            [ShortcutAction.QuickSearch] = new(true, true, false, Keys.Space),
            [ShortcutAction.ToggleSplitView] = new(true, true, false, Keys.S),
        };
        for (int n = 1; n <= 9; n++)
            d[SessionAction(n)] = new(true, true, false, Keys.D0 + n);
        return d;
    }

    public static ShortcutAction SessionAction(int number) =>
        number is >= 1 and <= 9 ? ShortcutAction.Session1 + (number - 1) : throw new ArgumentOutOfRangeException(nameof(number));

    /// <summary>1..9 per le azioni SessionN, altrimenti null.</summary>
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

        _ when SessionNumber(action) is int n => $"Vai alla sessione {n}",
        _ => action.ToString(),
    };

    /// <summary>Combinazione dell'azione; vuota se l'azione è disattivata.</summary>
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

    public bool TryGetAction(Keys keyData, out ShortcutAction action) => TryGetAction(KeyCombo.FromKeys(keyData), out action);

    /// <summary>Azioni con la stessa combinazione (da segnalare nelle impostazioni).</summary>
    public IReadOnlyList<IReadOnlyList<ShortcutAction>> Conflicts() =>
        _map.Where(kv => !kv.Value.IsEmpty)
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<ShortcutAction>)g.Select(kv => kv.Key).OrderBy(a => a).ToList())
            .ToList();

    /// <summary>
    /// Dalle impostazioni salvate: le azioni assenti, sconosciute o con combinazioni non valide tengono il predefinito;
    /// una stringa vuota disattiva l'azione.
    /// </summary>
    public static ShortcutMap FromSettings(IReadOnlyDictionary<string, string>? saved)
    {
        var map = new ShortcutMap();
        if (saved is null)
            return map;
        foreach (var (name, text) in saved)
        {
            if (!Enum.TryParse<ShortcutAction>(name, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                continue;
            if (string.IsNullOrWhiteSpace(text))
                map[action] = default;
            else if (KeyCombo.TryParse(text, out var combo) && combo.HasCommandModifier)
                map[action] = combo;
        }
        return map;
    }

    /// <summary>Solo le azioni che differiscono dal predefinito, così i predefiniti futuri arrivano a tutti.</summary>
    public Dictionary<string, string> ToSettings() =>
        _map.Where(kv => !Defaults.TryGetValue(kv.Key, out var d) || d != kv.Value)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());
}

/// <summary>Passaggio fra le sessioni aperte, in ordine di scheda, saltando quelle non connesse.</summary>
public static class SessionCycler
{
    /// <summary>
    /// Indice della prossima sessione connessa dopo (o prima di) <paramref name="current"/>, ricominciando dall'altro capo.
    /// Se nessuna è connessa restituisce -1; se è connessa solo quella corrente restituisce quella.
    /// <paramref name="current"/> fuori dall'elenco (es. -1) parte dall'inizio o dalla fine.
    /// </summary>
    public static int Next(IReadOnlyList<bool> connected, int current, bool forward = true)
    {
        ArgumentNullException.ThrowIfNull(connected);
        int n = connected.Count;
        if (n == 0)
            return -1;
        int start = current >= 0 && current < n ? current : (forward ? -1 : n);
        for (int step = 1; step <= n; step++)
        {
            int i = ((start + (forward ? step : -step)) % n + n) % n;
            if (connected[i])
                return i;
        }
        return -1;
    }

    public static int Previous(IReadOnlyList<bool> connected, int current) => Next(connected, current, forward: false);

    /// <summary>Indice della <paramref name="number"/>-esima sessione connessa (da 1), oppure -1.</summary>
    public static int Nth(IReadOnlyList<bool> connected, int number)
    {
        ArgumentNullException.ThrowIfNull(connected);
        if (number < 1)
            return -1;
        for (int i = 0, seen = 0; i < connected.Count; i++)
            if (connected[i] && ++seen == number)
                return i;
        return -1;
    }

    /// <summary>Indice dell'azione di scorciatoia, oppure null se l'azione non riguarda il cambio di sessione o non c'è dove andare.</summary>
    public static int? Target(ShortcutAction action, IReadOnlyList<bool> connected, int current)
    {
        int index = action switch
        {
            ShortcutAction.NextSession => Next(connected, current, forward: true),
            ShortcutAction.PreviousSession => Next(connected, current, forward: false),
            _ when ShortcutMap.SessionNumber(action) is int n => Nth(connected, n),
            _ => -1,
        };
        return index < 0 ? null : index;
    }
}
