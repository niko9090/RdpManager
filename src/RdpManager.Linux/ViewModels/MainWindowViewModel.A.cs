using System.Collections.ObjectModel;
using RdpManager.Core;
using RdpManager.Linux.Services;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels;

// Blocco A: connessioni, gruppi, ricerca, importa/esporta, PIN, impostazioni.
// I comandi hanno i nomi legati alle voci di Menus/MainMenu.axaml. Il codice è diviso per argomento:
//   questo file       comandi, cartelle, ricerca rapida, preferiti e recenti, testi rapidi, informazioni, cartelle di sistema
//   A.Selection.cs    selezione multipla, azioni di gruppo, trascinamento
//   A.Files.cs        importa .rdp/.rdpm/CSV, esporta .rdpm/.rdp
//   A.Network.cs      raggiungibilità, Wake-on-LAN, cerca PC in rete
//   A.Lock.cs         PIN e blocco
//   A.History.cs      cronologia delle sessioni
public sealed partial class MainWindowViewModel
{
    /// <summary>File, Importa, da file .rdp (#20).</summary>
    public RelayCommand ImportRdpCommand { get; private set; } = null!;

    /// <summary>File, Importa, da file .rdpm (#21).</summary>
    public RelayCommand ImportRdpmCommand { get; private set; } = null!;

    /// <summary>File, Importa, da file CSV (#22).</summary>
    public RelayCommand ImportCsvCommand { get; private set; } = null!;

    /// <summary>File, Esporta, tutto in un file .rdpm (#24).</summary>
    public RelayCommand ExportRdpmCommand { get; private set; } = null!;

    /// <summary>File, Blocca RdpManager (#28).</summary>
    public RelayCommand LockCommand { get; private set; } = null!;

    /// <summary>File, Apri cartella dei dati (#55).</summary>
    public RelayCommand OpenDataFolderCommand { get; private set; } = null!;

    /// <summary>Connessioni, Connetti tutta la cartella (#7).</summary>
    public RelayCommand ConnectFolderCommand { get; private set; } = null!;

    /// <summary>Connessioni, Impostazioni della cartella (#5).</summary>
    public RelayCommand FolderSettingsCommand { get; private set; } = null!;

    /// <summary>Connessioni, Esporta come file .rdp (#25).</summary>
    public RelayCommand ExportRdpFileCommand { get; private set; } = null!;

    /// <summary>Connessioni, Accendi con Wake-on-LAN (#14).</summary>
    public RelayCommand WakeCommand { get; private set; } = null!;

    /// <summary>Connessioni, Verifica raggiungibilità (#13).</summary>
    public RelayCommand CheckReachabilityCommand { get; private set; } = null!;

    /// <summary>Connessioni, Cronologia (#19).</summary>
    public RelayCommand HistoryCommand { get; private set; } = null!;

    /// <summary>Visualizza, Ricerca rapida (#12).</summary>
    public RelayCommand QuickSearchCommand { get; private set; } = null!;

    /// <summary>Visualizza, Ordina le connessioni per nome (#8).</summary>
    public RelayCommand SortByNameCommand { get; private set; } = null!;

    /// <summary>Sessione, Gestisci testi rapidi (#17).</summary>
    public RelayCommand QuickTextsCommand { get; private set; } = null!;

    /// <summary>Strumenti, Cerca PC in rete (#15).</summary>
    public RelayCommand NetworkScanCommand { get; private set; } = null!;

    /// <summary>Aiuto, Apri la cartella dei log (#55).</summary>
    public RelayCommand OpenLogFolderCommand { get; private set; } = null!;

    /// <summary>Aiuto, Pagina del progetto su GitHub (#56).</summary>
    public RelayCommand OpenProjectPageCommand { get; private set; } = null!;

    /// <summary>Aiuto, Informazioni (#56).</summary>
    public RelayCommand AboutCommand { get; private set; } = null!;

    /// <summary>Connessioni, Sposta in una cartella (#6): le connessioni selezionate.</summary>
    public RelayCommand MoveToFolderCommand { get; private set; } = null!;

    /// <summary>Connessioni, Assegna un profilo di credenziali (#6): le connessioni selezionate.</summary>
    public RelayCommand AssignProfileCommand { get; private set; } = null!;

    /// <summary>Elenco «Preferiti» del menu e del vassoio (#10).</summary>
    public ObservableCollection<QuickConnectItem> FavoriteItems { get; } = [];

    /// <summary>Elenco «Recenti» del menu e del vassoio (#10).</summary>
    public ObservableCollection<QuickConnectItem> RecentItems { get; } = [];

    public bool HasFavorites => FavoriteItems.Count > 0;

    public bool HasRecents => RecentItems.Count > 0;

    /// <summary>Quanti recenti si mostrano (come in Windows).</summary>
    public const int RecentCount = 8;

    private void InitBlockA()
    {
        ImportRdpCommand = new RelayCommand(() => Run(ImportRdpAsync), () => !IsLocked);
        ImportRdpmCommand = new RelayCommand(() => Run(ImportRdpmAsync), () => !IsLocked);
        ImportCsvCommand = new RelayCommand(() => Run(ImportCsvAsync), () => !IsLocked);
        ExportRdpmCommand = new RelayCommand(() => Run(ExportRdpmAsync), () => !IsLocked);
        LockCommand = new RelayCommand(() => Run(LockAsync));
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ConnectFolderCommand = new RelayCommand(() => Run(ConnectFolderAsync), () => SelectedNode is { IsFolder: true });
        FolderSettingsCommand = new RelayCommand(() => Run(FolderSettingsAsync), () => SelectedNode is { IsFolder: true });
        ExportRdpFileCommand = new RelayCommand(() => Run(ExportRdpFileAsync), () => SelectedNode?.Connection is not null && !HasMultiSelection);
        WakeCommand = new RelayCommand(() => Run(WakeAsync), () => SelectedNode?.Connection is not null && !HasMultiSelection);
        CheckReachabilityCommand = new RelayCommand(() => Run(() => CheckReachabilityAsync(announce: true)));
        HistoryCommand = new RelayCommand(() => Run(ShowHistoryAsync));
        QuickSearchCommand = new RelayCommand(() => Run(ShowQuickSearchAsync), () => !IsLocked);
        SortByNameCommand = new RelayCommand(SortByName);
        QuickTextsCommand = new RelayCommand(() => Run(ShowQuickTextsAsync));
        NetworkScanCommand = new RelayCommand(() => Run(ShowNetworkScanAsync), () => !IsLocked);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenProjectPageCommand = new RelayCommand(OpenProjectPage);
        AboutCommand = new RelayCommand(() => Run(ShowAboutAsync));
        MoveToFolderCommand = new RelayCommand(() => Run(MoveSelectedToFolderAsync), () => SelectedConnections.Count > 0);
        AssignProfileCommand = new RelayCommand(() => Run(AssignProfileToSelectedAsync), () => SelectedConnections.Count > 0);
        InitLock();
    }

    private void RaiseBlockASelectionCommands()
    {
        ConnectFolderCommand.RaiseCanExecuteChanged();
        FolderSettingsCommand.RaiseCanExecuteChanged();
        ExportRdpFileCommand.RaiseCanExecuteChanged();
        WakeCommand.RaiseCanExecuteChanged();
        MoveToFolderCommand.RaiseCanExecuteChanged();
        AssignProfileCommand.RaiseCanExecuteChanged();
    }

    // --- Cartelle (#5, #7) ---

    /// <summary>Impostazioni della cartella selezionata: profilo di credenziali e risoluzione ereditati dalle sue connessioni.</summary>
    public async Task FolderSettingsAsync()
    {
        if (IsLocked)
            return;
        if (SelectedNode is not { IsFolder: true } node)
            return;
        var vm = new FolderSettingsViewModel(Document, node.FolderPath);
        if (!await _dialogs.ShowAsync(vm))
            return;
        Save();
        RebuildTree();
        StatusMessage = $"Impostazioni della cartella «{node.FolderPath}» salvate.";
    }

    /// <summary>Connette tutta la cartella selezionata (e le sottocartelle), ognuna in una scheda.</summary>
    public async Task ConnectFolderAsync()
    {
        if (IsLocked)
            return;
        if (SelectedNode is not { IsFolder: true } node)
            return;
        await ConnectManyAsync(Document.ConnectionsInFolder(node.FolderPath), "Connetti tutta la cartella");
    }

    private async Task ConnectManyAsync(IReadOnlyList<RdpConnection> list, string title)
    {
        if (list.Count == 0)
            return;
        if (list.Count > 6 && !await _dialogs.ConfirmAsync(title, $"Aprire {list.Count} sessioni insieme?"))
            return;
        foreach (var c in list)
            Connect(c);
    }

    // --- Ordine (#8) ---

    /// <summary>Torna all'ordine per nome.</summary>
    public void SortByName()
    {
        if (IsLocked)
            return;
        Document.SortAlphabetically();
        Save();
        RebuildTree();
        StatusMessage = "Connessioni ordinate per nome.";
    }

    // --- Ricerca rapida (#12) ---

    public async Task ShowQuickSearchAsync()
    {
        if (IsLocked)
            return;
        var open = Tabs.Select(t => t.Session.ConnectionId).Distinct().ToList();
        var vm = new QuickSearchViewModel(Document.Connections, open);
        if (!await _dialogs.ShowAsync(vm) || vm.Chosen is not { } item)
            return;
        OpenOrSwitch(item.Connection);
    }

    /// <summary>Passa alla scheda già aperta della connessione, oppure la apre.</summary>
    public void OpenOrSwitch(RdpConnection connection)
    {
        if (IsLocked)
            return;
        if (Tabs.FirstOrDefault(t => t.Session.ConnectionId == connection.Id) is { } tab)
            SelectedTab = tab;
        else
            Connect(connection);
    }

    // --- Preferiti e recenti (#10) ---

    private void RefreshQuickLists()
    {
        Fill(FavoriteItems, Document.Favorites());
        Fill(RecentItems, Document.Recent(RecentCount));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecents));

        void Fill(ObservableCollection<QuickConnectItem> target, IReadOnlyList<RdpConnection> source)
        {
            target.Clear();
            foreach (var c in source)
                target.Add(new QuickConnectItem(
                    c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : ""),
                    RdpFile.FormatAddress(c.Host, c.Port),
                    new RelayCommand(() => OpenOrSwitch(c), () => !IsLocked)));
        }
    }

    // --- Testi rapidi (#17) ---

    public async Task ShowQuickTextsAsync()
    {
        if (IsLocked)
            return;
        var vm = new QuickTextsViewModel(Document, text => _dialogs.ConfirmDestructiveAsync("Testi rapidi", text));
        await _dialogs.ShowAsync(vm);
        if (vm.Changed)
            Save();
    }

    // --- Cartelle di sistema e informazioni (#55, #56) ---

    private void OpenDataFolder() { if (!IsLocked) OpenFolder(Path.GetDirectoryName(Path.GetFullPath(_store.FilePath))!); }

    private void OpenLogFolder() { if (!IsLocked) OpenFolder(AppLog.Default?.Directory ?? AppLog.DefaultDirectory); }

    private void OpenFolder(string dir)
    {
        if (!_shell.OpenFolder(dir))
            StatusMessage = $"Non riesco ad aprire la cartella {dir}.";
    }

    private void OpenProjectPage()
    {
        if (!_shell.OpenUrl(AppInfo.RepositoryUrl))
            StatusMessage = $"Non riesco ad aprire il browser: la pagina del progetto è {AppInfo.RepositoryUrl}";
    }

    /// <summary>Il testo della finestra «Informazioni».</summary>
    public string AboutText =>
        $"RdpManager {AppInfo.VersionText} per Linux — più desktop remoti nella stessa finestra.\n\n" +
        $"Progetto: {AppInfo.RepositoryUrl}\n" +
        $"Client RDP: {(_sessions.IsAvailable ? "FreeRDP" : _sessions.UnavailableReason ?? "non disponibile")}\n" +
        $"Dati: {_store.FilePath}\n\n" +
        "Le password sono cifrate con AES-GCM e una chiave del computer. Per portarle su un altro computer o su Windows usa l'esportazione in un file .rdpm.";

    public Task ShowAboutAsync() => _dialogs.ShowMessageAsync("Informazioni", AboutText);

    // --- Avvio dei servizi in sottofondo ---

    /// <summary>
    /// Da chiamare quando la finestra è aperta (lo fa il comportamento della finestra): accende il registro, controlla la raggiungibilità
    /// dei PC e abilita i controlli dopo le importazioni. Non parte nelle prove automatiche.
    /// </summary>
    public void StartBackgroundWork()
    {
        LogSetup.Initialize(Document.Settings);
        _backgroundChecks = true;
        if (!IsLocked)
            _ = CheckReachabilityAsync(announce: false);
    }

    private bool _backgroundChecks;
}

/// <summary>Una voce dei menu Preferiti e Recenti.</summary>
public sealed record QuickConnectItem(string Title, string ToolTip, RelayCommand Command);
