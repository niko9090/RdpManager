using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>
/// Esportazione in .rdpm (#24): password cifrate con una password del file (proposta quando ci sono password salvate)
/// oppure escluse, con un avviso se se ne perderebbero. Stesse regole del programma Windows.
/// </summary>
public sealed class RdpmExportViewModel : ViewModelBase
{
    public const int MinPasswordLength = 8;

    private readonly Func<string, Task<bool>> _confirm;
    private bool _withPasswords, _includeSettings;
    private string _password = "", _confirmPassword = "";
    private string? _error;

    public RdpmExportViewModel(ConnectionsDocument doc, Func<string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _confirm = confirm;
        int passwords = RdpmFile.CountExportablePasswords(doc);
        WithoutWarning = RdpmFile.WithoutPasswordsWarning(doc);
        UnreadableWarning = RdpmFile.UnreadablePasswordsWarning(doc);
        _withPasswords = RdpmFile.ProposeWithPasswords(doc);
        var profiles = doc.CredentialProfiles.Count switch { 0 => "", 1 => ", 1 profilo di credenziali", var n => $", {n} profili di credenziali" };
        var saved = passwords switch { 0 => "nessuna password salvata", 1 => "1 password salvata", _ => $"{passwords} password salvate" };
        Summary = $"{doc.Connections.Count} connessioni{profiles} e le loro cartelle; {saved}.";
    }

    public string WindowTitle => "Esporta connessioni";

    public string Summary { get; }

    /// <summary>Password salvate che su questo PC non si leggono (non andrebbero nel file), null se non ce ne sono.</summary>
    public string? UnreadableWarning { get; }

    public string? WithoutWarning { get; }

    public string CipherNote => "Cifratura AES-GCM. Senza la password del file le password salvate non si possono leggere: conservala a parte.";

    public string SettingsNote => "Senza le sessioni da riaprire, che valgono solo per questo PC. Nessuna password.";

    public bool WithPasswords
    {
        get => _withPasswords;
        set { if (Set(ref _withPasswords, value)) { OnPropertyChanged(nameof(WithoutPasswords)); } }
    }

    public bool WithoutPasswords { get => !_withPasswords; set => WithPasswords = !value; }

    public string Password { get => _password; set => Set(ref _password, value); }

    public string ConfirmPassword { get => _confirmPassword; set => Set(ref _confirmPassword, value); }

    public bool IncludeSettings { get => _includeSettings; set => Set(ref _includeSettings, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>null = password escluse dal file.</summary>
    public string? FilePassword => WithPasswords ? Password : null;

    /// <summary>
    /// Controlli prima di esportare: con le password, la password del file (almeno 8 caratteri, ripetuta uguale);
    /// senza, la conferma dell'avviso se ci sono password salvate che non andrebbero nel file.
    /// </summary>
    public async Task<bool> TryAcceptAsync()
    {
        Error = null;
        if (!WithPasswords)
            return WithoutWarning is null || await _confirm(WithoutWarning);
        Error = Password.Length < MinPasswordLength ? $"La password del file deve avere almeno {MinPasswordLength} caratteri."
            : Password != ConfirmPassword ? "Le due password non coincidono." : null;
        return Error is null;
    }
}

/// <summary>Importazione da .rdpm (#21): unione senza doppioni o sostituzione dell'elenco.</summary>
public sealed class RdpmImportViewModel : ViewModelBase
{
    private readonly int _current;
    private readonly Func<string, Task<bool>> _confirm;
    private bool _merge = true, _applySettings;

    public RdpmImportViewModel(int incoming, int current, bool hasPasswords, bool hasSettings, Func<string, Task<bool>> confirm)
    {
        _current = current;
        _confirm = confirm;
        HasSettings = hasSettings;
        Summary = $"Nel file: {incoming} connessioni{(hasPasswords ? " (con password)" : " (senza password)")}. Nell'elenco: {current}.";
    }

    public string WindowTitle => "Importa connessioni";

    public string Summary { get; }

    public string MergeNote => "Doppione = stesso host, porta, utente e dominio. Al doppione già presente si aggiunge solo la password, se non l'aveva.";

    /// <summary>Il file contiene le impostazioni dell'app: si può scegliere di applicarle.</summary>
    public bool HasSettings { get; }

    public bool Merge
    {
        get => _merge;
        set { if (Set(ref _merge, value)) OnPropertyChanged(nameof(Replace)); }
    }

    public bool Replace { get => !_merge; set => Merge = !value; }

    public bool ApplySettings { get => _applySettings; set => Set(ref _applySettings, value); }

    public ImportMode Mode => Replace ? ImportMode.Replace : ImportMode.Merge;

    public bool ApplySettingsChosen => HasSettings && ApplySettings;

    /// <summary>Con «sostituisci» chiede conferma prima di cancellare le connessioni attuali.</summary>
    public async Task<bool> TryAcceptAsync() =>
        !(Replace && _current > 0)
        || await _confirm($"Cancellare le {_current} connessioni attuali e tenere solo quelle del file?");
}
