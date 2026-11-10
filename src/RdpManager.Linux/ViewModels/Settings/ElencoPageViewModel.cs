using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Elenco» delle Impostazioni (blocco A).</summary>
public sealed class ElencoPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private ChoiceItem<ConnectionOrder> _order;

    public ElencoPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _order = Choices.Pick(Choices.Order, settings.ConnectionOrder);
    }

    public override string Title => "Elenco";

    public IReadOnlyList<ChoiceItem<ConnectionOrder>> OrderChoices => Choices.Order;

    public ChoiceItem<ConnectionOrder> Order { get => _order; set => Set(ref _order, value); }

    public override void Apply() => _settings.ConnectionOrder = Order.Value;
}
