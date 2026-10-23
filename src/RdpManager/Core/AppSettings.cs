namespace RdpManager.Core;

/// <summary>Tema dei colori dell'interfaccia.</summary>
public enum AppTheme
{
    /// <summary>Segue l'impostazione di Windows «Modalità app» (chiaro o scuro).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Come si aggiorna l'app.</summary>
public enum UpdateMode
{
    /// <summary>Controllo all'avvio e ogni 24 ore, scaricamento e verifica in sottofondo, installazione dopo la conferma.</summary>
    AutomaticWithConfirmation,
    /// <summary>Solo un avviso nella barra di stato; si aggiorna dal menu Aiuto.</summary>
    NotifyOnly,
    /// <summary>Nessun controllo automatico (resta «Aiuto → Controlla aggiornamenti»).</summary>
    Off,
}

/// <summary>Ordine delle connessioni nell'elenco.</summary>
public enum ConnectionOrder
{
    /// <summary>Per nome.</summary>
    Alphabetical,
    /// <summary>Quello scelto trascinando le connessioni (ordine del file).</summary>
    Manual,
}

/// <summary>Barra laterale della finestra (fuori dallo schermo intero, dove vale <see cref="FullScreenBarSettings"/>).</summary>
public enum SidebarMode
{
    /// <summary>Barra laterale sempre visibile accanto alle sessioni.</summary>
    Pinned,
    /// <summary>Barra laterale nascosta, compare avvicinando il mouse al bordo sinistro.</summary>
    AutoHide,
}

/// <summary>Limiti della barra laterale della finestra, ridimensionabile trascinandone il bordo (dalla 1.5.1).</summary>
public static class WindowSidebar
{
    public const int DefaultWidth = 280;
    public const int MinWidth = 180;
    public const int MaxWidth = 600;

    /// <summary>Spazio che resta sempre alle sessioni accanto alla barra.</summary>
    public const int MinSessionWidth = 300;

    /// <summary>
    /// Larghezza da usare in una finestra larga <paramref name="available"/>: quella salvata entro i limiti, ridotta se
    /// non lascerebbe <see cref="MinSessionWidth"/> alle sessioni (ma mai sotto <see cref="MinWidth"/>).
    /// </summary>
    public static int Fit(int width, int available)
    {
        int max = Math.Clamp(available - MinSessionWidth, MinWidth, MaxWidth);
        return Math.Clamp(width, MinWidth, max);
    }
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

    // --- Dalla 1.2 ---

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Icona nell'area di notifica con il menu delle connessioni.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Chiudendo la finestra l'app resta nell'area di notifica invece di uscire.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>
    /// Scorciatoia globale (RegisterHotKey) che richiama l'app da qualunque programma; stringa vuota = disattivata.
    /// Serve Ctrl o Alt (vedi <see cref="GlobalHotkey"/>).
    /// </summary>
    public string GlobalHotkey { get; set; } = Core.GlobalHotkey.DefaultText;

    /// <summary>Si annotano inizio, fine, durata e motivo di ogni sessione (history.json).</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>Divisione della vista affiancata: frazione della larghezza per la sessione di sinistra.</summary>
    public double SplitRatio { get; set; } = SplitLayout.DefaultRatio;

    // --- Dalla 1.3 ---

    /// <summary>Aggiornamenti automatici con conferma (predefinito), solo avviso o spenti.</summary>
    public UpdateMode UpdateMode { get; set; } = UpdateMode.AutomaticWithConfirmation;

    /// <summary>Log dettagliato: anche i messaggi di diagnosi, con gli host (mai password).</summary>
    public bool VerboseLog { get; set; }

    public ConnectionOrder ConnectionOrder { get; set; } = ConnectionOrder.Alphabetical;

    /// <summary>
    /// Impostato prima di un aggiornamento con «Aggiorna ora»: al prossimo avvio si riaprono le sessioni di
    /// <see cref="OpenSessions"/> anche se la riapertura all'avvio è spenta, poi torna falso.
    /// </summary>
    public bool ReopenOnceAfterUpdate { get; set; }

    // --- Dalla 1.4 ---

    /// <summary>Impronta PBKDF2 del PIN di blocco (mai il PIN); null = blocco con PIN spento.</summary>
    public PinHash? LockPin { get; set; }

    /// <summary>Blocco dopo questi minuti di inattività (0 = solo a mano); vale solo con il PIN impostato.</summary>
    public int LockAfterMinutes { get; set; } = 10;

    // --- Dalla 1.5 ---

    /// <summary>
    /// Barra a schermo intero (posizione, modalità, misure, sezioni). Assente nei file 1.1-1.4: <see cref="Normalize"/>
    /// la ricava dalla barra laterale di allora (<see cref="FullScreenBarSettings.FromLegacy"/>).
    /// </summary>
    public FullScreenBarSettings? FullScreenBar { get; set; }

    // --- Dalla 1.5.1 ---

    /// <summary>
    /// Larghezza della barra laterale della finestra, scelta trascinandone il bordo. Assente nei file 1.0-1.5:
    /// vale <see cref="WindowSidebar.DefaultWidth"/>, la larghezza fissa di prima.
    /// </summary>
    public int SidebarWidth { get; set; } = WindowSidebar.DefaultWidth;

    /// <summary>La barra a schermo intero, creata dalle impostazioni di prima se manca.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FullScreenBarSettings Bar => FullScreenBar ??= FullScreenBarSettings.FromLegacy(SidebarMode);

    /// <summary>Vero se il blocco con PIN è attivo.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LockEnabled => LockPin is { IsValid: true };

    public ShortcutMap GetShortcuts() => ShortcutMap.FromSettings(Shortcuts);

    /// <summary>
    /// Copia da esportare in un .rdpm: senza ciò che vale solo per questo PC (monitor scelto, sessioni da riaprire).
    /// </summary>
    public AppSettings ForExport()
    {
        var copy = Clone();
        copy.PreferredMonitor = null;
        copy.OpenSessions = [];
        copy.ReopenOnceAfterUpdate = false;
        copy.LockPin = null; // il PIN non esce mai dal PC
        copy.FullScreenBar = Bar.Clone();
        return copy;
    }

    /// <summary>Prende le impostazioni portabili di <paramref name="other"/>; monitor e sessioni da riaprire restano.</summary>
    public void ApplyPortable(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var monitor = PreferredMonitor;
        var open = OpenSessions;
        var src = other.Clone();
        src.Normalize();
        SidebarMode = src.SidebarMode;
        ReopenSessionsOnStartup = src.ReopenSessionsOnStartup;
        Shortcuts = src.Shortcuts;
        Theme = src.Theme;
        ShowTrayIcon = src.ShowTrayIcon;
        CloseToTray = src.CloseToTray;
        GlobalHotkey = src.GlobalHotkey;
        HistoryEnabled = src.HistoryEnabled;
        SplitRatio = src.SplitRatio;
        UpdateMode = src.UpdateMode;
        VerboseLog = src.VerboseLog;
        ConnectionOrder = src.ConnectionOrder;
        LockAfterMinutes = src.LockAfterMinutes;
        SidebarWidth = src.SidebarWidth;
        FullScreenBar = src.Bar.Clone();
        PreferredMonitor = monitor;
        OpenSessions = open;
    }

    /// <summary>
    /// Sessioni da riaprire all'avvio: quelle salvate se la riapertura è attiva o se c'è appena stato un
    /// aggiornamento (in quel caso il segnale si consuma). Lista vuota = nessuna.
    /// </summary>
    public IReadOnlyList<Guid> TakeSessionsToReopen()
    {
        bool once = ReopenOnceAfterUpdate;
        ReopenOnceAfterUpdate = false;
        return ReopenSessionsOnStartup || once ? OpenSessions.ToList() : [];
    }

    /// <summary>Copia indipendente (elenchi compresi), per i dialoghi che si possono annullare.</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.OpenSessions = [.. OpenSessions ?? []];
        copy.Shortcuts = new Dictionary<string, string>(Shortcuts ?? []);
        copy.FullScreenBar = FullScreenBar?.Clone();
        return copy;
    }

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
        if (!Enum.IsDefined(Theme))
            Theme = AppTheme.System;
        // null = file della 1.0/1.1 senza il campo: si usa il predefinito; "" = disattivata dall'utente.
        if (GlobalHotkey is null)
            GlobalHotkey = Core.GlobalHotkey.DefaultText;
        else if (GlobalHotkey.Length > 0 && !Core.GlobalHotkey.TryParse(GlobalHotkey, out _))
            GlobalHotkey = Core.GlobalHotkey.DefaultText;
        SplitRatio = SplitLayout.ClampRatio(SplitRatio);
        if (!Enum.IsDefined(ConnectionOrder))
            ConnectionOrder = ConnectionOrder.Alphabetical;
        if (!Enum.IsDefined(UpdateMode))
            UpdateMode = UpdateMode.AutomaticWithConfirmation;
        if (LockPin is { IsValid: false })
            LockPin = null;
        LockAfterMinutes = Math.Clamp(LockAfterMinutes, 0, 24 * 60);
        SidebarWidth = Math.Clamp(SidebarWidth, WindowSidebar.MinWidth, WindowSidebar.MaxWidth);
        Bar.Normalize();
    }
}
