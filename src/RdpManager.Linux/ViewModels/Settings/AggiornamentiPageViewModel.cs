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

    public override string Subtitle => "Come si aggiorna RdpManager";

    public ChoiceItem<UpdateMode> Update
    {
        get => _update;
        set
        {
            if (value is not null && Set(ref _update, value))
            {
                OnPropertyChanged(nameof(AutoMode));
                OnPropertyChanged(nameof(NotifyMode));
                OnPropertyChanged(nameof(OffMode));
            }
        }
    }

    /// <summary>Le tre scelte come opzioni a pallino (come su Windows).</summary>
    public bool AutoMode { get => Update.Value == UpdateMode.AutomaticWithConfirmation; set { if (value) Pick(UpdateMode.AutomaticWithConfirmation); } }

    public bool NotifyMode { get => Update.Value == UpdateMode.NotifyOnly; set { if (value) Pick(UpdateMode.NotifyOnly); } }

    public bool OffMode { get => Update.Value == UpdateMode.Off; set { if (value) Pick(UpdateMode.Off); } }

    private void Pick(UpdateMode mode) => Update = Choices.Pick(Choices.Update, mode);

    public string Version => "Versione in uso: " + AppInfo.VersionText;

    public string Note => "Su Linux l'aggiornamento non si installa da solo: quando c'è una versione nuova compare un avviso con il link alla pagina della release su GitHub, da cui scaricare il pacchetto. "
        + "Il controllo avviene all'avvio e ogni 24 ore; con «Solo avviso» compare una riga nella barra di stato, con «Spenti» non si controlla da soli.";

    public override void Apply() => _settings.UpdateMode = Update.Value;
}
