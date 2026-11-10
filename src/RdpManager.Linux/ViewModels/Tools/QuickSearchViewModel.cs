using System.Collections.ObjectModel;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Una riga della ricerca rapida.</summary>
public sealed record QuickSearchItem(QuickSearchResult Result)
{
    public RdpConnection Connection => Result.Connection;

    public string Name => Connection.DisplayName + (Connection.Label.Length > 0 ? $"  [{Connection.Label}]" : "");

    public string Detail => RdpFile.FormatAddress(Connection.Host, Connection.Port) + (Connection.Group.Length > 0 ? "  ·  " + Connection.Group : "");

    /// <summary>Cosa succede con Invio, come nel programma Windows.</summary>
    public string Action => Result.Kind == QuickSearchKind.OpenSession ? "Passa alla sessione" : "Apri";
}

/// <summary>Ricerca rapida (#12): campo di testo e elenco ordinato per somiglianza (<see cref="QuickSearch"/>), Invio apre.</summary>
public sealed class QuickSearchViewModel : ViewModelBase
{
    private readonly IReadOnlyList<RdpConnection> _connections;
    private readonly IReadOnlyList<Guid> _openIds;
    private string _query = "";
    private QuickSearchItem? _selected;

    public QuickSearchViewModel(IEnumerable<RdpConnection> connections, IReadOnlyList<Guid> openSessionIds)
    {
        _connections = connections.ToList();
        _openIds = openSessionIds;
        AcceptCommand = new RelayCommand(() => AcceptRequested?.Invoke(this, EventArgs.Empty), () => Selected is not null);
        Search();
    }

    public ObservableCollection<QuickSearchItem> Results { get; } = [];

    public RelayCommand AcceptCommand { get; }

    /// <summary>La finestra deve chiudersi con la scelta fatta.</summary>
    public event EventHandler? AcceptRequested;

    public string Hint => "Invio: apri o passa alla sessione · ↑↓ scegli · Esc chiudi";

    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value)) Search(); }
    }

    public QuickSearchItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) AcceptCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>La scelta confermata (null se la finestra è stata chiusa senza scegliere).</summary>
    public QuickSearchItem? Chosen { get; private set; }

    /// <summary>Conferma la riga selezionata (Invio o doppio clic).</summary>
    public bool Accept()
    {
        Chosen = Selected;
        return Chosen is not null;
    }

    /// <summary>Sposta la selezione con le frecce (la casella di testo tiene il fuoco).</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
            return;
        int index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    private void Search()
    {
        Results.Clear();
        foreach (var r in QuickSearch.Find(_query, _connections, _openIds))
            Results.Add(new QuickSearchItem(r));
        Selected = Results.FirstOrDefault();
    }
}
