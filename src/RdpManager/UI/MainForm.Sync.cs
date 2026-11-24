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
        _syncPoll.Tick += async (_, _) =>
        {
            await RunSyncAsync(manual: false);
            if (_autoSync)
                await RecoverVpnKeyAsync();
        };
        _syncPoll.Start();
        _folderRetryTimer.Tick += async (_, _) =>
        {
            _folderRetryTimer.Stop();
            await RefreshVpnFolderAsync();
        };
        _vpnWatch.Tick += async (_, _) =>
        {
            if (_autoSync && !_shutdownCts.IsCancellationRequested)
                await ReconnectVpnPeriodicAsync();
        };
        _vpnWatch.Start();
        Shown += async (_, _) =>
        {
            var vpnUp = false;
            if (_autoSync && !_shutdownCts.IsCancellationRequested
                && await AutoConnectVpnAsync() is { Length: > 0 } autoLine && !IsDisposed)
            {
                ShowNotice(autoLine);
                vpnUp = autoLine.StartsWith("VPN collegata", StringComparison.Ordinal);
            }
            if (_autoSync && _sync.SignedIn && !_shutdownCts.IsCancellationRequested)
            {
                if (vpnUp)
                    await WaitForVpnHandshakeAsync();
                await RunSyncAsync(manual: false);
                if (await EnsureVpnAsync() is { Length: > 0 } vpnLine && !IsDisposed)
                    ShowNotice(vpnLine);
            }
        };
        InitFileTransfer();
        FormClosed += (_, _) =>
        {
            _syncDelay.Dispose();
            _syncPoll.Dispose();
            _folderRetryTimer.Dispose();
            _vpnWatch.Dispose();
            _sync.Dispose();
        };
    }

    private readonly System.Windows.Forms.Timer _folderRetryTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _vpnWatch = new() { Interval = 60_000 };
    private readonly VpnFolderRetry _folderRetry = new();
    private Vpn.VpnAutoReconnect? _vpnReconnect;

    /// <summary>Aggiorna la cartella «marshmelloVPN»; ogni fallimento va nel registro e fa ritentare presto (30 s, 1, 2 min, poi i 5 min normali).</summary>
    internal async Task RefreshVpnFolderAsync()
    {
        if (!_sync.SignedIn || _locked || IsDisposed || _shutdownCts.IsCancellationRequested)
            return;
        VpnFolderRefresh result;
        try
        {
            result = await _sync.TryRefreshVpnFolderAsync(new FormSyncAccess(this), _shutdownCts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return;
        }
        if (IsDisposed)
            return;
        if (result.Failure is { } why)
        {
            var next = _folderRetry.OnFailure();
            AppLog.Default?.Info($"Cartella marshmelloVPN non aggiornata ({why}); riprovo fra {next.TotalSeconds:0} s");
            if (next < VpnFolderRetry.Normal)
            {
                _folderRetryTimer.Stop();
                _folderRetryTimer.Interval = (int)next.TotalMilliseconds;
                _folderRetryTimer.Start();
            }
        }
        else
        {
            _folderRetry.OnSuccess();
            _folderRetryTimer.Stop();
        }
    }

    /// <summary>Dopo l'accensione della VPN: aspetta un po' l'handshake (al massimo 15 s), poi si può aggiornare la cartella.</summary>
    private async Task WaitForVpnHandshakeAsync()
    {
        try
        {
            for (var i = 0; i < 15 && !IsDisposed && !_shutdownCts.IsCancellationRequested; i++)
            {
                if (CreateTunnel().Dettagli?.LastHandshakeUtc is not null)
                    return;
                await Task.Delay(1000, _shutdownCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Ogni 60 s: se il profilo ha «collegati all'avvio» e il tunnel è giù lo riaccende (senza UAC in ciclo).</summary>
    private async Task ReconnectVpnPeriodicAsync()
    {
        try
        {
            if (IsDisposed || !Vpn.WireGuardNative.DllPresent)
                return;
            var reconnect = _vpnReconnect ??= new Vpn.VpnAutoReconnect(CreateTunnel(), new Vpn.ScControl(),
                msg => AppLog.Default?.Info(msg));
            var profile = VpnProfile.Load();
            var line = await Task.Run(() => reconnect.Check(profile, p => p.Save()));
            if (IsDisposed)
                return;
            if (line.Length > 0)
                ShowNotice(line);
            if (line.StartsWith("VPN collegata", StringComparison.Ordinal) && _sync.SignedIn)
            {
                await WaitForVpnHandshakeAsync();
                await RefreshVpnFolderAsync();
            }
        }
        catch (Exception ex) when (ex is Vpn.VpnException or ArgumentException or InvalidOperationException or IOException)
        {
            AppLog.Default?.Info("Controllo periodico della VPN non riuscito: " + ex.Message);
        }
    }

    /// <summary>L'utente ha agito sulla VPN (per esempio Collega): il controllo periodico può riprovare.</summary>
    internal void ResetVpnAutoReconnect() => _vpnReconnect?.Reset();

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
            await RefreshVpnFolderAsync();
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
