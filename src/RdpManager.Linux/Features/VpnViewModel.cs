using RdpManager.Core;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Features;

/// <summary>
/// Pannello «VPN (WireGuard)»: chiavi, frammento per il server, endpoint e accensione del tunnel (NetworkManager o
/// wg-quick con pkexec, via <see cref="LinuxWireGuard"/>), più il profilo automatico dal server (provisioning).
/// </summary>
public sealed class VpnViewModel : ViewModelBase
{
    private readonly VpnProfile _profile;
    private readonly string _profilePath;
    private readonly LinuxWireGuard _tunnel;
    private readonly SyncService? _sync;
    private readonly IShellUi _ui;
    private readonly IDialogService _dialogs;
    private string _pcName, _address, _endpoint, _serverKey;
    private string _publicKey = "", _fragment = "", _status = "Spenta", _message = "";
    private bool _connected, _busy;

    public VpnViewModel(LinuxWireGuard tunnel, SyncService? sync, IShellUi ui, IDialogService dialogs, string? profilePath = null)
    {
        _tunnel = tunnel;
        _sync = sync;
        _ui = ui;
        _dialogs = dialogs;
        _profilePath = profilePath ?? VpnProfile.DefaultPath;
        _profile = VpnProfile.Load(_profilePath);
        if (_profile.Address.Length == 0)
            _profile.Address = WireGuardConfig.NextFreeAddress([]) ?? "";
        if (_profile.PcName.Length == 0)
            _profile.PcName = Environment.MachineName;
        _pcName = _profile.PcName;
        _address = _profile.Address;
        _endpoint = _profile.Endpoint;
        _serverKey = _profile.ServerPublicKey;

        GenerateKeysCommand = new RelayCommand(() => _ = GenerateKeysAsync(), () => !Busy);
        CopyFragmentCommand = new RelayCommand(() => _ = _ui.SetClipboardTextAsync(Fragment), () => Fragment.Length > 0);
        ToggleCommand = new RelayCommand(() => _ = ToggleAsync(), () => !Busy);
        ProvisionCommand = new RelayCommand(() => _ = ProvisionAsync(), () => !Busy && _sync is { SignedIn: true });
        RefreshKeys();
    }

    public RelayCommand GenerateKeysCommand { get; }
    public RelayCommand CopyFragmentCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ProvisionCommand { get; }

    public string PcName { get => _pcName; set => Set(ref _pcName, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string ServerPublicKey { get => _serverKey; set => Set(ref _serverKey, value); }
    public string PublicKey { get => _publicKey; private set => Set(ref _publicKey, value); }
    public string Fragment { get => _fragment; private set { if (Set(ref _fragment, value)) CopyFragmentCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Ultimo errore o esito (avvio, arresto, provisioning).</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool IsConnected { get => _connected; private set { if (Set(ref _connected, value)) OnPropertyChanged(nameof(ToggleText)); } }

    public string ToggleText => IsConnected ? "Spegni la VPN" : "Accendi la VPN";

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value))
                return;
            GenerateKeysCommand.RaiseCanExecuteChanged();
            ToggleCommand.RaiseCanExecuteChanged();
            ProvisionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanProvision => _sync is { SignedIn: true };

    public string BackendNote => "Su Linux la VPN usa NetworkManager (nmcli) se c'è, altrimenti wg-quick: in quel caso il sistema chiede la password di amministratore (pkexec).";

    /// <summary>Scrive i campi nel profilo e lo salva (alla chiusura del pannello e prima di accendere).</summary>
    public void Persist()
    {
        _profile.PcName = PcName.Trim();
        _profile.Address = Address.Trim();
        _profile.Endpoint = Endpoint.Trim();
        _profile.ServerPublicKey = ServerPublicKey.Trim();
        _profile.Save(_profilePath);
        RefreshKeys();
    }

    public async Task GenerateKeysAsync()
    {
        if (_profile.HasKey && !await _dialogs.ConfirmAsync("VPN",
                "Esiste già una chiave. Crearne una nuova scollega questo PC dal server finché non aggiungi il nuovo frammento. Continuare?"))
            return;
        _profile.GenerateKeys();
        Persist();
    }

    private void RefreshKeys()
    {
        var pub = _profile.PublicKey;
        PublicKey = pub ?? "";
        var addrOk = IsCidr(_profile.Address);
        Fragment = pub is not null && addrOk ? WireGuardConfig.BuildServerPeerFragment(pub, _profile.Address, _profile.PcName) : "";
        OnPropertyChanged(nameof(CanProvision));
        ProvisionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Indirizzo IPv4 con prefisso, per esempio 10.66.0.2/32.</summary>
    internal static bool IsCidr(string? text)
    {
        var parts = (text ?? "").Trim().Split('/');
        return parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var ip)
               && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
               && int.TryParse(parts[1], out var prefix) && prefix is >= 0 and <= 32;
    }

    public async Task ToggleAsync()
    {
        if (Busy)
            return;
        Persist();
        Busy = true;
        try
        {
            if (await _tunnel.IsConnectedAsync())
            {
                Status = "In arresto…";
                var r = await _tunnel.DisconnectAsync();
                Message = r.Message;
            }
            else
            {
                if (!_profile.HasKey)
                {
                    Message = "Prima crea le chiavi.";
                    return;
                }
                Status = "In avvio…";
                var r = await _tunnel.EnsureConnectedAsync(_profile.ToConfig());
                Message = r.Message;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Message = ex.Message;
        }
        finally
        {
            Busy = false;
        }
        await RefreshStateAsync();
    }

    public async Task RefreshStateAsync()
    {
        try
        {
            IsConnected = await _tunnel.IsConnectedAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                       or UnauthorizedAccessException or OperationCanceledException)
        {
            IsConnected = false;
        }
        Status = IsConnected ? "Accesa" : "Spenta";
    }

    /// <summary>Chiede al server il profilo di questo PC (chiavi create qui, al server va solo la pubblica).</summary>
    public async Task ProvisionAsync()
    {
        if (_sync is not { SignedIn: true } || Busy)
            return;
        Persist();
        Busy = true;
        try
        {
            var profile = await VpnProvisioning.EnsureAsync(_sync, _profilePath, PcName);
            if (profile is null)
            {
                Message = "Per ottenere la VPN dal server devi prima accedere all'account.";
                return;
            }
            Address = profile.Address;
            Endpoint = profile.Endpoint;
            ServerPublicKey = profile.ServerPublicKey;
            PcName = profile.PcName;
            _profile.Address = profile.Address;
            _profile.Endpoint = profile.Endpoint;
            _profile.ServerPublicKey = profile.ServerPublicKey;
            _profile.PcName = profile.PcName;
            Message = "Profilo ottenuto dal server (indirizzo " + profile.Address + "). Ora puoi accendere la VPN.";
        }
        catch (SyncServerException ex) when (ex.Code == "vpn_disabled")
        {
            Message = "Questo server non offre la VPN automatica: usa il frammento qui sopra.";
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or ArgumentException
                                       or InvalidOperationException or FormatException or IOException
                                       or UnauthorizedAccessException or OperationCanceledException)
        {
            Message = "VPN non configurata: " + ex.Message;
        }
        finally
        {
            Busy = false;
            try { RefreshKeys(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    /// <summary>
    /// VPN automatica dopo l'accesso: se questo PC non ha ancora un profilo e si è nell'account, lo ottiene dal server e accende
    /// il tunnel. Restituisce una riga di stato ("" = niente da fare).
    /// </summary>
    public static async Task<string> EnsureAsync(SyncService sync, LinuxWireGuard tunnel, string? profilePath = null, CancellationToken ct = default)
    {
        try
        {
            if (!sync.SignedIn || !VpnProvisioning.NeedsProvisioning(VpnProfile.Load(profilePath)))
                return "";
            var profile = await VpnProvisioning.EnsureAsync(sync, profilePath, ct: ct);
            if (profile is null)
                return "";
            AppLog.Default?.Info("VPN: profilo ottenuto dal server, indirizzo " + profile.Address);
            var result = await tunnel.EnsureConnectedAsync(profile.ToConfig(), ct);
            return result.Success
                ? "VPN configurata e accesa (indirizzo " + profile.Address + ")."
                : "VPN configurata ma non accesa: " + result.Message;
        }
        catch (SyncServerException ex) when (ex.Code == "vpn_disabled")
        {
            return ""; // il server non offre la VPN automatica
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or ArgumentException
                                       or InvalidOperationException or FormatException or IOException
                                       or UnauthorizedAccessException)
        {
            AppLog.Default?.Info("VPN automatica non riuscita: " + ex.Message);
            return "VPN non configurata: " + ex.Message;
        }
    }
}
