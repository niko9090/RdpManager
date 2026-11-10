namespace RdpManager.Linux.ViewModels;

// Blocco B: sincronizzazione, VPN, spazi di lavoro, schede, finestre, tocco, aggiornamenti.
// Ogni comando qui sotto è un segnaposto disabilitato, già legato a una voce di Menus/MainMenu.axaml.
// Per realizzare una funzione: sostituire il segnaposto con un RelayCommand vero (il nome della proprietà non cambia,
// così il menu non va toccato). Nuove voci o scorciatoie vanno aggiunte nella zona del blocco B del menu.
public sealed partial class MainWindowViewModel
{
    /// <summary>Sessione, Riconnetti (#34).</summary>
    public RelayCommand ReconnectCommand { get; } = Placeholder();

    /// <summary>Sessione, Stacca in una finestra (#36).</summary>
    public RelayCommand DetachTabCommand { get; } = Placeholder();

    /// <summary>Sessione, Incolla appunti come digitazione (#31).</summary>
    public RelayCommand PasteAsTypingCommand { get; } = Placeholder();

    /// <summary>Sessione, Invia Ctrl+Alt+Canc (#31).</summary>
    public RelayCommand SendCtrlAltDelCommand { get; } = Placeholder();

    /// <summary>Sessione, Tastiera su schermo (#31).</summary>
    public RelayCommand ScreenKeyboardCommand { get; } = Placeholder();

    /// <summary>Sessione, Foto della sessione (#31).</summary>
    public RelayCommand SnapshotCommand { get; } = Placeholder();

    /// <summary>Sessione, Scheda successiva (#33).</summary>
    public RelayCommand NextTabCommand { get; } = Placeholder();

    /// <summary>Sessione, Scheda precedente (#33).</summary>
    public RelayCommand PreviousTabCommand { get; } = Placeholder();

    /// <summary>Visualizza, Schermo intero (#40).</summary>
    public RelayCommand FullScreenCommand { get; } = Placeholder();

    /// <summary>Visualizza, Panoramica delle sessioni (#39).</summary>
    public RelayCommand OverviewCommand { get; } = Placeholder();

    /// <summary>Visualizza, Vista affiancata (#37).</summary>
    public RelayCommand SplitViewCommand { get; } = Placeholder();

    /// <summary>Visualizza, Mostra/nascondi barra laterale (#42).</summary>
    public RelayCommand ToggleSidebarCommand { get; } = Placeholder();

    /// <summary>Visualizza, Barra laterale fissa (#42).</summary>
    public RelayCommand PinSidebarCommand { get; } = Placeholder();

    /// <summary>Visualizza, Sposta la finestra sul monitor successivo (#41).</summary>
    public RelayCommand MoveToNextMonitorCommand { get; } = Placeholder();

    /// <summary>Spazi di lavoro, Salva lo spazio attuale (#43).</summary>
    public RelayCommand SaveWorkspaceCommand { get; } = Placeholder();

    /// <summary>Spazi di lavoro, Gestisci (#43).</summary>
    public RelayCommand ManageWorkspacesCommand { get; } = Placeholder();

    /// <summary>Strumenti, VPN WireGuard (#63, #64, #65).</summary>
    public RelayCommand VpnCommand { get; } = Placeholder();

    /// <summary>Strumenti, Account e sincronizzazione (#60, #61, #62).</summary>
    public RelayCommand SyncAccountCommand { get; } = Placeholder();

    /// <summary>Aiuto, Scorciatoie da tastiera (#50).</summary>
    public RelayCommand ShortcutsHelpCommand { get; } = Placeholder();

    /// <summary>Aiuto, Controlla aggiornamenti (#58).</summary>
    public RelayCommand CheckUpdatesCommand { get; } = Placeholder();
}
