using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Impostazioni comuni di una cartella: profilo di credenziali e risoluzione, ereditati dalle connessioni contenute.</summary>
internal sealed class FolderSettingsDialog : TouchForm
{
    private readonly ComboBox _profile = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _useResolution = new() { Text = "Imposta una risoluzione", AutoSize = true };
    private readonly RadioButton _fit = new() { Text = "Adatta alla finestra", AutoSize = true, Checked = true };
    private readonly RadioButton _fixed = new() { Text = "Fissa", AutoSize = true };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 8192, Width = 70, Value = 1920 };
    private readonly NumericUpDown _height = new() { Minimum = 480, Maximum = 8192, Width = 70, Value = 1080 };
    private readonly List<CredentialProfile> _profiles;

    public FolderSettingsDialog(string path, FolderSettings? current, IEnumerable<CredentialProfile> profiles)
    {
        DialogStyle.Setup(this, $"Impostazioni della cartella «{path}»");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _profiles = profiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

        _profile.Items.Add("(nessuno: ogni connessione ha le sue)");
        foreach (var p in _profiles)
            _profile.Items.Add(p.DisplayName);
        _profile.SelectedIndex = current?.CredentialProfileId is Guid id ? _profiles.FindIndex(p => p.Id == id) + 1 : 0;
        if (_profile.SelectedIndex < 0)
            _profile.SelectedIndex = 0;

        _useResolution.Checked = current?.Resolution is not null;
        _fixed.Checked = current?.Resolution == ResolutionMode.Fixed;
        _fit.Checked = !_fixed.Checked;
        _width.Value = Math.Clamp(current?.DesktopWidth ?? 1920, 640, 8192);
        _height.Value = Math.Clamp(current?.DesktopHeight ?? 1080, 480, 8192);
        void Sync()
        {
            _fit.Enabled = _fixed.Enabled = _useResolution.Checked;
            _width.Enabled = _height.Enabled = _useResolution.Checked && _fixed.Checked;
        }
        _useResolution.CheckedChanged += (_, _) => Sync();
        _fixed.CheckedChanged += (_, _) => Sync();
        Sync();

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Profilo di credenziali per le connessioni della cartella:", AutoSize = true });
        layout.Controls.Add(_profile);
        layout.Controls.Add(DialogStyle.Note("Vale per le connessioni senza utente, password o profilo propri, anche nelle sottocartelle."));
        layout.Controls.Add(_useResolution);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange([_fit, _fixed, _width, new Label { Text = "×", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _height]);
        layout.Controls.Add(row);
        layout.Controls.Add(DialogStyle.Note("Vale per le connessioni che hanno «Usa la risoluzione della cartella» nella loro finestra di modifica."));
        var ok = DialogStyle.Button("OK", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
        ThemeManager.Apply(this);
    }

    public Guid? ProfileId => _profile.SelectedIndex > 0 ? _profiles[_profile.SelectedIndex - 1].Id : null;

    public ResolutionMode? Resolution =>
        !_useResolution.Checked ? null : _fixed.Checked ? ResolutionMode.Fixed : ResolutionMode.FitToWindow;

    public int DesktopWidth => (int)_width.Value;
    public int DesktopHeight => (int)_height.Value;
}
