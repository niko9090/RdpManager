namespace RdpManager.Core;

/// <summary>
/// Scorciatoia globale per richiamare RdpManager da qualunque programma, registrata con RegisterHotKey.
/// A differenza delle scorciatoie delle sessioni (hook attivo solo con l'app in primo piano) vale ovunque,
/// quindi serve una combinazione che non si usa per altro: predefinita Ctrl+Alt+Maiusc+R.
/// </summary>
public static class GlobalHotkey
{
    public const string DefaultText = "Ctrl+Alt+Maiusc+R";

    // Valori di fsModifiers di RegisterHotKey.
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;

    public static KeyCombo Default { get; } = KeyCombo.Parse(DefaultText);

    /// <summary>Accetta solo combinazioni con Ctrl o Alt (una lettera da sola bloccherebbe la digitazione ovunque).</summary>
    public static bool TryParse(string? text, out KeyCombo combo) =>
        KeyCombo.TryParse(text, out combo) && combo.HasCommandModifier;

    /// <summary>Combinazione delle impostazioni: vuota se disattivata, predefinita se non valida.</summary>
    public static KeyCombo FromSettings(string? text) =>
        text is null ? Default
        : text.Trim().Length == 0 ? default
        : TryParse(text, out var combo) ? combo : Default;

    /// <summary>Modificatori e tasto virtuale per RegisterHotKey (con MOD_NOREPEAT).</summary>
    public static (uint Modifiers, uint VirtualKey) ToNative(KeyCombo combo)
    {
        if (combo.IsEmpty)
            throw new ArgumentException("Combinazione vuota.", nameof(combo));
        uint mods = ModNoRepeat;
        if (combo.Ctrl) mods |= ModControl;
        if (combo.Alt) mods |= ModAlt;
        if (combo.Shift) mods |= ModShift;
        return (mods, (uint)combo.Key);
    }

    /// <summary>Azione delle sessioni che ha la stessa combinazione (andrebbe rubata dalla scorciatoia globale), oppure null.</summary>
    public static ShortcutAction? ConflictWith(KeyCombo combo, ShortcutMap map) =>
        !combo.IsEmpty && map.TryGetAction(combo, out var action) ? action : null;
}
