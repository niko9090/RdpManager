using RdpManager.Core;
using RdpManager.Linux.Services;

namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>Pagina «Avvio» delle Impostazioni (blocco A): riapertura delle sessioni, cronologia, log, file dei dati.</summary>
public sealed class AvvioPageViewModel : SettingsPageViewModel
{
    private readonly AppSettings _settings;
    private bool _reopen, _history, _verbose;

    private readonly string _rolePath;
    private readonly AppRole _initialRole;
    private AppRole _role;

    /// <param name="rolePath">File del ruolo (null = quello dell'utente); le prove ne passano uno temporaneo.</param>
    public AvvioPageViewModel(AppSettings settings, string dataFilePath, IShellService? shell = null, string? rolePath = null)
    {
        _rolePath = rolePath ?? AppRoleStore.DefaultPath;
        _initialRole = _role = AppRoleStore.Load(_rolePath);
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

    /// <summary>Ruolo dell'installazione (indice in <see cref="RoleChoices"/>): «Desktop remoto + VPN» o «Gestore VPN».</summary>
    public int RoleIndex
    {
        get => (int)_role;
        set { if (Enum.IsDefined((AppRole)value)) Set(ref _role, (AppRole)value); }
    }

    public IReadOnlyList<string> RoleChoices { get; } = Enum.GetValues<AppRole>().Select(AppRoleStore.Describe).ToList();

    public string RoleNote => "Con «Gestore marshmelloVPN» all'avvio si apre la Plancia marshmelloVPN; la finestra con le schede resta raggiungibile. Vale dal prossimo avvio.";

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public override void Apply()
    {
        _settings.ReopenSessionsOnStartup = ReopenSessionsOnStartup;
        _settings.HistoryEnabled = HistoryEnabled;
        _settings.VerboseLog = VerboseLog;
        // Si scrive solo se è cambiato (o se il file non c'era e si è scelto qualcosa di diverso dal predefinito).
        if (_role != _initialRole)
            AppRoleStore.Save(_role, _rolePath);
    }
}
