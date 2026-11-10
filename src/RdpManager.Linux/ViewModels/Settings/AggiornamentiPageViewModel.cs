using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Aggiornamenti» delle Impostazioni (blocco B).</summary>
public sealed class AggiornamentiPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<UpdateMode> _update;

    public AggiornamentiPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _update = Choices.Pick(Choices.Update, settings.UpdateMode);
    }

    public override string Title => "Aggiornamenti";

    public IReadOnlyList<ChoiceItem<UpdateMode>> UpdateChoices => Choices.Update;

    public ChoiceItem<UpdateMode> Update { get => _update; set => Set(ref _update, value); }

    public override void Apply() => _settings.UpdateMode = Update.Value;
}
