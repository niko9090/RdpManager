using RdpManager.Core;
using RdpManager.Linux.Services;
using RdpManager.Linux.ViewModels.Settings;

namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Impostazioni: le stesse otto pagine di Windows (cartella <c>ViewModels/Settings</c> per i modelli, <c>Dialogs/Settings</c> per le viste).
/// Per aggiungere una pagina: nuovo modello, nuova vista, una riga in <see cref="Pages"/> (e in <see cref="Sections"/> se scrive valori).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private SettingsPageViewModel _selectedPage;

    /// <param name="shell">Per aprire le cartelle dei dati e dei log (null = pulsanti inattivi).</param>
    /// <param name="dialogs">Per le finestre delle pagine (il PIN); null = pulsanti inattivi.</param>
    public SettingsViewModel(AppSettings settings, string dataFilePath, IShellService? shell = null, IDialogService? dialogs = null)
    {
        DataFilePath = dataFilePath;
        var look = new AspettoPageViewModel(settings);
        var startup = new AvvioPageViewModel(settings, dataFilePath, shell);
        var list = new ElencoPageViewModel(settings);
        var tray = new VassoioPageViewModel(settings);
        var pin = new PinPageViewModel(settings, dialogs);
        var touch = look.Touch;
        var display = look.Display;
        var shortcuts = new ScorciatoiePageViewModel(settings);
        var updates = new AggiornamentiPageViewModel(settings);
        // Ogni modello di base una volta sola: è lui che scrive sulle impostazioni.
        Sections = [look, touch, display, startup, list, tray, pin, shortcuts, updates];
        // Le pagine mostrate sono quelle di Windows, nello stesso ordine.
        Pages =
        [
            new GeneralePageViewModel(tray, list, startup),
            look,
            new SchermoInteroPageViewModel(display),
            touch,
            shortcuts,
            pin,
            updates,
            new CronologiaPageViewModel(startup),
        ];
        _selectedPage = Pages[0];
    }

    public string DataFilePath { get; }

    /// <summary>Le pagine dell'elenco di sinistra (stesso ordine e stessi titoli di Windows).</summary>
    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    /// <summary>I modelli che tengono i valori e li scrivono su Salva; una pagina può mostrarne più di uno.</summary>
    public IReadOnlyList<SettingsPageViewModel> Sections { get; }

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
        if (Sections.OfType<PinPageViewModel>().FirstOrDefault() is { } pin && !pin.Validate())
        {
            SelectedPage = pin;
            return false;
        }
        // Come su Windows: combinazioni doppie o non valide non si salvano.
        if (Sections.OfType<ScorciatoiePageViewModel>().FirstOrDefault() is { Problems.Length: > 0 } keys)
        {
            SelectedPage = keys;
            return false;
        }
        if (Sections.OfType<VassoioPageViewModel>().FirstOrDefault() is { HotkeyError.Length: > 0 })
        {
            SelectedPage = Pages.OfType<GeneralePageViewModel>().First();
            return false;
        }
        return true;
    }

    /// <summary>Scrive i valori scelti sulle impostazioni.</summary>
    public void Apply()
    {
        foreach (var section in Sections)
            section.Apply();
    }
}
