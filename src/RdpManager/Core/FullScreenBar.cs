using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>
/// Lato dello schermo della barra a schermo intero. In alto non c'è: la parte alta dello schermo resta libera
/// (dalla 1.5); un valore «Top» o sconosciuto letto da un file torna a <see cref="Left"/>.
/// </summary>
public enum BarPosition
{
    Left,
    Right,
    Bottom,
}

/// <summary>Come si mostra la barra a schermo intero.</summary>
public enum BarMode
{
    /// <summary>Sempre visibile; la sessione occupa lo spazio che resta, senza sovrapporsi.</summary>
    Fixed,
    /// <summary>Nascosta; compare sopra la sessione tenendo il mouse sul bordo del suo lato per il ritardo scelto.</summary>
    AutoReveal,
    /// <summary>Nascosta; compare solo con la scorciatoia della barra laterale.</summary>
    Hidden,
}

/// <summary>Sezioni che si possono mettere nella barra, nell'ordine scelto dall'utente.</summary>
public enum BarSection
{
    /// <summary>Le sessioni aperte per passare dall'una all'altra (era la barra in alto fino alla 1.4).</summary>
    ConnectedSessions,
    Favorites,
    /// <summary>Tutte le connessioni, con una casella di ricerca.</summary>
    AllConnections,
    Workspaces,
    /// <summary>Pulsanti delle stesse azioni delle scorciatoie.</summary>
    QuickButtons,
    QuickTexts,
    /// <summary>Ctrl+Alt+Canc, foto della sessione e monitor successivo, sulla sessione attiva (dopo la 1.5.0).</summary>
    SessionActions,
}

/// <summary>Azioni della sezione «Azioni sulla sessione» (erano nella barra in alto fino alla 1.4).</summary>
public enum SessionBarAction
{
    SendCtrlAltDel,
    Snapshot,
    NextMonitor,
}

/// <summary>Aspetto delle sessioni nella sezione «Sessioni connesse».</summary>
public enum SessionListStyle
{
    /// <summary>Miniatura della sessione sopra il nome.</summary>
    Thumbnails,
    /// <summary>Una riga per sessione.</summary>
    Compact,
}

/// <summary>Una sezione della barra e se è mostrata.</summary>
public sealed class BarSectionSetting
{
    [JsonConverter(typeof(LenientEnumConverter<BarSection>))]
    public BarSection Kind { get; set; }

    public bool Visible { get; set; } = true;

    public BarSectionSetting()
    {
    }

    public BarSectionSetting(BarSection kind, bool visible = true)
    {
        Kind = kind;
        Visible = visible;
    }
}

/// <summary>Barra laterale a schermo intero (dalla 1.5), salvata nelle impostazioni dell'app.</summary>
public sealed class FullScreenBarSettings
{
    public const int DefaultWidth = 260;
    public const int MinWidth = 120;
    public const int MaxWidth = 600;
    public const int DefaultRevealDelayMs = 300;
    public const int MaxRevealDelayMs = 3000;

    [JsonConverter(typeof(LenientEnumConverter<BarPosition>))]
    public BarPosition Position { get; set; } = BarPosition.Left;

    [JsonConverter(typeof(LenientEnumConverter<BarMode>))]
    public BarMode Mode { get; set; } = BarMode.Fixed;

    /// <summary>Larghezza in pixel (a sinistra e a destra) o altezza (in basso).</summary>
    public int Width { get; set; } = DefaultWidth;

    /// <summary>Per quanto il mouse deve restare sul bordo prima che la barra a comparsa si apra.</summary>
    public int RevealDelayMs { get; set; } = DefaultRevealDelayMs;

    [JsonConverter(typeof(LenientEnumConverter<SessionListStyle>))]
    public SessionListStyle SessionStyle { get; set; } = SessionListStyle.Compact;

    /// <summary>Tutte le sezioni, nell'ordine scelto; quelle con Visible falso non si mostrano.</summary>
    public List<BarSectionSetting> Sections { get; set; } = DefaultSections();

    /// <summary>Ordine predefinito: le sessioni connesse in cima, come la vecchia barra in alto.</summary>
    public static List<BarSectionSetting> DefaultSections() =>
    [
        new(BarSection.ConnectedSessions), new(BarSection.Favorites), new(BarSection.AllConnections),
        new(BarSection.Workspaces), new(BarSection.QuickButtons), new(BarSection.SessionActions), new(BarSection.QuickTexts),
    ];

    /// <summary>
    /// Sezioni aggiunte dopo la 1.5.0: chi ha già la barra salvata le trova mostrate (subito dopo i pulsanti
    /// rapidi) invece che nascoste in fondo. Se poi le nasconde, restano nascoste.
    /// </summary>
    private static readonly BarSection[] AddedVisibleAfter150 = [BarSection.SessionActions];

    /// <summary>
    /// Barra per chi arriva dalla 1.1-1.4 (impostazioni senza questo campo): a sinistra, fissa o a comparsa come
    /// la barra laterale di prima; la barra di passaggio che stava in alto diventa la sezione «Sessioni connesse»
    /// in cima, i suoi comandi la sezione dei pulsanti rapidi.
    /// </summary>
    public static FullScreenBarSettings FromLegacy(SidebarMode sidebar) => new()
    {
        Position = BarPosition.Left,
        Mode = sidebar == SidebarMode.AutoHide ? BarMode.AutoReveal : BarMode.Fixed,
    };

    /// <summary>Le sezioni da mostrare, nell'ordine scelto.</summary>
    public IReadOnlyList<BarSection> VisibleSections => Sections.Where(s => s.Visible).Select(s => s.Kind).ToList();

    /// <summary>Spessore effettivo: larghezza (o altezza in basso) entro i limiti.</summary>
    public int Thickness => Math.Clamp(Width, MinWidth, MaxWidth);

    /// <summary>
    /// Ripara i valori: posizione in alto o sconosciuta → sinistra, misure entro i limiti, sezioni senza doppioni né
    /// sconosciute; quelle mancanti (es. aggiunte in una versione successiva) si aggiungono in fondo, nascoste.
    /// </summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Position))
            Position = BarPosition.Left;
        if (!Enum.IsDefined(Mode))
            Mode = BarMode.Fixed;
        if (!Enum.IsDefined(SessionStyle))
            SessionStyle = SessionListStyle.Compact;
        Width = Math.Clamp(Width, MinWidth, MaxWidth);
        RevealDelayMs = Math.Clamp(RevealDelayMs, 0, MaxRevealDelayMs);
        var seen = new HashSet<BarSection>();
        var sections = new List<BarSectionSetting>();
        foreach (var s in Sections ?? [])
            if (s is not null && Enum.IsDefined(s.Kind) && seen.Add(s.Kind))
                sections.Add(new BarSectionSetting(s.Kind, s.Visible));
        if (sections.Count == 0)
            sections = DefaultSections();
        foreach (var kind in Enum.GetValues<BarSection>().Where(k => !seen.Contains(k) && sections.All(s => s.Kind != k)))
        {
            if (AddedVisibleAfter150.Contains(kind))
            {
                int quick = sections.FindIndex(s => s.Kind == BarSection.QuickButtons);
                sections.Insert(quick >= 0 ? quick + 1 : sections.Count, new BarSectionSetting(kind));
            }
            else
            {
                sections.Add(new BarSectionSetting(kind, visible: false));
            }
        }
        Sections = sections;
    }

    /// <summary>Sposta di un posto (su = -1, giù = +1) la sezione; restituisce il nuovo indice.</summary>
    public int MoveSection(BarSection kind, int delta)
    {
        int i = Sections.FindIndex(s => s.Kind == kind);
        if (i < 0)
            return -1;
        int j = Math.Clamp(i + delta, 0, Sections.Count - 1);
        if (j != i)
        {
            var item = Sections[i];
            Sections.RemoveAt(i);
            Sections.Insert(j, item);
        }
        return j;
    }

    public FullScreenBarSettings Clone()
    {
        var copy = (FullScreenBarSettings)MemberwiseClone();
        copy.Sections = (Sections ?? []).Where(s => s is not null).Select(s => new BarSectionSetting(s.Kind, s.Visible)).ToList();
        return copy;
    }

    /// <summary>Nomi italiani delle sezioni per le impostazioni e i titoli nella barra.</summary>
    public static string Describe(BarSection section) => section switch
    {
        BarSection.ConnectedSessions => "Sessioni connesse",
        BarSection.Favorites => "Preferiti",
        BarSection.AllConnections => "Tutte le connessioni (con ricerca)",
        BarSection.Workspaces => "Spazi di lavoro",
        BarSection.QuickButtons => "Pulsanti rapidi",
        BarSection.QuickTexts => "Testi rapidi",
        BarSection.SessionActions => "Azioni sulla sessione",
        _ => section.ToString(),
    };

    public static string Describe(SessionBarAction action) => action switch
    {
        SessionBarAction.SendCtrlAltDel => "Invia Ctrl+Alt+Canc",
        SessionBarAction.Snapshot => "Foto della sessione",
        SessionBarAction.NextMonitor => "Monitor successivo",
        _ => action.ToString(),
    };

    /// <summary>Titolo breve della sezione nella barra.</summary>
    public static string Title(BarSection section) => section switch
    {
        BarSection.AllConnections => "Connessioni",
        _ => Describe(section),
    };

    public static string Describe(BarPosition position) => position switch
    {
        BarPosition.Left => "A sinistra",
        BarPosition.Right => "A destra",
        BarPosition.Bottom => "In basso",
        _ => position.ToString(),
    };

    public static string Describe(BarMode mode) => mode switch
    {
        BarMode.Fixed => "Fissa (la sessione si restringe)",
        BarMode.AutoReveal => "A comparsa dal bordo",
        BarMode.Hidden => "Nascosta (solo con la scorciatoia)",
        _ => mode.ToString(),
    };
}

/// <summary>Geometria dello schermo intero con la barra: dove stanno la sessione e la barra.</summary>
public static class FullScreenBarLayout
{
    /// <summary>Spessore della striscia sul bordo che apre la barra a comparsa.</summary>
    public const int EdgeThickness = 2;

    /// <param name="Session">Area della sessione.</param>
    /// <param name="Bar">Area della barra; vuota se la barra non si vede.</param>
    /// <param name="Overlay">Vero se la barra sta sopra la sessione (a comparsa o richiamata) invece che accanto.</param>
    public readonly record struct Result(Rectangle Session, Rectangle Bar, bool Overlay);

    /// <summary>
    /// Disposizione dentro <paramref name="area"/>. Barra fissa visibile: la sessione prende solo lo spazio che
    /// resta (mai sovrapposte). Barra a comparsa o nascosta: la sessione prende tutto e la barra, se aperta, sta
    /// sopra. Niente mai in alto: la barra sta a sinistra, a destra o in basso.
    /// </summary>
    public static Result Compute(Rectangle area, FullScreenBarSettings settings, bool barShown)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!barShown)
            return new Result(area, Rectangle.Empty, false);
        var position = Enum.IsDefined(settings.Position) ? settings.Position : BarPosition.Left;
        bool vertical = position != BarPosition.Bottom;
        int thickness = Math.Min(settings.Thickness, Math.Max(0, (vertical ? area.Width : area.Height) / 2));
        var bar = position switch
        {
            BarPosition.Right => new Rectangle(area.Right - thickness, area.Top, thickness, area.Height),
            BarPosition.Bottom => new Rectangle(area.Left, area.Bottom - thickness, area.Width, thickness),
            _ => new Rectangle(area.Left, area.Top, thickness, area.Height),
        };
        if (settings.Mode != BarMode.Fixed)
            return new Result(area, bar, true);
        var session = position switch
        {
            BarPosition.Right => new Rectangle(area.Left, area.Top, area.Width - thickness, area.Height),
            BarPosition.Bottom => new Rectangle(area.Left, area.Top, area.Width, area.Height - thickness),
            _ => new Rectangle(area.Left + thickness, area.Top, area.Width - thickness, area.Height),
        };
        return new Result(session, bar, false);
    }

    /// <summary>Vero se il punto (coordinate dello schermo) sta sul bordo di <paramref name="area"/> dove si apre la barra.</summary>
    public static bool IsOnRevealEdge(Point p, Rectangle area, BarPosition position)
    {
        if (!area.Contains(p))
            return false;
        return position switch
        {
            BarPosition.Right => p.X >= area.Right - EdgeThickness,
            BarPosition.Bottom => p.Y >= area.Bottom - EdgeThickness,
            _ => p.X < area.Left + EdgeThickness,
        };
    }

    /// <summary>
    /// Elenco della sezione «Tutte le connessioni»: con la ricerca vuota tutte nell'ordine dato, altrimenti solo
    /// quelle che corrispondono (nome, indirizzo o etichetta), dalle più pertinenti.
    /// </summary>
    public static IReadOnlyList<RdpConnection> FilterConnections(string? query, IEnumerable<RdpConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (string.IsNullOrWhiteSpace(query))
            return connections.ToList();
        return connections
            .Select(c => (Connection: c, Score: FuzzySearch.Score(query, [c.DisplayName, c.Host, c.Label])))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Connection.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => r.Connection)
            .ToList();
    }

    /// <summary>
    /// Azioni dei pulsanti rapidi: le stesse delle scorciatoie configurabili, tranne «Vai alla sessione N» (le
    /// sessioni sono già nella sezione delle sessioni connesse).
    /// </summary>
    public static IReadOnlyList<ShortcutAction> QuickActions { get; } =
        Enum.GetValues<ShortcutAction>().Where(a => ShortcutMap.SessionNumber(a) is null).ToList();

    /// <summary>Azioni sulla sessione attiva: le funzioni della 1.1 che stavano nella barra in alto.</summary>
    public static IReadOnlyList<SessionBarAction> SessionActions { get; } = Enum.GetValues<SessionBarAction>();

    /// <summary>
    /// Disposizione di un pulsante con nome e scorciatoia: su una riga se ci stanno affiancati, altrimenti su due
    /// (nome sopra, scorciatoia sotto a destra). Il nome che non ci sta comunque si tronca con «…». Mai sovrapposti.
    /// </summary>
    /// <param name="width">Larghezza del pulsante.</param>
    /// <param name="nameWidth">Larghezza del nome per intero.</param>
    /// <param name="shortcutWidth">Larghezza della scorciatoia (0 = nessuna).</param>
    /// <param name="lineHeight">Altezza di una riga di testo.</param>
    public static ActionButtonLayout LayoutActionButton(int width, int nameWidth, int shortcutWidth, int lineHeight)
    {
        const int pad = 6, gap = 10, oneLineHeight = 28;
        int inner = Math.Max(0, width - 2 * pad);
        if (shortcutWidth <= 0)
            return new(new Rectangle(pad, 0, inner, oneLineHeight), Rectangle.Empty, oneLineHeight, false);
        if (nameWidth + gap + shortcutWidth <= inner)
        {
            var sc = new Rectangle(width - pad - shortcutWidth, 0, shortcutWidth, oneLineHeight);
            return new(new Rectangle(pad, 0, inner - shortcutWidth - gap, oneLineHeight), sc, oneLineHeight, false);
        }
        int h = 2 * lineHeight + 8;
        int scw = Math.Min(shortcutWidth, inner);
        return new(new Rectangle(pad, 3, inner, lineHeight), new Rectangle(width - pad - scw, 4 + lineHeight, scw, lineHeight), h, true);
    }
}

/// <summary>Dove disegnare nome e scorciatoia di un pulsante della barra, e quanto è alto.</summary>
public readonly record struct ActionButtonLayout(Rectangle Name, Rectangle Shortcut, int Height, bool TwoLines);

/// <summary>
/// Legge un enum per nome o numero senza far fallire tutto il file: un valore sconosciuto (per esempio «Top» per la
/// posizione della barra) diventa un valore fuori dall'enum, che Normalize poi ripara.
/// </summary>
public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly TEnum Unknown = (TEnum)Enum.ToObject(typeof(TEnum), -1);

    /// <summary>Anche null arriva qui (diventa sconosciuto) invece di far fallire la lettura.</summary>
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                return Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
                       && !int.TryParse(text, out _)
                    ? value
                    : Unknown;
            case JsonTokenType.Number when reader.TryGetInt32(out int n):
                var number = (TEnum)Enum.ToObject(typeof(TEnum), n);
                return Enum.IsDefined(number) ? number : Unknown;
            default:
                reader.Skip();
                return Unknown;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : "");
}
