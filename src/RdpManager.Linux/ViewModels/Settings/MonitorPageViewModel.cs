using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Monitor» (blocco B): monitor preferito, barra laterale e barra a schermo intero.</summary>
public sealed class MonitorPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<string?> _monitor;
    private ChoiceItem<SidebarMode> _sidebar;
    private ChoiceItem<BarPosition> _barPosition;
    private ChoiceItem<BarMode> _barMode;

    public MonitorPageViewModel(AppSettings settings, IReadOnlyList<ScreenInfo>? screens = null)
    {
        _settings = settings;
        var list = new List<ChoiceItem<string?>> { new(null, "Il monitor principale") };
        foreach (var s in screens ?? ScreenProvider.All())
            list.Add(new ChoiceItem<string?>(s.DeviceName, s.DeviceName));
        // Un monitor scelto e ora scollegato resta in elenco, così la scelta non si perde in silenzio.
        if (settings.PreferredMonitor is { } saved && list.All(c => c.Value != saved))
            list.Add(new ChoiceItem<string?>(saved, saved + " (non collegato)"));
        MonitorChoices = list;
        _monitor = list.First(c => c.Value == settings.PreferredMonitor);
        _sidebar = Choices.Pick(SidebarChoices, settings.SidebarMode);
        _barPosition = Choices.Pick(BarPositionChoices, settings.Bar.Position);
        _barMode = Choices.Pick(BarModeChoices, settings.Bar.Mode);
    }

    public override string Title => "Monitor";

    public IReadOnlyList<ChoiceItem<string?>> MonitorChoices { get; }

    public static IReadOnlyList<ChoiceItem<SidebarMode>> SidebarChoices { get; } =
    [
        new(SidebarMode.Pinned, "Fissa, sempre visibile"),
        new(SidebarMode.AutoHide, "A comparsa dal bordo sinistro"),
    ];

    public static IReadOnlyList<ChoiceItem<BarPosition>> BarPositionChoices { get; } =
        Enum.GetValues<BarPosition>().Select(p => new ChoiceItem<BarPosition>(p, FullScreenBarSettings.Describe(p))).ToList();

    public static IReadOnlyList<ChoiceItem<BarMode>> BarModeChoices { get; } =
        Enum.GetValues<BarMode>().Select(m => new ChoiceItem<BarMode>(m, FullScreenBarSettings.Describe(m))).ToList();

    public ChoiceItem<string?> Monitor { get => _monitor; set { if (value is not null) Set(ref _monitor, value); } }

    public ChoiceItem<SidebarMode> Sidebar { get => _sidebar; set { if (value is not null) Set(ref _sidebar, value); } }

    public ChoiceItem<BarPosition> BarPositionChoice { get => _barPosition; set { if (value is not null) Set(ref _barPosition, value); } }

    public ChoiceItem<BarMode> BarModeChoice { get => _barMode; set { if (value is not null) Set(ref _barMode, value); } }

    public string Note => "Il monitor scelto è quello su cui si apre lo schermo intero e su cui la finestra viene portata all'avvio. "
        + "Con «Sposta la finestra sul monitor successivo» (menu Visualizza) si passa da uno all'altro.";

    public override void Apply()
    {
        _settings.PreferredMonitor = Monitor.Value;
        _settings.SidebarMode = Sidebar.Value;
        _settings.Bar.Position = BarPositionChoice.Value;
        _settings.Bar.Mode = BarModeChoice.Value;
    }
}
