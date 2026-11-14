using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>
/// Pagina «Aspetto» delle Impostazioni: tema, modalità tocco e barra laterale, come su Windows. La modalità tocco e la barra
/// laterale appartengono ai modelli «Tocco» e «Monitor», che la pagina mostra senza duplicarli.
/// </summary>
public sealed class AspettoPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<AppTheme> _theme;

    public AspettoPageViewModel(AppSettings settings, ToccoPageViewModel? touch = null, MonitorPageViewModel? display = null)
    {
        _settings = settings;
        _theme = Choices.Pick(Choices.Theme, settings.Theme);
        Touch = touch ?? new ToccoPageViewModel(settings);
        Display = display ?? new MonitorPageViewModel(settings);
    }

    public override string Title => "Aspetto";

    public override string Subtitle => "Tema, tocco e barra laterale";

    public ToccoPageViewModel Touch { get; }

    public MonitorPageViewModel Display { get; }

    public IReadOnlyList<ChoiceItem<AppTheme>> ThemeChoices => Choices.Theme;

    public ChoiceItem<AppTheme> Theme { get => _theme; set => Set(ref _theme, value); }

    public override void Apply() => _settings.Theme = Theme.Value;
}
