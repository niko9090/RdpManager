using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Elenco» delle Impostazioni (blocco A): come si ordinano le connessioni.</summary>
public sealed class ElencoPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private bool _manual;

    public ElencoPageViewModel(AppSettings settings)
    {
        _settings = settings;
        _manual = settings.ConnectionOrder == ConnectionOrder.Manual;
    }

    public override string Title => "Elenco";

    /// <summary>«Ordine delle connessioni scelto trascinandole (altrimenti per nome)».</summary>
    public bool ManualOrder { get => _manual; set => Set(ref _manual, value); }

    public string Note => "Con l'ordine manuale si trascinano le connessioni nell'elenco per cambiarne la posizione o spostarle in un'altra cartella. " +
                          "Anche «Visualizza › Ordina le connessioni per nome» torna all'ordine alfabetico.";

    public ConnectionOrder Order => ManualOrder ? ConnectionOrder.Manual : ConnectionOrder.Alphabetical;

    public override void Apply() => _settings.ConnectionOrder = Order;
}
