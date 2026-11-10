using RdpManager.Core;
using RdpManager.Linux.Services;
using RdpManager.Linux.ViewModels.Settings;

namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Impostazioni: un elenco di pagine, ciascuna con il proprio modello (cartella <c>ViewModels/Settings</c>) e la propria vista
/// (cartella <c>Dialogs/Settings</c>). Per aggiungere una pagina: nuovo modello, nuova vista, una riga in <see cref="Pages"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private SettingsPageViewModel _selectedPage;

    /// <param name="shell">Per aprire le cartelle dei dati e dei log (null = pulsanti inattivi).</param>
    /// <param name="dialogs">Per le finestre delle pagine (il PIN); null = pulsanti inattivi.</param>
    public SettingsViewModel(AppSettings settings, string dataFilePath, IShellService? shell = null, IDialogService? dialogs = null)
    {
        DataFilePath = dataFilePath;
        Pages =
        [
            // Blocco A
            new AspettoPageViewModel(settings),
            new AvvioPageViewModel(settings, dataFilePath, shell),
            new ElencoPageViewModel(settings),
            new PinPageViewModel(settings, dialogs),
            // Blocco B
            new VassoioPageViewModel(settings),
            new MonitorPageViewModel(settings),
            new ToccoPageViewModel(settings),
            new ScorciatoiePageViewModel(settings),
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

    /// <summary>
    /// Controlla le pagine prima di salvare: vero se tutto è valido. Altrimenti la pagina con l'errore diventa quella mostrata.
    /// </summary>
    public bool Validate()
    {
        if (Pages.OfType<PinPageViewModel>().FirstOrDefault() is { } pin && !pin.Validate())
        {
            SelectedPage = pin;
            return false;
        }
        return true;
    }

    /// <summary>Scrive i valori scelti sulle impostazioni.</summary>
    public void Apply()
    {
        foreach (var page in Pages)
            page.Apply();
    }
}
