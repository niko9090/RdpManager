using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Una riga dell'elenco dei testi rapidi.</summary>
public sealed record QuickTextItem(QuickText Text)
{
    public string Title => Text.Name.Trim().Length > 0 ? $"{Text.Name.Trim()} — {QuickText.Preview(Text.Text, 40)}" : Text.DisplayName;
}

/// <summary>
/// Gestione dei testi rapidi (#17): nuovo, modifica, elimina, ordine. Lavora direttamente sul documento;
/// <see cref="Changed"/> dice alla finestra principale di salvare. L'invio alla sessione è del blocco B.
/// </summary>
public sealed class QuickTextsViewModel : ViewModelBase
{
    private readonly ConnectionsDocument _doc;
    private readonly Func<string, Task<bool>> _confirm;
    private QuickTextItem? _selected;
    private QuickText? _draft;
    private string _name = "", _text = "";
    private string? _error;

    public QuickTextsViewModel(ConnectionsDocument doc, Func<string, Task<bool>> confirm)
    {
        _doc = doc;
        _confirm = confirm;
        NewCommand = new RelayCommand(StartNew);
        SaveCommand = new RelayCommand(() => Save(), () => IsEditing);
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => Selected is not null && _draft is null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected is not null && _draft is null);
        MoveDownCommand = new RelayCommand(() => Move(1), () => Selected is not null && _draft is null);
        Fill(null);
    }

    public string Note => "Frammenti di testo da digitare nella sessione attiva: menu Sessione › Testi rapidi. " +
                          "Gli a capo diventano Invio. Non usare i testi rapidi per le password: sono salvati in chiaro.";

    public ObservableCollection<QuickTextItem> Items { get; } = [];

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public bool Changed { get; private set; }

    public bool IsEditing => Selected is not null || _draft is not null;

    public QuickTextItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            _draft = null;
            Name = value?.Text.Name ?? "";
            Text = value?.Text.Text ?? "";
            Error = null;
            Refresh();
        }
    }

    /// <summary>Nome (facoltativo).</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Testo da digitare nella sessione.</summary>
    public string Text { get => _text; set => Set(ref _text, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    private void StartNew()
    {
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        _draft = new QuickText();
        Name = "";
        Text = "";
        Error = null;
        Refresh();
    }

    /// <summary>Controlla e salva il testo in modifica (nuovo o esistente). Falso con <see cref="Error"/> impostato.</summary>
    public bool Save()
    {
        if (!IsEditing)
            return false;
        if (QuickText.Validate(Name, Text) is { } error)
        {
            Error = error;
            return false;
        }
        var target = _draft ?? Selected!.Text;
        target.Name = Name.Trim();
        target.Text = Text;
        if (_draft is not null)
            _doc.QuickTexts.Add(target);
        Changed = true;
        Error = null;
        Fill(target);
        return true;
    }

    public async Task DeleteAsync()
    {
        if (Selected is not { } item || !await _confirm($"Eliminare «{item.Text.DisplayName}»?"))
            return;
        _doc.QuickTexts.Remove(item.Text);
        Changed = true;
        Fill(null);
    }

    private void Move(int delta)
    {
        if (Selected is { } item && _doc.MoveQuickText(item.Text.Id, delta))
        {
            Changed = true;
            Fill(item.Text);
        }
    }

    private void Fill(QuickText? select)
    {
        Items.Clear();
        foreach (var t in _doc.QuickTexts)
            Items.Add(new QuickTextItem(t));
        _draft = null;
        // Si riassegna anche se è lo stesso testo: la riga è nuova.
        _selected = null;
        Selected = Items.FirstOrDefault(i => i.Text == select) ?? Items.FirstOrDefault();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsEditing));
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }
}
