using System.Collections.ObjectModel;
using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Una sezione della barra a schermo intero: riga con casella «mostrata» nell'elenco delle sezioni.</summary>
public sealed class BarSectionItem : ViewModelBase
{
    private bool _visible;

    public BarSectionItem(BarSection kind, bool visible)
    {
        Kind = kind;
        _visible = visible;
    }

    public BarSection Kind { get; }

    public string Text => FullScreenBarSettings.Describe(Kind);

    public bool Visible { get => _visible; set => Set(ref _visible, value); }
}

/// <summary>
/// Monitor, barra laterale della finestra e barra a schermo intero (blocco B). Come su Windows i controlli stanno in due
/// pagine: la barra laterale in «Aspetto», il monitor e la barra a schermo intero in «Schermo intero».
/// </summary>
public sealed class MonitorPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<string?> _monitor;
    private ChoiceItem<SidebarMode> _sidebar;
    private ChoiceItem<BarPosition> _barPosition;
    private ChoiceItem<BarMode> _barMode;
    private ChoiceItem<SessionListStyle> _barStyle;
    private decimal _barWidth, _barDelay;
    private BarSectionItem? _selectedSection;

    public MonitorPageViewModel(AppSettings settings, IReadOnlyList<ScreenInfo>? screens = null)
    {
        _settings = settings;
        var list = new List<ChoiceItem<string?>> { new(null, "Principale") };
        foreach (var s in screens ?? ScreenProvider.All())
            list.Add(new ChoiceItem<string?>(s.DeviceName, s.DeviceName));
        // Un monitor scelto e ora scollegato resta in elenco, così la scelta non si perde in silenzio.
        if (settings.PreferredMonitor is { } saved && list.All(c => c.Value != saved))
            list.Add(new ChoiceItem<string?>(saved, saved + " (non collegato)"));
        MonitorChoices = list;
        _monitor = list.First(c => c.Value == settings.PreferredMonitor);
        _sidebar = Choices.Pick(SidebarChoices, settings.SidebarMode);
        var bar = settings.Bar;
        _barPosition = Choices.Pick(BarPositionChoices, bar.Position);
        _barMode = Choices.Pick(BarModeChoices, bar.Mode);
        _barStyle = Choices.Pick(BarStyleChoices, bar.SessionStyle);
        _barWidth = bar.Thickness;
        _barDelay = Math.Clamp(bar.RevealDelayMs, 0, FullScreenBarSettings.MaxRevealDelayMs);
        Sections = new ObservableCollection<BarSectionItem>(bar.Sections.Select(s => new BarSectionItem(s.Kind, s.Visible)));
        _selectedSection = Sections.FirstOrDefault();
        MoveUpCommand = new RelayCommand(() => MoveSection(-1));
        MoveDownCommand = new RelayCommand(() => MoveSection(+1));
    }

    public override string Title => "Monitor";

    public IReadOnlyList<ChoiceItem<string?>> MonitorChoices { get; }

    public static IReadOnlyList<ChoiceItem<SidebarMode>> SidebarChoices { get; } =
    [
        new(SidebarMode.Pinned, "Fissa"),
        new(SidebarMode.AutoHide, "A comparsa (bordo sinistro)"),
    ];

    public static IReadOnlyList<ChoiceItem<BarPosition>> BarPositionChoices { get; } =
        Enum.GetValues<BarPosition>().Select(p => new ChoiceItem<BarPosition>(p, FullScreenBarSettings.Describe(p))).ToList();

    public static IReadOnlyList<ChoiceItem<BarMode>> BarModeChoices { get; } =
        Enum.GetValues<BarMode>().Select(m => new ChoiceItem<BarMode>(m, FullScreenBarSettings.Describe(m))).ToList();

    public static IReadOnlyList<ChoiceItem<SessionListStyle>> BarStyleChoices { get; } =
    [
        new(SessionListStyle.Thumbnails, "Con miniatura"),
        new(SessionListStyle.Compact, "Compatte (una riga)"),
    ];

    public static decimal MinBarWidth => FullScreenBarSettings.MinWidth;

    public static decimal MaxBarWidth => FullScreenBarSettings.MaxWidth;

    public static decimal MaxBarDelay => FullScreenBarSettings.MaxRevealDelayMs;

    public ChoiceItem<string?> Monitor { get => _monitor; set { if (value is not null) Set(ref _monitor, value); } }

    public ChoiceItem<SidebarMode> Sidebar
    {
        get => _sidebar;
        set
        {
            if (value is not null && Set(ref _sidebar, value))
            {
                OnPropertyChanged(nameof(SidebarPinned));
                OnPropertyChanged(nameof(SidebarAutoHide));
            }
        }
    }

    /// <summary>Casella «Fissa» della barra laterale.</summary>
    public bool SidebarPinned
    {
        get => _sidebar.Value == SidebarMode.Pinned;
        set { if (value) Sidebar = Choices.Pick(SidebarChoices, SidebarMode.Pinned); }
    }

    /// <summary>Casella «A comparsa» della barra laterale.</summary>
    public bool SidebarAutoHide
    {
        get => _sidebar.Value == SidebarMode.AutoHide;
        set { if (value) Sidebar = Choices.Pick(SidebarChoices, SidebarMode.AutoHide); }
    }

    public ChoiceItem<BarPosition> BarPositionChoice { get => _barPosition; set { if (value is not null) Set(ref _barPosition, value); } }

    public ChoiceItem<BarMode> BarModeChoice
    {
        get => _barMode;
        set
        {
            if (value is not null && Set(ref _barMode, value))
                OnPropertyChanged(nameof(BarDelayEnabled));
        }
    }

    public ChoiceItem<SessionListStyle> BarStyleChoice { get => _barStyle; set { if (value is not null) Set(ref _barStyle, value); } }

    /// <summary>Larghezza in pixel (in basso: altezza).</summary>
    public decimal BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }

    /// <summary>Ritardo di comparsa in millisecondi; vale solo con la barra a comparsa dal bordo.</summary>
    public decimal BarDelay { get => _barDelay; set => Set(ref _barDelay, value); }

    public bool BarDelayEnabled => _barMode.Value == BarMode.AutoReveal;

    public ObservableCollection<BarSectionItem> Sections { get; }

    public BarSectionItem? SelectedSection { get => _selectedSection; set => Set(ref _selectedSection, value); }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    /// <summary>Sposta di un posto la sezione scelta (su = -1, giù = +1).</summary>
    public void MoveSection(int delta)
    {
        if (_selectedSection is null)
            return;
        int i = Sections.IndexOf(_selectedSection);
        int j = Math.Clamp(i + delta, 0, Sections.Count - 1);
        if (i < 0 || j == i)
            return;
        var item = _selectedSection;
        Sections.Move(i, j);
        SelectedSection = item;
    }

    public string Note => "Su quale schermo si apre una sessione a schermo intero e su cui la finestra viene portata all'avvio. "
        + "Con «Sposta la finestra sul monitor successivo» (menu Visualizza) si passa da uno all'altro.";

    public override void Apply()
    {
        _settings.PreferredMonitor = Monitor.Value;
        _settings.SidebarMode = Sidebar.Value;
        _settings.FullScreenBar = new FullScreenBarSettings
        {
            Position = BarPositionChoice.Value,
            Mode = BarModeChoice.Value,
            Width = (int)Math.Clamp(BarWidth, FullScreenBarSettings.MinWidth, FullScreenBarSettings.MaxWidth),
            RevealDelayMs = (int)Math.Clamp(BarDelay, 0, FullScreenBarSettings.MaxRevealDelayMs),
            SessionStyle = BarStyleChoice.Value,
            Sections = Sections.Select(s => new BarSectionSetting(s.Kind, s.Visible)).ToList(),
        };
        _settings.FullScreenBar.Normalize();
    }
}
