namespace RdpManager.Core;

/// <summary>
/// Regola di validità delle scorciatoie scritte come testo ("Ctrl+Alt+Maiusc+R") che i modelli (impostazioni, spazi di
/// lavoro) usano per ripulire i dati letti dal file. I nomi dei tasti sono quelli della piattaforma: il controllo
/// preciso lo registra l'applicazione (<see cref="Validator"/>); quello di ripiego controlla solo la forma.
/// </summary>
public static class HotkeyRules
{
    /// <summary>Scorciatoia globale predefinita per richiamare l'app.</summary>
    public const string DefaultText = "Ctrl+Alt+Maiusc+R";

    private static readonly string[] Modifiers = ["ctrl", "control", "ctl", "alt", "maiusc", "shift"];

    /// <summary>Da sostituire all'avvio con il controllo della piattaforma (vero se la combinazione è accettabile).</summary>
    public static Func<string, bool> Validator { get; set; } = StructuralCheck;

    public static bool IsValid(string? text) => !string.IsNullOrWhiteSpace(text) && Validator(text);

    /// <summary>Forma valida: Ctrl o Alt più esattamente un tasto non modificatore.</summary>
    private static bool StructuralCheck(string text)
    {
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Any(t => t.Length == 0))
            return false;
        var lower = tokens.Select(t => t.ToLowerInvariant()).ToList();
        var hasCommand = lower.Any(t => t is "ctrl" or "control" or "ctl" or "alt");
        return hasCommand && lower.Count(t => !Modifiers.Contains(t)) == 1;
    }
}