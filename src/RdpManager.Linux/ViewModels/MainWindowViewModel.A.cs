namespace RdpManager.Linux.ViewModels;

// Blocco A: connessioni, gruppi, ricerca, importa/esporta, PIN, impostazioni.
// Ogni comando qui sotto è un segnaposto disabilitato, già legato a una voce di Menus/MainMenu.axaml.
// Per realizzare una funzione: sostituire il segnaposto con un RelayCommand vero (il nome della proprietà non cambia,
// così il menu non va toccato). Nuove voci o scorciatoie vanno aggiunte nella zona del blocco A del menu.
public sealed partial class MainWindowViewModel
{
    /// <summary>File, Importa, da file .rdp (#20).</summary>
    public RelayCommand ImportRdpCommand { get; } = Placeholder();

    /// <summary>File, Importa, da file .rdpm (#21).</summary>
    public RelayCommand ImportRdpmCommand { get; } = Placeholder();

    /// <summary>File, Importa, da file CSV (#22).</summary>
    public RelayCommand ImportCsvCommand { get; } = Placeholder();

    /// <summary>File, Esporta, tutto in un file .rdpm (#24).</summary>
    public RelayCommand ExportRdpmCommand { get; } = Placeholder();

    /// <summary>File, Blocca RdpManager (#28).</summary>
    public RelayCommand LockCommand { get; } = Placeholder();

    /// <summary>File, Apri cartella dei dati (#55).</summary>
    public RelayCommand OpenDataFolderCommand { get; } = Placeholder();

    /// <summary>Connessioni, Connetti tutta la cartella (#7).</summary>
    public RelayCommand ConnectFolderCommand { get; } = Placeholder();

    /// <summary>Connessioni, Impostazioni della cartella (#5).</summary>
    public RelayCommand FolderSettingsCommand { get; } = Placeholder();

    /// <summary>Connessioni, Esporta come file .rdp (#25).</summary>
    public RelayCommand ExportRdpFileCommand { get; } = Placeholder();

    /// <summary>Connessioni, Accendi con Wake-on-LAN (#14).</summary>
    public RelayCommand WakeCommand { get; } = Placeholder();

    /// <summary>Connessioni, Verifica raggiungibilità (#13).</summary>
    public RelayCommand CheckReachabilityCommand { get; } = Placeholder();

    /// <summary>Connessioni, Cronologia (#19).</summary>
    public RelayCommand HistoryCommand { get; } = Placeholder();

    /// <summary>Visualizza, Ricerca rapida (#12).</summary>
    public RelayCommand QuickSearchCommand { get; } = Placeholder();

    /// <summary>Visualizza, Ordina le connessioni per nome (#8).</summary>
    public RelayCommand SortByNameCommand { get; } = Placeholder();

    /// <summary>Sessione, Gestisci testi rapidi (#17).</summary>
    public RelayCommand QuickTextsCommand { get; } = Placeholder();

    /// <summary>Strumenti, Cerca PC in rete (#15).</summary>
    public RelayCommand NetworkScanCommand { get; } = Placeholder();

    /// <summary>Aiuto, Apri la cartella dei log (#55).</summary>
    public RelayCommand OpenLogFolderCommand { get; } = Placeholder();

    /// <summary>Aiuto, Pagina del progetto su GitHub (#56).</summary>
    public RelayCommand OpenProjectPageCommand { get; } = Placeholder();

    /// <summary>Aiuto, Informazioni (#56).</summary>
    public RelayCommand AboutCommand { get; } = Placeholder();
}
