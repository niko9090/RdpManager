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
        Reload();
    }

    public ObservableCollection<WorkspaceItem> Items { get; } = [];

    public RelayCommand OpenCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ApplyShortcutCommand { get; }

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
        }
    }

    /// <summary>Scorciatoia da assegnare, per esempio «Ctrl+Alt+Maiusc+1»; vuota = nessuna.</summary>
    public string Shortcut { get => _shortcut; set => Set(ref _shortcut, value); }

    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => "Nessuno spazio di lavoro salvato. Apri le connessioni che ti servono e scegli «Spazi di lavoro → Salva lo spazio attuale».";

    public void Reload()
    {
        var keep = Selected?.Workspace;
        Items.Clear();
        foreach (var ws in _doc.Workspaces.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new WorkspaceItem(ws, ws.Connections.Count(id => _doc.Find(id) is not null)));
        Selected = Items.FirstOrDefault(i => i.Workspace == keep) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenSelected()
    {
        if (Selected is { } item)
            _open(item.Workspace);
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } item)
            return;
        if (!await _dialogs.ConfirmDestructiveAsync("Elimina spazio di lavoro", $"Eliminare lo spazio di lavoro «{item.Name}»? Le connessioni non vengono toccate."))
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
