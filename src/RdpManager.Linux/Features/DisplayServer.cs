namespace RdpManager.Linux.Features;

/// <summary>Il server grafico in uso: da questo dipende cosa si può fare (incorporare le sessioni, scorciatoia globale, cattura).</summary>
public enum DisplayServerKind { X11, Wayland, Unknown }

/// <summary>Rileva X11 o Wayland dalle variabili d'ambiente e spiega all'utente cosa cambia.</summary>
public static class DisplayServer
{
    public static DisplayServerKind Detect(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var session = env("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant();
        if (session == "wayland")
            return DisplayServerKind.Wayland;
        if (session == "x11")
            return DisplayServerKind.X11;
        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
            return DisplayServerKind.Wayland;
        if (!string.IsNullOrEmpty(env("DISPLAY")))
            return DisplayServerKind.X11;
        return DisplayServerKind.Unknown;
    }

    public static DisplayServerKind Current { get; set; } = Detect();

    /// <summary>
    /// Frase per l'utente sul modo in cui si aprono le sessioni; null se non c'è niente da dire.
    /// <paramref name="embedded"/>: la sessione è dentro la scheda (possibile solo su X11, anche via XWayland).
    /// </summary>
    public static string? SessionNote(DisplayServerKind kind, bool embedded) => kind switch
    {
        _ when embedded => null,
        DisplayServerKind.Wayland =>
            "Stai usando Wayland: le sessioni si aprono in una finestra separata e non dentro la scheda. "
            + "L'incorporamento nella scheda funziona solo su X11 (per averlo, scegli «Xorg» o «X11» nella schermata di accesso).",
        DisplayServerKind.X11 => "Questa sessione è in una finestra separata (client senza incorporamento o più monitor).",
        _ => "Non riesco a capire se il desktop è X11 o Wayland: la sessione si apre in una finestra separata.",
    };

    /// <summary>La scorciatoia globale dipende dal desktop: spiegazione per le Impostazioni.</summary>
    public static string GlobalHotkeyNote(DisplayServerKind kind) => kind switch
    {
        DisplayServerKind.X11 => "La scorciatoia globale è attiva su questo desktop (X11).",
        DisplayServerKind.Wayland => "Su Wayland il sistema non permette a un programma di intercettare i tasti globali: la scorciatoia globale non è disponibile. Dentro la finestra le scorciatoie funzionano.",
        _ => "Non so se questo desktop permette le scorciatoie globali: se non funziona, usa quelle dentro la finestra.",
    };

    public static string TrayNote(DisplayServerKind kind) =>
        "L'icona nell'area di notifica dipende dal desktop: GNOME richiede l'estensione «AppIndicator». Se non compare, il programma continua a funzionare normalmente"
        + (kind == DisplayServerKind.Wayland ? " (e chiudere la finestra chiude il programma)." : ".");

    public static bool CanGlobalHotkey(DisplayServerKind kind) => kind == DisplayServerKind.X11;
}
