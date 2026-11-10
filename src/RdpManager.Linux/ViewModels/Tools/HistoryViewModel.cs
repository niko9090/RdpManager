using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Una riga della cronologia.</summary>
public sealed record HistoryRow(string Name, string Host, string Start, string End, string Duration, string Reason);

/// <summary>Cronologia delle connessioni (#19): inizio, fine, durata, motivo; filtro per connessione e svuotamento.</summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private static readonly ChoiceItem<Guid?> All = new(null, "Tutte le connessioni");

    private readonly ConnectionHistory _history;
    private readonly Func<string, Task<bool>> _confirm;
    private ChoiceItem<Guid?> _filter;
    private string _summary = "";

    /// <param name="confirm">Domanda sì/no prima di svuotare.</param>
    public HistoryViewModel(ConnectionHistory history, ConnectionsDocument doc, Guid? connectionId, Func<string, Task<bool>> confirm)
    {
        _history = history;
        _confirm = confirm;
        // Connessioni dell'elenco più quelle eliminate che compaiono ancora nella cronologia.
        var choices = history.Entries.GroupBy(e => e.ConnectionId)
            .Select(g => new ChoiceItem<Guid?>(g.Key, doc.Find(g.Key)?.DisplayName ?? g.Last().Name + " (eliminata)"))
            .OrderBy(c => c.Text, StringComparer.CurrentCultureIgnoreCase);
        Filters = [All, .. choices];
        _filter = Filters.FirstOrDefault(c => c.Value is not null && c.Value == connectionId) ?? All;
        ClearShownCommand = new RelayCommand(() => _ = ClearShownAsync());
        Fill();
    }

    public IReadOnlyList<ChoiceItem<Guid?>> Filters { get; }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    public RelayCommand ClearShownCommand { get; }

    /// <summary>La cronologia è stata modificata (la finestra principale la salva).</summary>
    public bool Changed { get; private set; }

    public ChoiceItem<Guid?> Filter
    {
        get => _filter;
        set { if (value is not null && Set(ref _filter, value)) Fill(); }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public async Task ClearShownAsync()
    {
        var what = Filter.Value is null ? "tutta la cronologia" : $"la cronologia di «{Filter.Text}»";
        if (!await _confirm($"Svuotare {what}?"))
            return;
        if (Filter.Value is Guid id)
            _history.Clear(id);
        else
            _history.Clear();
        Changed = true;
        Fill();
    }

    private void Fill()
    {
        var entries = _history.Recent(Filter.Value);
        Rows.Clear();
        foreach (var e in entries)
            Rows.Add(new HistoryRow(e.Name, e.Host, e.StartUtc.ToLocalTime().ToString("g"),
                e.EndUtc is { } end ? end.ToLocalTime().ToString("g") : "in corso",
                HistoryEntry.FormatDuration(e.Duration), e.Reason));
        var total = entries.Where(e => e.Duration is not null).Aggregate(TimeSpan.Zero, (s, e) => s + e.Duration!.Value);
        Summary = $"{entries.Count} sessioni · tempo totale {HistoryEntry.FormatDuration(total)}";
    }
}
