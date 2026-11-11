using RdpManager.Core;
using RdpManager.Linux.Services;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Avvio» delle Impostazioni (blocco A): riapertura delle sessioni, cronologia, log, file dei dati.</summary>
public sealed class AvvioPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private bool _reopen, _history, _verbose;

    public AvvioPageViewModel(AppSettings settings, string dataFilePath, IShellService? shell = null)
    {
        _settings = settings;
        DataFilePath = dataFilePath;
        _reopen = settings.ReopenSessionsOnStartup;
        _history = settings.HistoryEnabled;
        _verbose = settings.VerboseLog;
        OpenLogFolderCommand = new RelayCommand(() => shell?.OpenFolder(AppLog.Default?.Directory ?? AppLog.DefaultDirectory));
        OpenDataFolderCommand = new RelayCommand(() => shell?.OpenFolder(Path.GetDirectoryName(Path.GetFullPath(dataFilePath))!));
    }

    public override string Title => "Avvio";

    public string DataFilePath { get; }

    public string LogFolderNote => $"Log a rotazione in {AppLog.DefaultDirectory} (5 file da 1 MB).";

    /// <summary>«Riapri all'avvio le sessioni lasciate aperte». La riapertura vera è del blocco B; qui c'è solo la scelta.</summary>
    public bool ReopenSessionsOnStartup { get => _reopen; set => Set(ref _reopen, value); }

    /// <summary>«Annota inizio, fine e motivo di ogni sessione» (la cronologia delle connessioni).</summary>
    public bool HistoryEnabled { get => _history; set => Set(ref _history, value); }

    public bool VerboseLog { get => _verbose; set => Set(ref _verbose, value); }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public override void Apply()
    {
        _settings.ReopenSessionsOnStartup = ReopenSessionsOnStartup;
        _settings.HistoryEnabled = HistoryEnabled;
        _settings.VerboseLog = VerboseLog;
    }
}
