using RdpManager.Core;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels;

// Blocco A: PIN e blocco dell'app (#28): blocco a mano e a tempo, sblocco con attese dopo i tentativi sbagliati, «Ho dimenticato il PIN».
public sealed partial class MainWindowViewModel
{
    private bool _locked;
    private string _pinInput = "";
    private string? _lockError;
    private int _pinFailures;
    private DateTime _pinWaitUntilUtc;
    private DateTime _lastActivityUtc;

    /// <summary>Orologio (le prove lo sostituiscono).</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Sblocca con il PIN scritto nel campo della schermata di blocco.</summary>
    public RelayCommand UnlockCommand { get; private set; } = null!;

    /// <summary>«Ho dimenticato il PIN»: toglie il PIN e le password salvate (le connessioni restano).</summary>
    public RelayCommand ForgotPinCommand { get; private set; } = null!;

    /// <summary>Vero mentre l'app è bloccata: la finestra mostra solo la schermata del PIN e nasconde le sessioni.</summary>
    public bool IsLocked
    {
        get => _locked;
        private set
        {
            if (!Set(ref _locked, value))
                return;
            OnPropertyChanged(nameof(IsUnlocked));
            foreach (var c in new[] { ImportRdpCommand, ImportRdpmCommand, ImportCsvCommand, ExportRdpmCommand, QuickSearchCommand, NetworkScanCommand })
                c.RaiseCanExecuteChanged();
            RefreshQuickLists();
        }
    }

    public bool IsUnlocked => !_locked;

    /// <summary>Il PIN scritto nella schermata di blocco.</summary>
    public string PinInput { get => _pinInput; set => Set(ref _pinInput, value); }

    /// <summary>Esito dell'ultimo tentativo («PIN sbagliato.»), null se non c'è.</summary>
    public string? LockError { get => _lockError; private set => Set(ref _lockError, value); }

    public string LockHint => "Le sessioni restano aperte ma nascoste; le password salvate restano cifrate finché non sblocchi.";

    private void InitLock()
    {
        UnlockCommand = new RelayCommand(() => Unlock(PinInput));
        ForgotPinCommand = new RelayCommand(() => Run(ForgotPinAsync));
        _lastActivityUtc = UtcNow();
    }

    /// <summary>Con il PIN impostato l'app parte bloccata (e con le password di nuovo cifrate in memoria).</summary>
    private void StartLockedIfNeeded()
    {
        if (!Document.Settings.LockEnabled)
            return;
        Document.SealPasswords();
        IsLocked = true;
    }

    /// <summary>
    /// «Blocca RdpManager»: password in memoria di nuovo cifrate e schermata del PIN. Senza PIN impostato propone di
    /// impostarlo.
    /// </summary>
    public async Task LockAsync()
    {
        if (IsLocked)
            return;
        if (!Document.Settings.LockEnabled)
        {
            if (!await _dialogs.ConfirmAsync("Blocco", "Per bloccare RdpManager serve un PIN. Impostarlo ora?"))
                return;
            await SetPinAsync();
            return;
        }
        Lock();
    }

    /// <summary>Blocca subito (il PIN deve essere già impostato).</summary>
    public void Lock()
    {
        if (IsLocked || !Document.Settings.LockEnabled)
            return;
        Document.SealPasswords();
        PinInput = "";
        LockError = null;
        IsLocked = true;
        AppLog.Default?.Info("RdpManager bloccato");
    }

    /// <summary>Imposta un PIN nuovo (due volte) e lo salva.</summary>
    public async Task SetPinAsync()
    {
        var vm = new PinSetViewModel();
        if (!await _dialogs.ShowAsync(vm) || vm.Result is not { } hash)
            return;
        Document.Settings.LockPin = hash;
        Save();
        StatusMessage = "PIN impostato: «File › Blocca RdpManager» blocca l'app" +
                        (Document.Settings.LockAfterMinutes > 0 ? $", e si blocca da sola dopo {Document.Settings.LockAfterMinutes} minuti di inattività." : ".");
    }

    /// <summary>
    /// Prova a sbloccare. Dopo 5 tentativi sbagliati ci sono attese crescenti (5, 10, 20… secondi, al massimo 5 minuti).
    /// </summary>
    public bool Unlock(string pin)
    {
        if (!IsLocked)
            return true;
        var now = UtcNow();
        if (now < _pinWaitUntilUtc)
        {
            LockError = $"Troppi tentativi: riprova tra {(int)Math.Ceiling((_pinWaitUntilUtc - now).TotalSeconds)} s.";
            return false;
        }
        if (!PinLock.Verify(pin, Document.Settings.LockPin))
        {
            _pinFailures++;
            PinInput = "";
            if (_pinFailures >= 5)
                _pinWaitUntilUtc = now + TimeSpan.FromSeconds(Math.Min(300, 5 << Math.Min(_pinFailures - 5, 6)));
            LockError = "PIN sbagliato.";
            AppLog.Default?.Warning("PIN sbagliato alla schermata di blocco");
            return false;
        }
        FinishUnlock();
        return true;
    }

    private void FinishUnlock()
    {
        _pinFailures = 0;
        _pinWaitUntilUtc = default;
        PinInput = "";
        LockError = null;
        Document.UnsealPasswords();
        _lastActivityUtc = UtcNow();
        IsLocked = false;
        AppLog.Default?.Info("RdpManager sbloccato");
        RebuildTree();
    }

    /// <summary>PIN dimenticato: dopo la conferma le connessioni restano, PIN e password salvate si cancellano.</summary>
    public async Task ForgotPinAsync()
    {
        if (!IsLocked)
            return;
        int passwords = Document.Connections.Count(c => c.HasPassword) + Document.CredentialProfiles.Count(p => p.HasPassword);
        if (!await _dialogs.ConfirmAsync("PIN dimenticato",
                $"Il PIN viene tolto e le {passwords} password salvate vengono cancellate: le connessioni restano, ma le " +
                "password andranno reinserite.\n\nContinuare?"))
            return;
        ForgetPinConfirmed();
    }

    /// <summary>PIN dimenticato, già confermato.</summary>
    public void ForgetPinConfirmed()
    {
        int n = Document.ForgetPinAndPasswords();
        Save();
        AppLog.Default?.Warning($"PIN dimenticato: tolto il PIN e cancellate {n} password salvate");
        FinishUnlock();
        StatusMessage = $"PIN tolto: {n} password salvate cancellate, le connessioni sono rimaste.";
    }

    // --- Blocco a tempo ---

    /// <summary>Un tasto o un movimento del mouse: azzera l'attesa del blocco a tempo.</summary>
    public void NotifyActivity() => _lastActivityUtc = UtcNow();

    /// <summary>Blocca se l'inattività ha superato i minuti impostati. Vero se ha bloccato.</summary>
    public bool CheckIdleLock(TimeSpan? idle = null)
    {
        if (IsLocked || !Document.Settings.LockEnabled)
            return false;
        if (!LockPolicy.ShouldLock(Document.Settings, idle ?? UtcNow() - _lastActivityUtc))
            return false;
        Lock();
        return true;
    }

    private int _tickCount;

    /// <summary>
    /// Il battito dell'app (ogni 15 secondi, dal comportamento della finestra): controlla il blocco a tempo e, una volta al minuto,
    /// la raggiungibilità dei PC.
    /// </summary>
    public void Tick()
    {
        if (CheckIdleLock() || IsLocked)
            return;
        if (_backgroundChecks && ++_tickCount % 4 == 0)
            _ = CheckReachabilityAsync(announce: false);
    }
}
