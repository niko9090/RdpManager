using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.UI;

/// <summary>Pannello «VPN»: chiavi, frammento per il server, endpoint e accensione del tunnel WireGuard.</summary>
public sealed class VpnForm : TouchForm
{
    private readonly VpnProfile _profile;
    private readonly string _profilePath;
    private readonly IWireGuardTunnel _tunnel;
    private readonly TextBox _pcName = new() { Width = 360 };
    private readonly TextBox _address = new() { Width = 360 };
    private readonly TextBox _endpoint = new() { Width = 360 };
    private readonly TextBox _serverKey = new() { Width = 360 };
    private readonly TextBox _publicKey = new() { Width = 360, ReadOnly = true };
    private readonly TextBox _fragment = new() { Width = 360, Height = 80, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _status = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Button _toggle = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    private readonly Func<IWin32Window, string, Task>? _importFile;
    private readonly FlowLayoutPanel _advanced = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, Visible = false };

    /// <param name="importFile">Importa un file di configurazione (MainForm lo collega; senza, il pulsante non c'è).</param>
    internal VpnForm(IWireGuardTunnel tunnel, string? profilePath = null, Func<IWin32Window, string, Task>? importFile = null)
    {
        _importFile = importFile;
        _tunnel = tunnel;
        _profilePath = profilePath ?? VpnProfile.DefaultPath;
        _profile = VpnProfile.Load(_profilePath);
        if (_profile.Address.Length == 0)
            _profile.Address = WireGuardConfig.NextFreeAddress([]) ?? "";
        if (_profile.PcName.Length == 0)
            _profile.PcName = Environment.MachineName;

        Text = "VPN (WireGuard)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var gen = new Button { Text = "Crea chiavi", AutoSize = true };
        gen.Click += (_, _) => GenerateKeys();
        var copy = new Button { Text = "Copia il frammento", AutoSize = true };
        copy.Click += (_, _) => { if (_fragment.Text.Length > 0) Clipboard.SetText(_fragment.Text); };
        _toggle.Click += (_, _) => Toggle();

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        // Percorso normale: un file di configurazione e basta. I campi a mano stanno in «Avanzate».
        if (_importFile is not null)
        {
            layout.Controls.Add(new Label
            {
                Text = "Per usare la VPN basta caricare il file di configurazione che ti ha dato chi gestisce il server:\n"
                       + "il programma fa il resto (registra questo PC e accende la VPN). Niente da compilare.",
                AutoSize = true,
                MaximumSize = new Size(400, 0),
            });
            var load = new Button
            {
                Text = "Carica file di configurazione…", AutoSize = true, Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(3, 10, 3, 10), Font = new Font(Font, FontStyle.Bold),
            };
            load.Click += async (_, _) => await LoadConfigFileAsync();
            layout.Controls.Add(load);
        }
        layout.Controls.Add(new Label { Text = "Stato del tunnel", AutoSize = true, Margin = new Padding(3, 4, 3, 0) });
        layout.Controls.Add(_status);
        layout.Controls.Add(_toggle);

        var advancedToggle = new Button { Text = "Avanzate ▸", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
        advancedToggle.Click += (_, _) =>
        {
            _advanced.Visible = !_advanced.Visible;
            advancedToggle.Text = _advanced.Visible ? "Avanzate ▾" : "Avanzate ▸";
        };
        layout.Controls.Add(advancedToggle);
        void Row(string label, Control c)
        {
            _advanced.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
            _advanced.Controls.Add(c);
        }
        Row("Nome di questo PC (nel commento del frammento)", _pcName);
        Row("Indirizzo di questo PC nel tunnel", _address);
        Row("Endpoint del server", _endpoint);
        Row("Chiave pubblica del server (dal Synology)", _serverKey);
        _advanced.Controls.Add(gen);
        Row("Chiave pubblica di questo PC", _publicKey);
        Row("Da aggiungere alla configurazione WireGuard del server", _fragment);
        _advanced.Controls.Add(copy);
        // Senza file di configurazione (prove, altri usi) i campi restano a portata di mano.
        _advanced.Visible = _importFile is null;
        if (_importFile is null)
            advancedToggle.Visible = false;
        layout.Controls.Add(_advanced);
        Controls.Add(layout);

        _endpoint.Text = _profile.Endpoint;
        _pcName.Text = _profile.PcName;
        _address.Text = _profile.Address;
        _serverKey.Text = _profile.ServerPublicKey;
        _address.Leave += (_, _) => Persist();
        _endpoint.Leave += (_, _) => Persist();
        _serverKey.Leave += (_, _) => Persist();
        _pcName.Leave += (_, _) => Persist();

        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Dispose(); Persist(); };
        RefreshKeys();
        RefreshState();
    }

    private async Task LoadConfigFileAsync()
    {
        if (_importFile is null || MainForm.PickConfigFile(this) is not { } path)
            return;
        Persist();
        await _importFile(this, path);
        if (IsDisposed)
            return;
        // il file ha riscritto il profilo: si rileggono i campi
        var fresh = VpnProfile.Load(_profilePath);
        _profile.PcName = fresh.PcName;
        _profile.Address = fresh.Address;
        _profile.Endpoint = fresh.Endpoint;
        _profile.ServerPublicKey = fresh.ServerPublicKey;
        _profile.Dns = fresh.Dns;
        _profile.AllowedIPs = fresh.AllowedIPs;
        _profile.PersistentKeepalive = fresh.PersistentKeepalive;
        _profile.PrivateKeyProtected = fresh.PrivateKeyProtected;
        _pcName.Text = _profile.PcName;
        _address.Text = _profile.Address;
        _endpoint.Text = _profile.Endpoint;
        _serverKey.Text = _profile.ServerPublicKey;
        RefreshKeys();
        RefreshState();
    }

    private void Persist()
    {
        _profile.PcName = _pcName.Text.Trim();
        _profile.Address = _address.Text.Trim();
        _profile.Endpoint = _endpoint.Text.Trim();
        _profile.ServerPublicKey = _serverKey.Text.Trim();
        _profile.Save(_profilePath);
        RefreshKeys();
    }

    private void GenerateKeys()
    {
        if (_profile.HasKey && MessageBox.Show(this,
                "Esiste già una chiave. Crearne una nuova scollega questo PC dal server finché non aggiungi il nuovo frammento. Continuare?",
                "VPN", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _profile.GenerateKeys();
        Persist();
    }

    private void RefreshKeys()
    {
        var pub = _profile.PublicKey;
        _publicKey.Text = pub ?? "";
        var addrOk = WireGuardConfig.TryParseCidr(_profile.Address, out _, out _);
        _fragment.Text = pub is not null && addrOk
            ? WireGuardConfig.BuildServerPeerFragment(pub, _profile.Address, _profile.PcName).Replace("\n", "\r\n")
            : "";
    }

    private void Toggle()
    {
        Persist();
        try
        {
            Cursor = Cursors.WaitCursor;
            if (_tunnel.Stato is TunnelState.Acceso or TunnelState.InAvvio)
                _tunnel.Spegni();
            else
            {
                if (!_profile.HasKey)
                    throw new VpnException("Prima crea le chiavi.");
                if (!WireGuardNative.DllPresent)
                    throw new VpnException("Manca wireguard.dll nella cartella del programma.");
                _tunnel.Accendi(_profile.ToConfig());
            }
        }
        catch (Exception ex) when (ex is VpnException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshState();
        }
    }

    private static string Details(VpnStatus? d)
        => d is null
            ? ""
            : d.LastHandshakeUtc is { } hs
                ? $" (ultimo contatto col server {Math.Max(0, (int)(DateTime.UtcNow - hs).TotalSeconds)} s fa, inviati {d.TxBytes:N0} B, ricevuti {d.RxBytes:N0} B)"
                : " (in attesa del primo contatto col server)";

    private void RefreshState()
    {
        var s = _tunnel.Stato;
        _status.Text = s switch
        {
            TunnelState.Acceso => "Acceso" + Details(_tunnel.Dettagli),
            TunnelState.InAvvio => "In avvio…",
            TunnelState.InArresto => "In arresto…",
            TunnelState.Errore => "Errore",
            _ => "Spento",
        };
        _toggle.Text = s is TunnelState.Acceso or TunnelState.InAvvio ? "Spegni la VPN" : "Accendi la VPN";
    }
}
