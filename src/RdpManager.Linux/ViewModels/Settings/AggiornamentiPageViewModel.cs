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

    public ChoiceItem<UpdateMode> Update { get => _update; set { if (value is not null) Set(ref _update, value); } }

    public string Version => "Versione in uso: " + AppInfo.VersionText;

    public string Note => "Su Linux l'aggiornamento non si installa da solo: quando c'è una versione nuova compare un avviso con il link alla pagina della release su GitHub, da cui scaricare il pacchetto. "
        + "«Automatici, con conferma» controlla all'avvio e ogni 24 ore e mostra l'avviso; «Solo avviso» scrive solo una riga nella barra di stato; «Disattivati» non controlla da solo (resta Aiuto, Controlla aggiornamenti).";

    public override void Apply() => _settings.UpdateMode = Update.Value;
}
