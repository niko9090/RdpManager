using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

// Blocco A: selezione multipla e azioni di gruppo (#6), trascinamento nell'elenco (#8).
public sealed partial class MainWindowViewModel
{
    private List<TreeNodeViewModel> _selectedNodes = [];

    /// <summary>Le righe selezionate nell'albero (più di una con Ctrl o Maiusc).</summary>
    public IReadOnlyList<TreeNodeViewModel> SelectedNodes => _selectedNodes;

    /// <summary>Le connessioni selezionate: quelle delle righe scelte, oppure la sola connessione della riga attuale.</summary>
    public IReadOnlyList<RdpConnection> SelectedConnections
    {
        get
        {
            var list = _selectedNodes.Where(n => n.Connection is not null).Select(n => n.Connection!).ToList();
            if (list.Count > 1)
                return list;
            return SelectedNode?.Connection is { } c ? [c] : [];
        }
    }

    /// <summary>Vero se sono selezionate due o più connessioni: Connetti, Elimina, Sposta valgono per tutte.</summary>
    public bool HasMultiSelection => _selectedNodes.Count(n => n.Connection is not null) > 1;

    /// <summary>L'albero comunica la selezione attuale (anche multipla).</summary>
    public void SetSelection(IEnumerable<TreeNodeViewModel> nodes)
    {
        _selectedNodes = nodes.ToList();
        RaiseSelectionCommands();
        if (HasMultiSelection)
            StatusMessage = $"{_selectedNodes.Count(n => n.Connection is not null)} connessioni selezionate: clic destro per le azioni di gruppo, Canc per eliminarle, Invio per connetterle.";
    }

    /// <summary>La riga dell'albero con quella chiave (per il trascinamento), null se non c'è più.</summary>
    public TreeNodeViewModel? FindNode(string key) => Flatten(Roots).FirstOrDefault(n => n.Key == key);

    /// <summary>Dopo la ricostruzione dell'albero le righe sono nuove: si ritrovano per chiave quelle ancora presenti.</summary>
    private void KeepSelectionAfterRebuild()
    {
        if (_selectedNodes.Count == 0)
            return;
        var keys = _selectedNodes.Select(n => n.Key).ToHashSet();
        _selectedNodes = Flatten(Roots).Where(n => keys.Contains(n.Key)).ToList();
    }

    public async Task ConnectGroupAsync() => await ConnectManyAsync(SelectedConnections, "Connetti");

    /// <summary>Connette le connessioni selezionate (chiede conferma se sono più di sei).</summary>
    public void ConnectGroup() => Run(ConnectGroupAsync);

    /// <summary>Elimina le connessioni selezionate dopo la conferma (le sessioni aperte restano finché non si chiudono).</summary>
    public async Task DeleteGroupAsync()
    {
        var list = SelectedConnections;
        if (list.Count == 0)
            return;
        var question = $"Eliminare {GroupActions.DescribeForConfirmation(list)}?\n\nLe sessioni già aperte restano finché non le chiudi.";
        if (!await _dialogs.ConfirmAsync("Elimina", question))
            return;
        int n = GroupActions.Delete(Document, list.Select(c => c.Id));
        _selectedNodes = [];
        SelectedNode = null;
        Save();
        RebuildTree();
        StatusMessage = $"{n} connessioni eliminate.";
    }

    /// <summary>Sposta le connessioni selezionate in una cartella scelta (anche nuova, per esempio Clienti/Rossi; vuoto = radice).</summary>
    public async Task MoveSelectedToFolderAsync()
    {
        var list = SelectedConnections;
        if (list.Count == 0)
            return;
        var label = list.Count == 1
            ? $"Cartella per «{list[0].DisplayName}» (anche nuova, es. Clienti/Rossi; vuoto = radice):"
            : $"Cartella per le {list.Count} connessioni (anche nuova, es. Clienti/Rossi; vuoto = radice):";
        var folder = await _dialogs.ChooseAsync("Sposta in una cartella", label, Document.AllGroups(), SelectedNode?.FolderPath ?? "", editable: true);
        if (folder is null)
            return;
        int n = GroupActions.MoveToFolder(Document, list.Select(c => c.Id), folder);
        var normalized = RdpConnection.NormalizeGroup(folder);
        Save();
        RebuildTree();
        StatusMessage = $"{n} connessioni spostate in «{(normalized.Length > 0 ? normalized : "radice")}».";
    }

    /// <summary>Assegna un profilo di credenziali (o nessuno) alle connessioni selezionate.</summary>
    public async Task AssignProfileToSelectedAsync()
    {
        var list = SelectedConnections;
        if (list.Count == 0)
            return;
        var profiles = Document.CredentialProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        const string none = "Credenziali proprie (nessun profilo)";
        var choices = new[] { none }.Concat(profiles.Select(p => p.DisplayName)).ToList();
        var label = list.Count == 1 ? $"Credenziali per «{list[0].DisplayName}»:" : $"Credenziali per le {list.Count} connessioni:";
        var chosen = await _dialogs.ChooseAsync("Assegna un profilo", label, choices, none, editable: false);
        if (chosen is null)
            return;
        int index = choices.IndexOf(chosen);
        if (index < 0)
            return;
        Guid? profile = index > 0 ? profiles[index - 1].Id : null;
        int n = GroupActions.AssignProfile(Document, list.Select(c => c.Id), profile);
        Save();
        RebuildTree();
        StatusMessage = $"Profilo assegnato a {n} connessioni.";
    }

    // --- Trascinamento nell'elenco (#8) ---

    /// <summary>
    /// Sposta una riga trascinata sopra <paramref name="target"/>: una connessione sopra un'altra va prima di lei (nella sua cartella),
    /// sopra una cartella ci entra, sull'area vuota (<c>null</c>) va nella radice; una cartella sopra un'altra ci entra.
    /// L'elenco passa all'ordine manuale. Falso se non c'è nulla da spostare.
    /// </summary>
    public bool MoveNode(TreeNodeViewModel source, TreeNodeViewModel? target)
    {
        bool moved;
        string? selectKey = source.Key;
        if (source.Connection is { } c)
        {
            moved = target switch
            {
                null => Document.MoveConnection(c.Id, null, ""),
                { Connection: { } before } => Document.MoveConnection(c.Id, before.Id, before.Group),
                _ => Document.MoveConnection(c.Id, null, target.FolderPath),
            };
        }
        else
        {
            var newPath = Document.MoveFolder(source.FolderPath, target?.IsFolder == true ? target.FolderPath : target?.Connection?.Group ?? "");
            moved = newPath is not null && !newPath.Equals(source.FolderPath, StringComparison.CurrentCultureIgnoreCase);
            if (newPath is not null)
                _expanded.Add(newPath);
            selectKey = newPath is null ? null : "f:" + newPath;
        }
        if (!moved)
            return false;
        Save();
        RebuildTree();
        SelectedNode = selectKey is null ? null : Flatten(Roots).FirstOrDefault(n => n.Key == selectKey);
        StatusMessage = "Elenco riordinato.";
        return true;
    }
}
