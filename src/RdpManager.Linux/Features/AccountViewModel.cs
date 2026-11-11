using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Features;

/// <summary>
/// Account e sincronizzazione: stesse funzioni e stessi testi del dialogo di Windows (accedi, registrati con la
/// «Password del server», sincronizza ora, esci, importa configurazione). L'indirizzo scritto a mano si corregge da solo
/// (<see cref="SyncHttp.NormalizeUrl"/>: «http://» verso la 8443 diventa «https://», per esempio https://syncnetpro.com:8443).
/// </summary>
public sealed class AccountViewModel : ViewModelBase
{
    private readonly SyncService _sync;
    private readonly Func<Task<SyncOutcome>> _syncNow;
    private readonly Func<SyncOutcome?> _lastOutcome;
    private readonly Func<Task<string>>? _afterSignIn;
    private readonly IShellUi _ui;
    private readonly IDialogService _dialogs;
    private string _server, _user, _password = "", _status = "";
    private string? _registrationCode;
    private readonly Func<string, Task<string>>? _importFile;
    private bool _busy;

    public AccountViewModel(SyncService sync, Func<Task<SyncOutcome>> syncNow, Func<SyncOutcome?> lastOutcome,
        IShellUi ui, IDialogService dialogs, Func<Task<string>>? afterSignIn = null, ClientConfig? defaultConfig = null,
        Func<string, Task<string>>? importFile = null)
    {
        _importFile = importFile;
        _sync = sync;
        _syncNow = syncNow;
        _lastOutcome = lastOutcome;
        _ui = ui;
        _dialogs = dialogs;
        _afterSignIn = afterSignIn;
        _server = sync.State.ServerUrl;
        _user = sync.State.Username;
        if (!sync.SignedIn && (defaultConfig ?? ClientConfig.FindDefault()) is { } found)
            Apply(found);

        SignInCommand = new RelayCommand(() => _ = SignInAsync(create: false), () => !Busy && !SignedIn);
        RegisterCommand = new RelayCommand(() => _ = SignInAsync(create: true), () => !Busy && !SignedIn);
        SyncNowCommand = new RelayCommand(() => _ = SyncNowAsync(), () => !Busy && SignedIn);
        SignOutCommand = new RelayCommand(() => _ = SignOutAsync(), () => !Busy && SignedIn);
        ImportConfigCommand = new RelayCommand(() => _ = ImportConfigAsync(), () => !Busy && !SignedIn);
        RefreshStatus();
    }

    public RelayCommand SignInCommand { get; }
    public RelayCommand RegisterCommand { get; }
    public RelayCommand SyncNowCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand ImportConfigCommand { get; }

    public string Explanation =>
        "Con lo stesso account, tutti i tuoi PC vedono le stesse connessioni, cartelle e profili di credenziali. "
        + "I dati sono cifrati su questo PC con la password dell'account: il server non li legge e, se perdi la password, non si recuperano.";

    public string ServerLabel => "Indirizzo del server (quello del tuo server, per esempio https://vpn.example.com:8443)";

    public string ServerUrl { get => _server; set => Set(ref _server, value); }

    public string Username { get => _user; set => Set(ref _user, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool SignedIn => _sync.SignedIn;

    public bool SignedOut => !_sync.SignedIn;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value))
                return;
            RaiseCommands();
            OnPropertyChanged(nameof(CanEditFields));
        }
    }

    public bool CanEditFields => !Busy && !SignedIn;

    /// <summary>Password del server letta dal file di configurazione o chiesta all'utente (serve solo alla registrazione).</summary>
    public string? RegistrationCode => _registrationCode;

    private void RaiseCommands()
    {
        SignInCommand.RaiseCanExecuteChanged();
        RegisterCommand.RaiseCanExecuteChanged();
        SyncNowCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        ImportConfigCommand.RaiseCanExecuteChanged();
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(SignedIn));
        OnPropertyChanged(nameof(SignedOut));
        OnPropertyChanged(nameof(CanEditFields));
        RaiseCommands();
        if (Busy)
            return;
        if (!SignedIn)
        {
            Status = "Non hai fatto l'accesso: la sincronizzazione è spenta.";
            return;
        }
        var text = $"Collegato come «{_sync.State.Username}» su {_sync.State.ServerUrl}.";
        if (_sync.State.LastSyncUtc is { } last)
            text += $"\nUltima sincronizzazione: {last.ToLocalTime():dd/MM/yyyy HH:mm:ss}.";
        if (_lastOutcome() is { Ok: false } bad)
            text += "\n" + bad.Message;
        Status = text;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        Busy = busy;
        if (!busy)
            RefreshStatus();
        else if (text is not null)
            Status = text;
    }

    public static string Explain(Exception ex) => ex switch
    {
        SyncUnreachableException { ServerDown: true } => "Il server è spento o si sta riavviando: riprova fra qualche minuto.",
        SyncUnreachableException => "Il server non risponde. Controlla l'indirizzo e che la VPN sia accesa.",
        SyncServerException { Code: "invalid_credentials" } => "Utente o password sbagliati.",
        SyncServerException { Code: "too_many_attempts", RetryAfterSeconds: { } s } =>
            $"Troppi tentativi sbagliati: riprova fra {Math.Max(1, (s + 59) / 60)} minuti.",
        SyncServerException { Code: "invalid_registration_code" } =>
            "La password del server non è giusta. Controlla di averla scritta uguale (maiuscole e simboli compresi) o chiedila a chi gestisce il server.",
        SyncServerException { Code: "account_exists" } => "Esiste già un account con questo nome: usa «Accedi».",
        SyncServerException { Code: "registration_disabled" } => "Questo server non accetta registrazioni dall'app: chiedi a chi lo gestisce.",
        SyncServerException s => s.Message,
        _ => ex.Message,
    };

    /// <summary>Gli errori che i metodi qui sotto mostrano nello stato invece di lasciarli uscire (e lasciare il dialogo occupato).</summary>
    private static bool IsExpected(Exception ex) =>
        ex is SyncServerException or SyncUnreachableException or ArgumentException or IOException
            or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException;

    public async Task SignInAsync(bool create)
    {
        if (Busy)
            return;
        SetBusy(true, create ? "Creo l'account…" : "Accedo…");
        string? error = null;
        var extra = "";
        try
        {
            if (create)
                await RegisterAsync();
            await _sync.SignInAsync(ServerUrl, Username, Password);
            Password = "";
            SetBusy(true, "Sincronizzo…");
            var outcome = await _syncNow();
            var vpnLine = _afterSignIn is null ? "" : await _afterSignIn();
            if (!outcome.Ok)
                extra += "\n" + outcome.Message;
            if (vpnLine.Length > 0)
                extra += "\n" + vpnLine;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = Explain(ex);
        }
        finally
        {
            SetBusy(false);
        }
        if (error is not null)
            Status = error;
        else
            Status += extra;
    }

    /// <summary>
    /// Registrazione con la «Password del server» (o senza, se il server è aperto): usa quella del file di configurazione;
    /// se il server ne vuole una e non c'è (o è sbagliata) la chiede all'utente.
    /// </summary>
    private async Task RegisterAsync()
    {
        while (true)
        {
            try
            {
                await _sync.RegisterAsync(ServerUrl, Username, Password, _registrationCode);
                return;
            }
            catch (SyncServerException ex) when (ex.Code == "invalid_registration_code")
            {
                SetBusy(false);
                var code = await _ui.AskSecretAsync("Registrazione",
                    "Password del server (te la dà chi gestisce il server).\nServe solo la prima volta, per creare il tuo account:");
                if (string.IsNullOrWhiteSpace(code))
                    throw new ArgumentException("Registrazione annullata: serve la password del server.");
                _registrationCode = code.Trim();
                SetBusy(true, "Creo l'account…");
            }
        }
    }

    public async Task ImportConfigAsync()
    {
        if (Busy)
            return;
        var path = await _ui.PickFileAsync("Importa configurazione", "Configurazione RdpManager", "*.rdpmconfig", "*.json", "*.conf");
        if (path is null)
            return;
        // .rdpmconfig e .conf: import completo (server, VPN, registrazione del PC, accensione).
        if (_importFile is not null && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            SetBusy(true, "Carico il file di configurazione…");
            string text;
            try { text = await _importFile(path); }
            finally { SetBusy(false); }
            if (ClientConfig.FindDefault(exeFolder: "") is { } saved)
                Apply(saved);
            Status = text;
            return;
        }
        try
        {
            Apply(ClientConfig.Load(path));
            Status = "Configurazione importata: ora scrivi nome utente e password e premi «Registrati» (o «Accedi»).";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Configurazione non importata: " + ex.Message;
        }
    }

    private void Apply(ClientConfig config)
    {
        if (config.ServerUrl.Length > 0)
            ServerUrl = config.ServerUrl;
        if (config.RegistrationCode.Length > 0)
            _registrationCode = config.RegistrationCode;
    }

    public async Task SyncNowAsync()
    {
        if (Busy)
            return;
        SetBusy(true, "Sincronizzo…");
        string? line = null;
        try
        {
            var outcome = await _syncNow();
            line = outcome.Message;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            line = Explain(ex);
        }
        finally
        {
            SetBusy(false);
        }
        Status += "\n" + line;
    }

    public async Task SignOutAsync()
    {
        if (Busy)
            return;
        if (!await _dialogs.ConfirmAsync("Account",
                "Uscire dall'account? Le connessioni di questo PC restano dove sono, ma non si sincronizzano più."))
            return;
        SetBusy(true, "Esco…");
        string? error = null;
        try
        {
            await _sync.SignOutAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = Explain(ex);
        }
        finally
        {
            SetBusy(false);
        }
        if (error is not null)
            Status = error;
    }
}
