using RdpManager.Core;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels;

// Blocco A: cronologia delle sessioni (#18 registrazione, #19 finestra «Cronologia…»).
public sealed partial class MainWindowViewModel
{
    private HistoryStore? _historyStore;
    private ConnectionHistory? _history;
    private readonly Dictionary<ISession, HistoryEntry> _openHistory = [];
    private readonly HashSet<ISession> _closedByUser = [];

    /// <summary>La cronologia (<c>history.json</c> accanto al file dei dati), letta al primo uso: le sessioni rimaste aperte si chiudono come «interrotte».</summary>
    internal ConnectionHistory History
    {
        get
        {
            if (_history is null)
            {
                _historyStore = HistoryStore.Beside(_store.FilePath);
                _history = _historyStore.Load();
                if (_history.CloseInterrupted() > 0)
                    SaveHistory();
            }
            return _history;
        }
    }

    private void SaveHistory()
    {
        if (_historyStore is null || _history is null)
            return;
        try
        {
            _historyStore.Save(_history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Cronologia non salvata: " + ex.Message;
        }
    }

    /// <summary>Annota l'inizio della sessione; la fine si annota quando la sessione si chiude.</summary>
    private void RecordHistoryStart(RdpConnection connection, ISession session)
    {
        var entry = History.Start(connection, UtcNow());
        _openHistory[session] = entry;
        SaveHistory();
        session.Closed += (_, _) => EndHistory(session);
    }

    private void EndHistory(ISession session)
    {
        if (!_openHistory.Remove(session, out var entry))
            return;
        var byUser = _closedByUser.Remove(session);
        History.End(entry, UtcNow(), byUser ? "Chiusa dall'utente" : "Sessione terminata");
        SaveHistory();
    }

    /// <summary>La scheda viene chiusa dall'utente (per distinguere il motivo nella cronologia).</summary>
    private void MarkClosedByUser(ISession session) => _closedByUser.Add(session);

    /// <summary>Mostra la cronologia; filtrata sulla connessione selezionata, se ce n'è una.</summary>
    public async Task ShowHistoryAsync()
    {
        var vm = new HistoryViewModel(History, Document, SelectedNode?.Connection?.Id, text => _dialogs.ConfirmAsync("Cronologia", text));
        await _dialogs.ShowAsync(vm);
        if (!vm.Changed)
            return;
        // Le sessioni ancora aperte restano annotate anche se la loro riga è stata tolta.
        foreach (var entry in _openHistory.Values)
            if (!History.Entries.Contains(entry))
                History.Entries.Add(entry);
        SaveHistory();
    }
}
