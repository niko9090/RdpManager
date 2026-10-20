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
    private readonly string? _originalPassword;

    public ConnectionDialog(RdpConnection target, IEnumerable<string> groups, string title)
    {
        _target = target;
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
        Row("Utente", _user);
        Row("Dominio", _domain);
        Row("Password", Flow(_password, _showPassword));
        grid.Controls.Add(new Label());
        grid.Controls.Add(new Label
        {
            Text = "Facoltativa. Viene salvata cifrata (DPAPI, solo per il tuo utente Windows).",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(300, 0),
        });
        Row("Cartella", _group);
        Row("Risoluzione", Flow(_fit, _fixed, _width, new Label { Text = "×", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _height));
        Row("Risorse", Flow(_clipboard, _drives));

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
        _group.Text = target.Group;
        _fit.Checked = target.ResolutionMode == ResolutionMode.FitToWindow;
        _fixed.Checked = !_fit.Checked;
        _width.Value = Math.Clamp(target.DesktopWidth, 640, 8192);
        _height.Value = Math.Clamp(target.DesktopHeight, 480, 8192);
        _width.Enabled = _height.Enabled = _fixed.Checked;
        _clipboard.Checked = target.RedirectClipboard;
        _drives.Checked = target.RedirectDrives;
    }

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
    }
}
