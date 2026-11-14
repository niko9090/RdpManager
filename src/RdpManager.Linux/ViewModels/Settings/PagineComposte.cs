namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>
/// Pagina che mostra i controlli di altri modelli (stesse sezioni di Windows). Non scrive niente da sola:
/// <see cref="SettingsViewModel.Apply"/> applica una volta sola ogni modello di base.
/// </summary>
public abstract class ComposedSettingsPageViewModel : SettingsPageViewModel
{
    public override void Apply() { }
}

/// <summary>«Generale»: area di notifica, richiamo rapido, elenco delle connessioni, avvio.</summary>
public sealed class GeneralePageViewModel(VassoioPageViewModel tray, ElencoPageViewModel list, AvvioPageViewModel startup)
    : ComposedSettingsPageViewModel
{
    public override string Title => "Generale";

    public override string Subtitle => "Avvio, elenco delle connessioni e area di notifica";

    public VassoioPageViewModel Tray { get; } = tray;

    public ElencoPageViewModel List { get; } = list;

    public AvvioPageViewModel Startup { get; } = startup;
}

/// <summary>«Schermo intero»: monitor e barra delle sessioni.</summary>
public sealed class SchermoInteroPageViewModel(MonitorPageViewModel display) : ComposedSettingsPageViewModel
{
    public override string Title => "Schermo intero";

    public override string Subtitle => "Monitor e barra delle sessioni";

    public MonitorPageViewModel Display { get; } = display;
}

/// <summary>«Cronologia e log»: registri delle sessioni e dell'app, più il file dei dati.</summary>
public sealed class CronologiaPageViewModel(AvvioPageViewModel logs) : ComposedSettingsPageViewModel
{
    public override string Title => "Cronologia e log";

    public override string Subtitle => "Registri delle sessioni e dell'app";

    public AvvioPageViewModel Logs { get; } = logs;
}
