using RdpManager.Core;
using RdpManager.Linux.ViewModels.Settings;

namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Impostazioni: un elenco di pagine, ciascuna con il proprio modello (cartella <c>ViewModels/Settings</c>) e la propria vista
/// (cartella <c>Dialogs/Settings</c>). Per aggiungere una pagina: nuovo modello, nuova vista, una riga in <see cref="Pages"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private SettingsPageViewModel _selectedPage;

    public SettingsViewModel(AppSettings settings, string dataFilePath)
    {
        DataFilePath = dataFilePath;
        Pages =
        [
            // Blocco A
            new AspettoPageViewModel(settings),
            new AvvioPageViewModel(settings, dataFilePath),
            new ElencoPageViewModel(settings),
            new PinPageViewModel(),
            // Blocco B
            new VassoioPageViewModel(),
            new MonitorPageViewModel(),
            new ToccoPageViewModel(),
            new ScorciatoiePageViewModel(),
            new AggiornamentiPageViewModel(settings),
        ];
        _selectedPage = Pages[0];
    }

    public string DataFilePath { get; }

    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    public SettingsPageViewModel SelectedPage
    {
        get => _selectedPage;
        set { if (value is not null) Set(ref _selectedPage, value); }
    }

    /// <summary>Scrive i valori scelti sulle impostazioni.</summary>
    public void Apply()
    {
        foreach (var page in Pages)
            page.Apply();
    }
}
