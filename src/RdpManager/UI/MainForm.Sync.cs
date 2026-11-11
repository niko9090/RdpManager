using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Account di sincronizzazione (dalla 1.7): stesso login = stesse connessioni, cartelle e profili su ogni PC.</summary>
public sealed partial class MainForm
{
    private SyncService _sync = null!;
    private bool _autoSync;
    private bool _syncApplying;
    private bool _syncWasOffline;
    private SyncOutcome? _lastSync;
    private readonly System.Windows.Forms.Timer _syncDelay = new() { Interval = 4000 };
    private readonly System.Windows.Forms.Timer _syncPoll = new() { Interval = 5 * 60 * 1000 };

    /// <summary>Per le prove: ritardo fra una modifica e la sincronizzazione automatica.</summary>
    internal int SyncDelayMilliseconds
    {
        get => _syncDelay.Interval;
        set => _syncDelay.Interval = Math.Max(1, value);
    }

    internal SyncService Sync => _sync;

    /// <summary>Per le prove: attiva la sincronizzazione automatica anche con un archivio di prova.</summary>
    internal bool AutoSync
    {
        get => _autoSync;
        set => _autoSync = value;
    }

    private void InitSync()
    {
        _sync = new SyncService(SyncState.PathFor(_store));
        _autoSync = _backgroundChecks;
        _syncDelay.Tick += async (_, _) =>
        {
            _syncDelay.Stop();
            await RunSyncAsync(manual: false);
        };
        _syncPoll.Tick += async (_, _) => await RunSyncAsync(manual: false);
        _syncPoll.Start();
        Shown += async (_, _) =>
        {
            if (_autoSync && _sync.SignedIn && !_shutdownCts.IsCancellationRequested)
            {
                await RunSyncAsync(manual: false);
                if (await EnsureVpnAsync() is { Length: > 0 } vpnLine && !IsDisposed)
                    ShowNotice(vpnLine);
            }
        };
        FormClosed += (_, _) =>
        {
            _syncDelay.Dispose();
            _syncPoll.Dispose();
            _sync.Dispose();
        };
    }

    /// <summary>Dopo ogni salvataggio: se c'è qualcosa da mandare al server, sincronizza fra poco (senza bloccare niente).</summary>
    private void ScheduleSync()
    {
        if (!_autoSync || _syncApplying || _locked || _shutdownCts.IsCancellationRequested || _sync is not { SignedIn: true })
            return;
        if (_sync.HasLocalChanges(SyncMerge.Snapshot(_doc)))
        {
            _syncDelay.Stop();
            _syncDelay.Start();
        }
    }

    private sealed class FormSyncAccess(MainForm form) : ISyncDocumentAccess
    {
        public ConnectionsDocument Snapshot() => (ConnectionsDocument)form.Invoke(new Func<ConnectionsDocument>(() => SyncMerge.Snapshot(form._doc)));

        public bool Apply(Func<ConnectionsDocument, bool> change) => (bool)form.Invoke(new Func<bool>(() =>
        {
            if (!change(form._doc))
                return false;
            form._syncApplying = true;
            try
            {
                form.SaveConnections();
                form.RefreshTree();
            }
            finally
            {
                form._syncApplying = false;
            }
            return true;
        }));
    }

    /// <summary>Sincronizza ora (a mano) o in automatico. Mai un blocco: l'esito è un avviso discreto.</summary>
    internal async Task<SyncOutcome> RunSyncAsync(bool manual)
    {
        if (!_sync.SignedIn)
            return new SyncOutcome(SyncStatus.NotSignedIn, "Nessun account di sincronizzazione.");
        if (_locked)
            return new SyncOutcome(SyncStatus.Error, "L'app è bloccata con il PIN: sblocca per sincronizzare.");
        if (_shutdownCts.IsCancellationRequested)
            return new SyncOutcome(SyncStatus.Error, "Il programma si sta chiudendo.");
        SyncOutcome outcome;
        try
        {
            outcome = await _sync.SyncAsync(new FormSyncAccess(this), _shutdownCts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return new SyncOutcome(SyncStatus.Error, "Sincronizzazione interrotta: " + ex.Message);
        }
        if (IsDisposed)
            return outcome;
        _lastSync = outcome;
        AppLog.Default?.Info($"Sincronizzazione: {outcome.Status} - {outcome.Message}");

        if (outcome.Status == SyncStatus.Offline)
        {
            // Una volta sola per volta che il server sparisce (VPN spenta): niente avvisi a ogni tentativo.
            if (manual || !_syncWasOffline)
                ShowNotice("Sincronizzazione: server non raggiungibile (VPN spenta?). Lavoro in locale.");
            _syncWasOffline = true;
        }
        else
        {
            _syncWasOffline = false;
            if (manual || outcome.LocalChanged || outcome.Status is SyncStatus.AuthFailed or SyncStatus.Unreadable or SyncStatus.Error)
                ShowNotice("Sincronizzazione: " + outcome.Message);
        }
        return outcome;
    }

    internal void ShowSyncAccount()
    {
        using var dlg = new SyncAccountDialog(_sync, () => RunSyncAsync(manual: true), () => _lastSync, EnsureVpnAsync,
            importFile: path => ImportConfigFileAsync(path, Form.ActiveForm));
        ThemeManager.Apply(dlg);
        dlg.ShowDialog(this);
    }
}
