using System.Collections.ObjectModel;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Linux.Sessions;

namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Modello della finestra principale. Diviso in tre file: questo (albero, sessioni, salvataggio),
/// <c>MainWindowViewModel.A.cs</c> (comandi del blocco A) e <c>MainWindowViewModel.B.cs</c> (comandi del blocco B).
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ConnectionStore _store;
    private readonly ISessionHost _sessions;
    private readonly IDialogService _dialogs;
    private readonly HashSet<string> _expanded = new(StringComparer.CurrentCultureIgnoreCase);
    private string _searchText = "";
    private string _status = "";
    private TreeNodeViewModel? _selectedNode;
    private SessionTabViewModel? _selectedTab;
    private bool _canSave = true;
    private bool _treeFiltered;

    public MainWindowViewModel(ConnectionStore store, ISessionHost sessions, IDialogService dialogs)
    {
        _store = store;
        _sessions = sessions;
        _dialogs = dialogs;

        NewConnectionCommand = new RelayCommand(() => Run(NewConnectionAsync));
        NewFolderCommand = new RelayCommand(() => Run(NewFolderAsync));
        EditCommand = new RelayCommand(() => Run(EditSelectedAsync), () => SelectedNode is not null);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => SelectedNode?.Connection is not null);
        DeleteCommand = new RelayCommand(() => Run(DeleteSelectedAsync), () => SelectedNode is not null);
        ConnectCommand = new RelayCommand(ConnectSelected, () => SelectedNode?.Connection is not null);
        FavoriteCommand = new RelayCommand(ToggleFavorite, () => SelectedNode?.Connection is not null);
        ProfilesCommand = new RelayCommand(() => Run(ShowProfilesAsync));
        SettingsCommand = new RelayCommand(() => Run(ShowSettingsAsync));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        SearchCommand = new RelayCommand(() => FocusSearchRequested?.Invoke(this, EventArgs.Empty));
        CloseCurrentTabCommand = new RelayCommand(() => { if (SelectedTab is { } t) CloseTab(t); }, () => SelectedTab is not null);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        Reload();
    }

    public ConnectionsDocument Document { get; private set; } = new();

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    public RelayCommand NewConnectionCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand FavoriteCommand { get; }
    public RelayCommand ProfilesCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand CloseCurrentTabCommand { get; }
    public RelayCommand ExitCommand { get; }

    /// <summary>Il menu chiede di portare il cursore nel campo di ricerca.</summary>
    public event EventHandler? FocusSearchRequested;

    /// <summary>Il menu chiede di chiudere l'applicazione.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Comando di una voce di menu non ancora realizzata su Linux: resta disabilitato finché il blocco non lo sostituisce.</summary>
    internal static RelayCommand Placeholder() => new(() => { }, () => false);

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) RebuildTree(); }
    }

    public string StatusMessage { get => _status; private set => Set(ref _status, value); }

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
                return;
            EditCommand.RaiseCanExecuteChanged();
            DuplicateCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            FavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public SessionTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value))
                CloseCurrentTabCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Numero di connessioni che passano il filtro di ricerca (per il messaggio «nessun risultato»).</summary>
    public int VisibleConnectionCount { get; private set; }

    public bool NoResults => SearchText.Trim().Length > 0 && VisibleConnectionCount == 0;

    public string Title => "RdpManager";

    public string DataFilePath => _store.FilePath;

    /// <summary>Rilegge il file dati. Se non è leggibile parte da un elenco vuoto e non lo sovrascrive.</summary>
    public void Reload()
    {
        try
        {
            Document = _store.Load();
            _canSave = true;
            StatusMessage = "";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Document = new ConnectionsDocument();
            _canSave = false;
            StatusMessage = $"Il file dei dati non è leggibile ({ex.Message}): non verrà modificato.";
        }
        RebuildTree();
    }

    // --- Albero e ricerca ---

    private void RebuildTree()
    {
        var selectedKey = SelectedNode?.Key;
        // Con la ricerca attiva tutte le cartelle sono aperte d'ufficio: non vanno ricordate come scelta dell'utente.
        if (!_treeFiltered)
            foreach (var n in Flatten(Roots).Where(n => n.IsFolder))
            {
                if (n.IsExpanded) _expanded.Add(n.FolderPath); else _expanded.Remove(n.FolderPath);
            }

        var query = SearchText.Trim();
        bool filtering = query.Length > 0;
        _treeFiltered = filtering;
        var folders = new Dictionary<string, TreeNodeViewModel>(StringComparer.CurrentCultureIgnoreCase);
        var roots = new List<TreeNodeViewModel>();

        foreach (var path in Document.AllGroups())
        {
            var node = TreeNodeViewModel.ForFolder(path);
            node.IsExpanded = filtering || _expanded.Contains(path);
            folders[path] = node;
            var parent = ConnectionsDocument.ParentFolder(path);
            (parent.Length > 0 && folders.TryGetValue(parent, out var p) ? p.Children : (ICollection<TreeNodeViewModel>)roots).Add(node);
        }

        int visible = 0;
        foreach (var c in Document.InDisplayOrder(Document.Connections.Where(c => c.Matches(query))))
        {
            visible++;
            var node = TreeNodeViewModel.ForConnection(c);
            (c.Group.Length > 0 && folders.TryGetValue(c.Group, out var f) ? f.Children : (ICollection<TreeNodeViewModel>)roots).Add(node);
        }
        VisibleConnectionCount = visible;

        if (filtering)
            roots.RemoveAll(n => Prune(n));

        Roots.Clear();
        foreach (var n in roots)
            Roots.Add(n);

        SelectedNode = selectedKey is null ? null : Flatten(Roots).FirstOrDefault(n => n.Key == selectedKey);
        OnPropertyChanged(nameof(VisibleConnectionCount));
        OnPropertyChanged(nameof(NoResults));
    }

    /// <summary>Toglie le cartelle senza risultati; vero se la cartella va rimossa.</summary>
    private static bool Prune(TreeNodeViewModel folder)
    {
        if (!folder.IsFolder)
            return false;
        foreach (var child in folder.Children.Where(Prune).ToList())
            folder.Children.Remove(child);
        return folder.Children.Count == 0;
    }

    private static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
                yield return c;
        }
    }

    // --- Connessioni e cartelle ---

    public async Task NewConnectionAsync()
    {
        var connection = new RdpConnection { Group = SelectedNode?.FolderPath ?? "" };
        var editor = new ConnectionEditViewModel(Document, connection, isNew: true);
        if (!await _dialogs.EditConnectionAsync(editor))
            return;
        if (Save())
            StatusMessage = $"Connessione «{connection.DisplayName}» aggiunta.";
        RebuildTree();
        SelectedNode = Flatten(Roots).FirstOrDefault(n => n.Connection == connection);
    }

    public async Task NewFolderAsync()
    {
        var parent = SelectedNode?.FolderPath ?? "";
        var name = await _dialogs.PromptAsync("Nuova cartella",
            parent.Length > 0 ? $"Nome della cartella (dentro «{parent}»)" : "Nome della cartella", "");
        var path = Document.AddFolder(parent, name);
        if (path is null)
            return;
        _expanded.Add(parent);
        Save();
        RebuildTree();
        SelectedNode = Flatten(Roots).FirstOrDefault(n => n.IsFolder && n.FolderPath.Equals(path, StringComparison.CurrentCultureIgnoreCase));
    }

    public async Task EditSelectedAsync()
    {
        if (SelectedNode is not { } node)
            return;
        if (node.Connection is { } c)
        {
            // Si modifica una copia: l'originale cambia solo se la conferma riesce.
            var copy = c.Clone();
            var editor = new ConnectionEditViewModel(Document, copy, isNew: false);
            if (!await _dialogs.EditConnectionAsync(editor))
                return;
            int index = Document.Connections.IndexOf(c);
            Document.Connections[index] = copy;
            Save();
            RebuildTree();
            SelectedNode = Flatten(Roots).FirstOrDefault(n => n.Connection == copy);
        }
        else
        {
            var name = await _dialogs.PromptAsync("Rinomina cartella", "Nuovo nome", node.Title);
            var newPath = Document.RenameFolder(node.FolderPath, name);
            if (newPath is null)
                return;
            _expanded.Add(newPath);
            Save();
            RebuildTree();
        }
    }

    public void DuplicateSelected()
    {
        if (SelectedNode?.Connection is not { } c)
            return;
        var copy = c.Duplicate();
        Document.Connections.Insert(Document.Connections.IndexOf(c) + 1, copy);
        Save();
        RebuildTree();
        SelectedNode = Flatten(Roots).FirstOrDefault(n => n.Connection == copy);
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedNode is not { } node)
            return;
        if (node.Connection is { } c)
        {
            if (!await _dialogs.ConfirmAsync("Elimina connessione", $"Eliminare la connessione «{c.DisplayName}»?"))
                return;
            Document.Connections.Remove(c);
        }
        else
        {
            int inside = Document.ConnectionsInFolder(node.FolderPath).Count;
            var message = inside == 0
                ? $"Eliminare la cartella «{node.FolderPath}»?"
                : $"Eliminare la cartella «{node.FolderPath}»? Le {inside} connessioni contenute non vengono cancellate: passano alla cartella superiore.";
            if (!await _dialogs.ConfirmAsync("Elimina cartella", message))
                return;
            Document.DeleteFolder(node.FolderPath);
        }
        Save();
        RebuildTree();
    }

    public void ToggleFavorite()
    {
        if (SelectedNode?.Connection is not { } c)
            return;
        c.IsFavorite = !c.IsFavorite;
        Save();
        RebuildTree();
    }

    // --- Profili e impostazioni ---

    public async Task ShowProfilesAsync()
    {
        await _dialogs.ShowProfilesAsync(new ProfilesViewModel(Document, Save, _dialogs));
        RebuildTree();
    }

    public async Task ShowSettingsAsync()
    {
        var settings = new SettingsViewModel(Document.Settings, _store.FilePath);
        if (!await _dialogs.ShowSettingsAsync(settings))
            return;
        settings.Apply();
        Save();
        RebuildTree();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Le impostazioni sono state cambiate e salvate (l'interfaccia riapplica per esempio il tema).</summary>
    public event EventHandler? SettingsChanged;

    // --- Sessioni ---

    public void ConnectSelected()
    {
        if (SelectedNode?.Connection is { } c)
            Connect(c);
    }

    public void Connect(RdpConnection connection)
    {
        var resolved = Document.WithResolvedCredentials(connection);
        var session = _sessions.Open(resolved);
        var tab = new SessionTabViewModel(session, CloseTab);
        session.Closed += (_, _) => RemoveTab(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasTabs));

        if (Document.Settings.HistoryEnabled)
        {
            Document.MarkConnected(connection.Id, DateTime.UtcNow);
            Save();
        }
        StatusMessage = _sessions.IsAvailable ? $"Connessione a «{connection.DisplayName}» aperta." : _sessions.UnavailableReason ?? "";
    }

    private void CloseTab(SessionTabViewModel tab)
    {
        tab.Session.Close();
        RemoveTab(tab); // se la sessione non ha segnalato la chiusura
    }

    private void RemoveTab(SessionTabViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        Tabs.RemoveAt(index);
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        OnPropertyChanged(nameof(HasTabs));
    }

    // --- Salvataggio ---

    /// <summary>Salva il documento; falso (con messaggio nella barra di stato) se non è possibile o non riesce.</summary>
    public bool Save()
    {
        if (!_canSave)
        {
            StatusMessage = "Il file dei dati non era leggibile: le modifiche non vengono salvate.";
            return false;
        }
        try
        {
            _store.Save(Document);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Salvataggio non riuscito: {ex.Message}";
            return false;
        }
    }

    /// <summary>Esegue un'operazione asincrona dei comandi: un errore finisce nella barra di stato invece di perdersi.</summary>
    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
    }
}
