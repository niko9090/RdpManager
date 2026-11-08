using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Impostazioni di base (quelle condivise con l'app Windows nello stesso file dati).</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private ChoiceItem<AppTheme> _theme;
    private ChoiceItem<UpdateMode> _update;
    private ChoiceItem<ConnectionOrder> _order;
    private bool _history, _verbose;

    public SettingsViewModel(AppSettings settings, string dataFilePath)
    {
        _settings = settings;
        DataFilePath = dataFilePath;
        _theme = Choices.Pick(Choices.Theme, settings.Theme);
        _update = Choices.Pick(Choices.Update, settings.UpdateMode);
        _order = Choices.Pick(Choices.Order, settings.ConnectionOrder);
        _history = settings.HistoryEnabled;
        _verbose = settings.VerboseLog;
    }

    public string DataFilePath { get; }

    public IReadOnlyList<ChoiceItem<AppTheme>> ThemeChoices => Choices.Theme;
    public IReadOnlyList<ChoiceItem<UpdateMode>> UpdateChoices => Choices.Update;
    public IReadOnlyList<ChoiceItem<ConnectionOrder>> OrderChoices => Choices.Order;

    public ChoiceItem<AppTheme> Theme { get => _theme; set => Set(ref _theme, value); }
    public ChoiceItem<UpdateMode> Update { get => _update; set => Set(ref _update, value); }
    public ChoiceItem<ConnectionOrder> Order { get => _order; set => Set(ref _order, value); }
    public bool HistoryEnabled { get => _history; set => Set(ref _history, value); }
    public bool VerboseLog { get => _verbose; set => Set(ref _verbose, value); }

    /// <summary>Scrive i valori scelti sulle impostazioni.</summary>
    public void Apply()
    {
        _settings.Theme = Theme.Value;
        _settings.UpdateMode = Update.Value;
        _settings.ConnectionOrder = Order.Value;
        _settings.HistoryEnabled = HistoryEnabled;
        _settings.VerboseLog = VerboseLog;
    }
}
