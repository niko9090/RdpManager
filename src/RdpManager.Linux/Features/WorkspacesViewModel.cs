using System.Collections.ObjectModel;
using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Features;

/// <summary>Una riga dell'elenco degli spazi di lavoro.</summary>
public sealed class WorkspaceItem(Workspace workspace, int available)
{
    public Workspace Workspace { get; } = workspace;

    public string Name => Workspace.Name;

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Workspace.Connections.Count} connessioni" };
            if (available < Workspace.Connections.Count)
                parts.Add($"{Workspace.Connections.Count - available} non esistono più");
            if (Workspace.SplitLeft is not null)
                parts.Add("vista affiancata");
            if (Workspace.FullScreen)
                parts.Add("schermo intero");
            if (Workspace.Shortcut.Length > 0)
                parts.Add(Workspace.Shortcut);
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Name;
}

/// <summary>Gestione degli spazi di lavoro: apri, elimina, assegna la scorciatoia.</summary>
public sealed class WorkspacesViewModel : ViewModelBase
{
    private readonly ConnectionsDocument _doc;
    private readonly Func<bool> _save;
    private readonly Action<Workspace> _open;
    private readonly IDialogService _dialogs;
    private WorkspaceItem? _selected;
    private string _shortcut = "";
    private string _message = "";

    public WorkspacesViewModel(ConnectionsDocument doc, Func<bool> save, Action<Workspace> open, IDialogService dialogs)
    {
        _doc = doc;
        _save = save;
        _open = open;
        _dialogs = dialogs;
        OpenCommand = new RelayCommand(OpenSelected, () => Selected is not null);
        DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => Selected is not null);
        ApplyShortcutCommand = new RelayCommand(ApplyShortcut, () => Selected is not null);
        RenameCommand = new RelayCommand(() => _ = RenameSelectedAsync(), () => Selected is not null);
        ShortcutCommand = new RelayCommand(() => _ = AskShortcutAsync(), () => Selected is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => Selected is not null);
        MoveDownCommand = new RelayCommand(() => MoveSelected(+1), () => Selected is not null);
        Reload();
    }

    public ObservableCollection<WorkspaceItem> Items { get; } = [];

    public RelayCommand OpenCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ApplyShortcutCommand { get; }

    /// <summary>«Rinomina…», «Scorciatoia…», «Su» e «Giù»: gli stessi pulsanti del dialogo di Windows.</summary>
    public RelayCommand RenameCommand { get; }
    public RelayCommand ShortcutCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public WorkspaceItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            Shortcut = value?.Workspace.Shortcut ?? "";
            OpenCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ApplyShortcutCommand.RaiseCanExecuteChanged();
            RenameCommand.RaiseCanExecuteChanged();
            ShortcutCommand.RaiseCanExecuteChanged();
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Scorciatoia da assegnare, per esempio «Ctrl+Alt+Maiusc+1»; vuota = nessuna.</summary>
    public string Shortcut { get => _shortcut; set => Set(ref _shortcut, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public string Intro => "Uno spazio di lavoro riapre le sue connessioni con lo stesso layout. Si salva da Spazi di lavoro › Salva lo spazio attuale.";

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => "Nessuno spazio di lavoro salvato. Apri le connessioni che ti servono e scegli «Spazi di lavoro › Salva lo spazio attuale».";

    public void Reload()
    {
        var keep = Selected?.Workspace;
        Items.Clear();
        foreach (var ws in _doc.Workspaces)
            Items.Add(new WorkspaceItem(ws, ws.Connections.Count(id => _doc.Find(id) is not null)));
        Selected = Items.FirstOrDefault(i => i.Workspace == keep) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenSelected()
    {
        if (Selected is { } item)
            _open(item.Workspace);
    }

    /// <summary>Chiede il nuovo nome e lo salva (come «Rinomina…» su Windows).</summary>
    public async Task RenameSelectedAsync()
    {
        if (Selected is not { } item)
            return;
        if (await _dialogs.PromptAsync("Rinomina", "Nome dello spazio di lavoro:", item.Name) is not { Length: > 0 } name)
            return;
        item.Workspace.Name = name.Trim();
        _save();
        Reload();
    }

    /// <summary>Chiede la combinazione e la assegna (come «Scorciatoia…» su Windows).</summary>
    public async Task AskShortcutAsync()
    {
        if (Selected is not { } item)
            return;
        var text = await _dialogs.PromptAsync("Scorciatoia", "Combinazione (es. Ctrl+Alt+Maiusc+1; vuoto = nessuna):", item.Workspace.Shortcut);
        if (text is null)
            return;
        Shortcut = text;
        ApplyShortcut();
    }

    /// <summary>Sposta di un posto lo spazio scelto (su = -1, giù = +1) nell'ordine salvato.</summary>
    public void MoveSelected(int delta)
    {
        if (Selected is not { } item)
            return;
        int from = _doc.Workspaces.IndexOf(item.Workspace), to = Math.Clamp(from + delta, 0, _doc.Workspaces.Count - 1);
        if (from < 0 || from == to)
            return;
        _doc.Workspaces.RemoveAt(from);
        _doc.Workspaces.Insert(to, item.Workspace);
        _save();
        Reload();
        Selected = Items.FirstOrDefault(i => i.Workspace == item.Workspace);
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } item)
            return;
        if (!await _dialogs.ConfirmDestructiveAsync("Elimina spazio di lavoro", $"Eliminare lo spazio di lavoro «{item.Name}»? Le connessioni restano."))
            return;
        _doc.Workspaces.Remove(item.Workspace);
        _save();
        Reload();
    }

    public void ApplyShortcut()
    {
        if (Selected is not { } item)
            return;
        var text = Shortcut.Trim();
        if (text.Length > 0)
        {
            if (!KeyCombo.TryParse(text, out var combo) || !combo.HasCommandModifier)
            {
                Message = "La scorciatoia non è valida: serve Ctrl o Alt più un tasto (per esempio Ctrl+Alt+Maiusc+1).";
                return;
            }
            text = combo.ToString();
            var other = _doc.Workspaces.FirstOrDefault(w => w != item.Workspace && w.Shortcut.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                Message = $"La scorciatoia è già usata dallo spazio «{other.Name}».";
                return;
            }
        }
        item.Workspace.Shortcut = text;
        _save();
        Message = text.Length == 0 ? "Scorciatoia tolta." : $"Scorciatoia impostata: {text}.";
        Reload();
    }
}
