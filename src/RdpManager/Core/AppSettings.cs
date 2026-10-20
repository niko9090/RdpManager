namespace RdpManager.Core;

public enum SidebarMode
{
    /// <summary>Barra laterale sempre visibile accanto alle sessioni.</summary>
    Pinned,
    /// <summary>Barra laterale nascosta, compare avvicinando il mouse al bordo sinistro.</summary>
    AutoHide,
}

/// <summary>Impostazioni dell'app, salvate in connections.json insieme alle connessioni.</summary>
public sealed class AppSettings
{
    /// <summary>Nome di dispositivo del monitor scelto (Screen.DeviceName, es. "\.\DISPLAY2"); null = principale.</summary>
    public string? PreferredMonitor { get; set; }

    public SidebarMode SidebarMode { get; set; } = SidebarMode.Pinned;

    /// <summary>Alla chiusura si ricordano le sessioni aperte e all'avvio si riaprono.</summary>
    public bool ReopenSessionsOnStartup { get; set; }

    /// <summary>Id delle connessioni aperte all'ultima chiusura, nell'ordine delle schede.</summary>
    public List<Guid> OpenSessions { get; set; } = [];

    /// <summary>
    /// Scorciatoie personalizzate: nome di <see cref="ShortcutAction"/> → combinazione ("Ctrl+Alt+Destra").
    /// Le azioni assenti usano i valori predefiniti; vedi <see cref="GetShortcuts"/>.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    public ShortcutMap GetShortcuts() => ShortcutMap.FromSettings(Shortcuts);

    public void SetShortcuts(ShortcutMap map) => Shortcuts = map.ToSettings();

    /// <summary>Ripara i valori mancanti o non validi dopo il caricamento.</summary>
    public void Normalize()
    {
        OpenSessions ??= [];
        OpenSessions = OpenSessions.Where(id => id != Guid.Empty).Distinct().ToList();
        Shortcuts ??= [];
        if (string.IsNullOrWhiteSpace(PreferredMonitor))
            PreferredMonitor = null;
        if (!Enum.IsDefined(SidebarMode))
            SidebarMode = SidebarMode.Pinned;
    }
}
