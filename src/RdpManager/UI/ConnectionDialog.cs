using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Finestra di aggiunta/modifica di una connessione. Modifica l'oggetto solo con OK.</summary>
public sealed class ConnectionDialog : Form
{
    private readonly RdpConnection _target;
    private readonly TextBox _name = new() { Width = 280 };
    private readonly TextBox _host = new() { Width = 280, PlaceholderText = "nome-pc, indirizzo IP o nome:porta" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly TextBox _user = new() { Width = 280 };
    private readonly TextBox _domain = new() { Width = 280 };
    private readonly TextBox _password = new() { Width = 280, UseSystemPasswordChar = true };
    private readonly CheckBox _showPassword = new() { Text = "Mostra", AutoSize = true };
    private readonly ComboBox _group = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly RadioButton _fit = new() { Text = "Adatta alla finestra", AutoSize = true };
    private readonly RadioButton _fixed = new() { Text = "Fissa:", AutoSize = true };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 8192, Width = 70 };
    private readonly NumericUpDown _height = new() { Minimum = 480, Maximum = 8192, Width = 70 };
    private readonly CheckBox _clipboard = new() { Text = "Condividi appunti", AutoSize = true };
    private readonly CheckBox _drives = new() { Text = "Condividi unità locali", AutoSize = true };
    private readonly CheckBox _printers = new() { Text = "Stampanti", AutoSize = true };
    private readonly CheckBox _microphone = new() { Text = "Microfono", AutoSize = true };
    private readonly ComboBox _audio = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _allMonitors = new() { Text = "Usa tutti i monitor", AutoSize = true };
    private readonly CheckBox _autoReconnect = new() { Text = "Riconnetti da sola, al massimo", AutoSize = true };
    private readonly NumericUpDown _attempts = new() { Minimum = 1, Maximum = ReconnectPolicy.MaxAllowedAttempts, Width = 50 };
    private readonly CheckBox _favorite = new() { Text = "Preferita ★", AutoSize = true };
    private readonly TextBox _label = new() { Width = 90, MaxLength = 12, PlaceholderText = "es. PROD" };
    private readonly Button _color = new() { Text = "Colore…", AutoSize = true };
    private readonly Button _noColor = new() { Text = "Nessuno", AutoSize = true };
    private readonly Panel _colorSwatch = new() { Size = new Size(22, 22), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 4, 3, 3) };
    private readonly TextBox _mac = new() { Width = 160, PlaceholderText = "AA:BB:CC:DD:EE:FF" };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly ComboBox _credentials = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _useGateway = new() { Text = "Passa da un Remote Desktop Gateway", AutoSize = true };
    private readonly TextBox _gatewayHost = new() { Width = 280, PlaceholderText = "gateway.azienda.it (o host:porta)" };
    private readonly ComboBox _gatewayCredentials = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly IReadOnlyList<CredentialProfile> _profiles;
    private bool _gatewayCredentialsTouched;
    private string? _chosenColor;
    private readonly string? _originalPassword;

    private static readonly (AudioMode Mode, string Text)[] AudioChoices =
    [
        (AudioMode.Local, "Su questo PC"),
        (AudioMode.Remote, "Sul PC remoto"),
        (AudioMode.None, "Nessun audio"),
    ];

    public ConnectionDialog(RdpConnection target, IEnumerable<string> groups, string title,
        IReadOnlyList<CredentialProfile>? profiles = null)
    {
        _target = target;
        _profiles = (profiles ?? []).OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            grid.Controls.Add(control);
        }
        FlowLayoutPanel Flow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            f.Controls.AddRange(items);
            return f;
        }

        Row("Nome", _name);
        Row("Host", _host);
        Row("Porta", _port);
        Row("Credenziali", Flow(_credentials, Help("«Proprie» usa utente, dominio e password qui sotto. Un profilo (menu Connessioni › Profili di credenziali) li prende da lì: cambiando il profilo cambiano tutte le connessioni che lo usano.")));
        Row("Utente", _user);
        Row("Dominio", _domain);
        Row("Password", Flow(_password, _showPassword));
        grid.Controls.Add(new Label());
        grid.Controls.Add(new Label
        {
            Text = "Facoltativa. Viene salvata cifrata (DPAPI, solo per il tuo utente Windows).",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(300, 0),
        });
        Row("Gateway", Flow(_useGateway, Help("Remote Desktop Gateway (RD Gateway): il PC remoto si raggiunge attraverso questo server, di solito da fuori dall'ufficio. Si salva anche nei file .rdp esportati.")));
        Row("", _gatewayHost);
        Row("Credenziali gateway", _gatewayCredentials);
        Row("Cartella", _group);
        Row("Risoluzione", Flow(_fit, _fixed, _width, new Label { Text = "×", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _height));
        Row("Risorse", Flow(_clipboard, _drives, _printers, _microphone));
        Row("Audio", _audio);
        Row("Monitor", Flow(_allMonitors, Help("A schermo intero la sessione occupa tutti i monitor come un unico desktop. Con «Apri con mstsc» usa la modalità multimonitor di Windows.")));
        Row("Riconnessione", Flow(_autoReconnect, _attempts, new Label { Text = "volte", AutoSize = true, Margin = new Padding(3, 6, 0, 0) },
            Help("Dopo una disconnessione non voluta (rete, server riavviato) l'app ritenta con attese crescenti: 2, 4, 8, 16, 30 secondi. Non ritenta se hai chiuso tu la sessione o se le credenziali sono sbagliate.")));
        Row("Elenco", Flow(_favorite, new Label { Text = "Etichetta", AutoSize = true, Margin = new Padding(10, 6, 3, 0) }, _label, _colorSwatch, _color, _noColor));
        Row("MAC", Flow(_mac, Help("Indirizzo della scheda di rete del PC remoto, per accenderlo con Wake-on-LAN (menu Connessioni › Accendi). Il PC deve avere il Wake-on-LAN attivo ed essere nella stessa rete.")));

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);

        _group.Items.AddRange(groups.Cast<object>().ToArray());
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        _fixed.CheckedChanged += (_, _) => _width.Enabled = _height.Enabled = _fixed.Checked;
        _autoReconnect.CheckedChanged += (_, _) => _attempts.Enabled = _autoReconnect.Checked;
        _audio.Items.AddRange(AudioChoices.Select(a => (object)a.Text).ToArray());
        _color.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = ToColor(_chosenColor) ?? Color.SteelBlue };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                SetColor($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        };
        _noColor.Click += (_, _) => SetColor(null);
        ok.Click += OnOk;

        _name.Text = target.Name;
        _host.Text = target.Host;
        _port.Value = Math.Clamp(target.Port, 1, 65535);
        _user.Text = target.Username;
        _domain.Text = target.Domain;
        _originalPassword = target.Password ?? "";
        _password.Text = _originalPassword;
        if (target.Password is null && !string.IsNullOrEmpty(target.ProtectedPassword))
            _password.PlaceholderText = "(password salvata non leggibile da questo utente)";
        _credentials.Items.Add("Proprie (qui sotto)");
        _gatewayCredentials.Items.Add("Le stesse della connessione");
        foreach (var p in _profiles)
        {
            _credentials.Items.Add("Profilo: " + p.DisplayName);
            _gatewayCredentials.Items.Add("Profilo: " + p.DisplayName);
        }
        _credentials.SelectedIndex = 1 + IndexOfProfile(target.CredentialProfileId);
        _credentials.SelectedIndexChanged += (_, _) => UpdateCredentialFields();
        UpdateCredentialFields();
        _useGateway.Checked = target.UseGateway;
        _gatewayHost.Text = target.GatewayHost;
        _gatewayCredentials.SelectedIndex = target.GatewayCredentials == GatewayCredentialsSource.Profile
            ? 1 + IndexOfProfile(target.GatewayProfileId)
            : 0;
        _useGateway.CheckedChanged += (_, _) => UpdateGatewayFields();
        _gatewayCredentials.SelectionChangeCommitted += (_, _) => _gatewayCredentialsTouched = true;
        UpdateGatewayFields();
        _group.Text = target.Group;
        _fit.Checked = target.ResolutionMode == ResolutionMode.FitToWindow;
        _fixed.Checked = !_fit.Checked;
        _width.Value = Math.Clamp(target.DesktopWidth, 640, 8192);
        _height.Value = Math.Clamp(target.DesktopHeight, 480, 8192);
        _width.Enabled = _height.Enabled = _fixed.Checked;
        _clipboard.Checked = target.RedirectClipboard;
        _drives.Checked = target.RedirectDrives;
        _printers.Checked = target.RedirectPrinters;
        _microphone.Checked = target.RedirectMicrophone;
        _audio.SelectedIndex = Math.Max(0, Array.FindIndex(AudioChoices, a => a.Mode == target.AudioMode));
        _allMonitors.Checked = target.UseAllMonitors;
        _autoReconnect.Checked = target.AutoReconnect;
        _attempts.Value = Math.Clamp(target.AutoReconnectMaxAttempts, 1, ReconnectPolicy.MaxAllowedAttempts);
        _attempts.Enabled = _autoReconnect.Checked;
        _favorite.Checked = target.IsFavorite;
        _label.Text = target.Label;
        _mac.Text = target.MacAddress;
        SetColor(target.Color);
    }

    /// <summary>Posizione del profilo nell'elenco ordinato, -1 se assente.</summary>
    private int IndexOfProfile(Guid? id) => id is Guid g ? _profiles.ToList().FindIndex(p => p.Id == g) : -1;

    private CredentialProfile? ChosenProfile =>
        _credentials.SelectedIndex > 0 ? _profiles[_credentials.SelectedIndex - 1] : null;

    private void UpdateCredentialFields()
    {
        bool own = ChosenProfile is null;
        _user.Enabled = _domain.Enabled = _password.Enabled = _showPassword.Enabled = own;
        if (ChosenProfile is { } p)
        {
            _user.PlaceholderText = p.Username;
            _domain.PlaceholderText = p.Domain;
            _password.PlaceholderText = p.HasPassword ? "(dal profilo)" : "(il profilo non ha password)";
        }
        else
        {
            _user.PlaceholderText = _domain.PlaceholderText = "";
            _password.PlaceholderText = _target.Password is null && !string.IsNullOrEmpty(_target.ProtectedPassword)
                ? "(password salvata non leggibile da questo utente)" : "";
        }
    }

    private void UpdateGatewayFields() =>
        _gatewayHost.Enabled = _gatewayCredentials.Enabled = _useGateway.Checked;

    /// <summary>Un «?» con la spiegazione nel suggerimento (e in un messaggio al clic).</summary>
    private Label Help(string text)
    {
        var mark = new Label
        {
            Text = "?", AutoSize = true, ForeColor = SystemColors.HotTrack, Cursor = Cursors.Help,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(6, 6, 3, 0),
        };
        _tips.SetToolTip(mark, text);
        mark.Click += (_, _) => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return mark;
    }

    private void SetColor(string? color)
    {
        _chosenColor = RdpConnection.NormalizeColor(color);
        _colorSwatch.BackColor = ToColor(_chosenColor) ?? SystemColors.Control;
        _noColor.Enabled = _chosenColor is not null;
    }

    internal static Color? ToColor(string? hex) =>
        RdpConnection.NormalizeColor(hex) is { } h ? ColorTranslator.FromHtml(h) : null;

    private void OnOk(object? sender, EventArgs e)
    {
        var (host, port) = RdpFile.SplitAddress(_host.Text);
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Indica l'host (nome o indirizzo del PC remoto).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _host.Focus();
            return;
        }

        if (_useGateway.Checked && _gatewayHost.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Indica l'host del gateway, oppure togli la spunta «Passa da un Remote Desktop Gateway».", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _gatewayHost.Focus();
            return;
        }

        string mac = "";
        if (_mac.Text.Trim().Length > 0 && !WakeOnLan.TryNormalizeMac(_mac.Text, out mac))
        {
            MessageBox.Show(this, "Indirizzo MAC non valido. Usa la forma AA:BB:CC:DD:EE:FF (oppure lascia vuoto).", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _mac.Focus();
            return;
        }

        _target.Host = host.Trim();
        _target.Port = port ?? (int)_port.Value;
        _target.Name = string.IsNullOrWhiteSpace(_name.Text) ? _target.Host : _name.Text.Trim();
        _target.Username = _user.Text.Trim();
        _target.Domain = _domain.Text.Trim();
        if (_password.Text != _originalPassword)
        {
            _target.Password = _password.Text.Length > 0 ? _password.Text : null;
            _target.ProtectedPassword = null;
        }
        _target.Group = RdpConnection.NormalizeGroup(_group.Text);
        _target.ResolutionMode = _fit.Checked ? ResolutionMode.FitToWindow : ResolutionMode.Fixed;
        _target.DesktopWidth = (int)_width.Value;
        _target.DesktopHeight = (int)_height.Value;
        _target.RedirectClipboard = _clipboard.Checked;
        _target.RedirectDrives = _drives.Checked;
        _target.RedirectPrinters = _printers.Checked;
        _target.RedirectMicrophone = _microphone.Checked;
        _target.AudioMode = AudioChoices[Math.Max(0, _audio.SelectedIndex)].Mode;
        _target.UseAllMonitors = _allMonitors.Checked;
        _target.AutoReconnect = _autoReconnect.Checked;
        _target.AutoReconnectMaxAttempts = (int)_attempts.Value;
        _target.IsFavorite = _favorite.Checked;
        _target.Label = _label.Text.Trim();
        _target.Color = _chosenColor;
        _target.MacAddress = mac;
        _target.CredentialProfileId = ChosenProfile?.Id;
        _target.UseGateway = _useGateway.Checked;
        _target.GatewayHost = _gatewayHost.Text.Trim();
        if (!_gatewayCredentialsTouched)
        {
            // Menu non toccato: resta com'era (es. «credenziali diverse da chiedere» importato da un .rdp).
        }
        else if (_gatewayCredentials.SelectedIndex > 0)
        {
            _target.GatewayCredentials = GatewayCredentialsSource.Profile;
            _target.GatewayProfileId = _profiles[_gatewayCredentials.SelectedIndex - 1].Id;
        }
        else
        {
            _target.GatewayCredentials = GatewayCredentialsSource.SameAsConnection;
            _target.GatewayProfileId = null;
        }

    }
}
