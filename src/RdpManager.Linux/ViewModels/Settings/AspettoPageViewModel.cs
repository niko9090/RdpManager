using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Aspetto» delle Impostazioni (blocco A).</summary>
public sealed class AspettoPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<AppTheme> _theme;

    public AspettoPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _theme = Choices.Pick(Choices.Theme, settings.Theme);
    }

    public override string Title => "Aspetto";

    public IReadOnlyList<ChoiceItem<AppTheme>> ThemeChoices => Choices.Theme;

    public ChoiceItem<AppTheme> Theme { get => _theme; set => Set(ref _theme, value); }

    public override void Apply() => _settings.Theme = Theme.Value;
}
