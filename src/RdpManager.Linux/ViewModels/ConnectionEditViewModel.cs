using System.Globalization;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Modulo di modifica di una connessione: lavora su una copia e scrive sull'originale solo con <see cref="TryCommit"/>.</summary>
public sealed class ConnectionEditViewModel : ViewModelBase
{
    private static readonly ChoiceItem<Guid?> NoProfile = new(null, "(nessuno: credenziali proprie)");

    private readonly ConnectionsDocument _doc;
    private readonly RdpConnection _target;

    private string _name, _host, _port, _username, _domain, _group, _label, _mac, _gatewayHost, _width, _height, _attempts, _color;
    private string _password = "";
    private string? _error;
    private bool _clipboard, _drives, _printers, _microphone, _favorite, _autoReconnect, _useGateway, _keepAlive;
    private bool _inheritResolution, _allMonitors, _showPassword;
    private ChoiceItem<ResolutionMode> _resolution;
    private ChoiceItem<AudioMode> _audio;
    private ChoiceItem<QualityProfile> _quality;
    private ChoiceItem<Guid?> _profile, _gatewayProfile;

    /// <param name="isNew">Vero per una connessione nuova: se la conferma riesce viene aggiunta al documento.</param>
    public ConnectionEditViewModel(ConnectionsDocument doc, RdpConnection connection, bool isNew)
    {
        _doc = doc;
        _target = connection;
        IsNew = isNew;

        _name = connection.Name;
        _host = connection.Host;
        _port = connection.Port.ToString(CultureInfo.InvariantCulture);
        _username = connection.Username;
        _domain = connection.Domain;
        _group = connection.Group;
        _label = connection.Label;
        _mac = connection.MacAddress;
        _gatewayHost = connection.GatewayHost;
        _width = connection.DesktopWidth.ToString(CultureInfo.InvariantCulture);
        _height = connection.DesktopHeight.ToString(CultureInfo.InvariantCulture);
        _clipboard = connection.RedirectClipboard;
        _drives = connection.RedirectDrives;
        _printers = connection.RedirectPrinters;
        _microphone = connection.RedirectMicrophone;
        _favorite = connection.IsFavorite;
        _autoReconnect = connection.AutoReconnect;
        _useGateway = connection.UseGateway;
        _keepAlive = connection.KeepAlive;
        _inheritResolution = connection.InheritFolderResolution;
        _allMonitors = connection.UseAllMonitors;
        _attempts = Math.Clamp(connection.AutoReconnectMaxAttempts, 1, ReconnectPolicy.MaxAllowedAttempts).ToString(CultureInfo.InvariantCulture);
        _color = connection.Color ?? "";
        _resolution = Choices.Pick(Choices.Resolution, connection.ResolutionMode);
        _audio = Choices.Pick(Choices.Audio, connection.AudioMode);
        _quality = Choices.Pick(Choices.Quality, connection.Quality);

        Profiles = [NoProfile, .. doc.CredentialProfiles.Select(p => new ChoiceItem<Guid?>(p.Id, p.DisplayName))];
        _profile = Profiles.FirstOrDefault(p => p.Value is not null && p.Value == connection.CredentialProfileId) ?? NoProfile;
        GatewayProfiles = [SameGatewayCredentials, .. doc.CredentialProfiles.Select(p => new ChoiceItem<Guid?>(p.Id, "Profilo: " + p.DisplayName))];
        _gatewayProfile = connection.GatewayCredentials == GatewayCredentialsSource.Profile
            ? GatewayProfiles.FirstOrDefault(p => p.Value is not null && p.Value == connection.GatewayProfileId) ?? SameGatewayCredentials
            : SameGatewayCredentials;
        Groups = doc.AllGroups();
        _gatewayTouched = false;
    }

    private static readonly ChoiceItem<Guid?> SameGatewayCredentials = new(null, "Le stesse della connessione");

    private bool _gatewayTouched;

    /// <summary>Profili fra cui scegliere le credenziali del gateway (il primo: le stesse della connessione).</summary>
    public IReadOnlyList<ChoiceItem<Guid?>> GatewayProfiles { get; }

    public ChoiceItem<Guid?> GatewayProfile
    {
        get => _gatewayProfile;
        set { if (Set(ref _gatewayProfile, value)) _gatewayTouched = true; }
    }

    /// <summary>Colore dell'etichetta come «#RRGGBB» (vuoto = nessuno).</summary>
    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) OnPropertyChanged(nameof(ColorPreview)); }
    }

    /// <summary>Il colore normalizzato per l'anteprima, null se vuoto o non valido.</summary>
    public string? ColorPreview => RdpConnection.NormalizeColor(_color);

    /// <summary>Numero massimo di tentativi di riconnessione automatica.</summary>
    public string AutoReconnectMaxAttempts { get => _attempts; set => Set(ref _attempts, value); }

    /// <summary>Vale la risoluzione impostata sulla cartella, se c'è.</summary>
    public bool InheritFolderResolution { get => _inheritResolution; set => Set(ref _inheritResolution, value); }

    public bool UseAllMonitors { get => _allMonitors; set => Set(ref _allMonitors, value); }

    /// <summary>«Mostra» la password mentre la si scrive.</summary>
    public bool ShowPassword { get => _showPassword; set => Set(ref _showPassword, value); }

    /// <summary>
    /// Vero se la connessione ha una password salvata che questo utente non riesce a leggere (cifrata da un altro utente o
    /// sistema, per esempio importata da Windows): va reinserita (#29).
    /// </summary>
    public bool HasUnreadablePassword => _target.Password is null && !string.IsNullOrEmpty(_target.ProtectedPassword);

    /// <summary>Testo da mostrare nel campo password quando è vuoto.</summary>
    public string PasswordHint =>
        !OwnCredentials ? (Profile.Text.Length > 0 ? "(dal profilo)" : "")
        : HasUnreadablePassword ? "(password salvata non leggibile da questo utente: scrivila di nuovo)"
        : "Vuota = non cambiare quella salvata";

    /// <summary>Utente, dominio e password sono quelli della connessione (nessun profilo scelto).</summary>
    public bool OwnCredentials => Profile.Value is null;

    /// <summary>Avviso sotto il campo password, null se non serve.</summary>
    public string? PasswordWarning => HasUnreadablePassword && OwnCredentials
        ? "La password salvata è stata cifrata da un altro utente o computer e non si può leggere qui. Scrivila di nuovo per salvarla."
        : null;

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? "Nuova connessione" : "Modifica connessione";

    public IReadOnlyList<ChoiceItem<Guid?>> Profiles { get; }

    /// <summary>Cartelle esistenti, per suggerire il nome della cartella.</summary>
    public IReadOnlyList<string> Groups { get; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Domain { get => _domain; set => Set(ref _domain, value); }

    /// <summary>Vuota = non cambia la password già salvata.</summary>
    public string Password { get => _password; set => Set(ref _password, value); }

    public string Group { get => _group; set => Set(ref _group, value); }
    public string Label { get => _label; set => Set(ref _label, value); }
    public string MacAddress { get => _mac; set => Set(ref _mac, value); }
    public string GatewayHost { get => _gatewayHost; set => Set(ref _gatewayHost, value); }
    public string DesktopWidth { get => _width; set => Set(ref _width, value); }
    public string DesktopHeight { get => _height; set => Set(ref _height, value); }
    public bool RedirectClipboard { get => _clipboard; set => Set(ref _clipboard, value); }
    public bool RedirectDrives { get => _drives; set => Set(ref _drives, value); }
    public bool RedirectPrinters { get => _printers; set => Set(ref _printers, value); }
    public bool RedirectMicrophone { get => _microphone; set => Set(ref _microphone, value); }
    public bool IsFavorite { get => _favorite; set => Set(ref _favorite, value); }
    public bool AutoReconnect { get => _autoReconnect; set => Set(ref _autoReconnect, value); }
    public bool UseGateway { get => _useGateway; set => Set(ref _useGateway, value); }
    public bool KeepAlive { get => _keepAlive; set => Set(ref _keepAlive, value); }

    public IReadOnlyList<ChoiceItem<ResolutionMode>> ResolutionChoices => Choices.Resolution;
    public IReadOnlyList<ChoiceItem<AudioMode>> AudioChoices => Choices.Audio;
    public IReadOnlyList<ChoiceItem<QualityProfile>> QualityChoices => Choices.Quality;

    public ChoiceItem<ResolutionMode> Resolution
    {
        get => _resolution;
        set { if (Set(ref _resolution, value)) OnPropertyChanged(nameof(FixedResolution)); }
    }

    public ChoiceItem<AudioMode> Audio { get => _audio; set => Set(ref _audio, value); }
    public ChoiceItem<QualityProfile> Quality { get => _quality; set => Set(ref _quality, value); }
    public ChoiceItem<Guid?> Profile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value))
                return;
            OnPropertyChanged(nameof(OwnCredentials));
            OnPropertyChanged(nameof(PasswordHint));
            OnPropertyChanged(nameof(PasswordWarning));
        }
    }

    public bool FixedResolution => _resolution.Value == ResolutionMode.Fixed;

    /// <summary>Motivo per cui la conferma è stata rifiutata, null se non ce n'è.</summary>
    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Controlla i campi e li scrive sulla connessione (aggiungendola se nuova). Falso con <see cref="Error"/> impostato.</summary>
    public bool TryCommit()
    {
        // «nome:porta» nel campo del computer vale come la porta (come nel programma Windows).
        var (host, hostPort) = RdpFile.SplitAddress(Host);
        host = host.Trim();
        if (host.Length == 0)
            return Fail("Indica il nome o l'indirizzo del computer.");
        int port = hostPort ?? 0;
        if (hostPort is null && (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return Fail("La porta deve essere un numero da 1 a 65535.");
        if (!int.TryParse(AutoReconnectMaxAttempts.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int attempts)
            || attempts is < 1 or > ReconnectPolicy.MaxAllowedAttempts)
            return Fail($"I tentativi di riconnessione devono essere un numero da 1 a {ReconnectPolicy.MaxAllowedAttempts}.");
        string? color = null;
        if (Color.Trim().Length > 0 && (color = RdpConnection.NormalizeColor(Color)) is null)
            return Fail("Il colore deve essere nella forma #RRGGBB (oppure lascia vuoto).");
        int width = _target.DesktopWidth, height = _target.DesktopHeight;
        if (FixedResolution
            && (!int.TryParse(DesktopWidth.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out width) || width < 320
                || !int.TryParse(DesktopHeight.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out height) || height < 200))
            return Fail("Larghezza e altezza devono essere numeri (almeno 320 x 200).");
        var mac = "";
        if (MacAddress.Trim().Length > 0 && !WakeOnLan.TryNormalizeMac(MacAddress, out mac))
            return Fail("L'indirizzo MAC non è valido (esempio: AA:BB:CC:DD:EE:FF).");
        if (UseGateway && string.IsNullOrWhiteSpace(GatewayHost))
            return Fail("Indica l'host del gateway oppure disattivalo.");

        _target.Name = Name.Trim();
        _target.Host = host;
        _target.Port = port;
        _target.Username = Username.Trim();
        _target.Domain = Domain.Trim();
        _target.Group = RdpConnection.NormalizeGroup(Group);
        _target.Label = Label.Trim();
        _target.MacAddress = mac;
        _target.UseGateway = UseGateway;
        _target.GatewayHost = GatewayHost.Trim();
        _target.ResolutionMode = Resolution.Value;
        _target.DesktopWidth = width;
        _target.DesktopHeight = height;
        _target.RedirectClipboard = RedirectClipboard;
        _target.RedirectDrives = RedirectDrives;
        _target.RedirectPrinters = RedirectPrinters;
        _target.RedirectMicrophone = RedirectMicrophone;
        _target.IsFavorite = IsFavorite;
        _target.AutoReconnect = AutoReconnect;
        _target.KeepAlive = KeepAlive;
        _target.InheritFolderResolution = InheritFolderResolution;
        _target.UseAllMonitors = UseAllMonitors;
        _target.AutoReconnectMaxAttempts = attempts;
        _target.Color = color;
        if (_gatewayTouched)
        {
            _target.GatewayCredentials = GatewayProfile.Value is null ? GatewayCredentialsSource.SameAsConnection : GatewayCredentialsSource.Profile;
            _target.GatewayProfileId = GatewayProfile.Value;
        }
        _target.AudioMode = Audio.Value;
        _target.Quality = Quality.Value;
        _target.CredentialProfileId = Profile.Value;
        if (Password.Length > 0)
        {
            _target.Password = Password;
            _target.ProtectedPassword = null;
        }

        if (IsNew && !_doc.Connections.Contains(_target))
            _doc.Connections.Add(_target);
        Error = null;
        return true;
    }

    private bool Fail(string message)
    {
        Error = message;
        return false;
    }
}
