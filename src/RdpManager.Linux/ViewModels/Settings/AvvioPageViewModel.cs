using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Avvio» delle Impostazioni (blocco A).</summary>
public sealed class AvvioPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private bool _history, _verbose;

    public AvvioPageViewModel(AppSettings settings, string dataFilePath)
    {
        _settings = settings;
        DataFilePath = dataFilePath;
        _history = settings.HistoryEnabled;
        _verbose = settings.VerboseLog;
    }

    public override string Title => "Avvio";

    public string DataFilePath { get; }

    /// <summary>Registra la cronologia delle connessioni (non la riapertura delle sessioni, che è del blocco B).</summary>
    public bool HistoryEnabled { get => _history; set => Set(ref _history, value); }

    public bool VerboseLog { get => _verbose; set => Set(ref _verbose, value); }

    public override void Apply()
    {
        _settings.HistoryEnabled = HistoryEnabled;
        _settings.VerboseLog = VerboseLog;
    }
}
