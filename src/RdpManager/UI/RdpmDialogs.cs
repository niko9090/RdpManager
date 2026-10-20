using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Esportazione in .rdpm: password escluse oppure cifrate con una password del file.</summary>
public sealed class RdpmExportDialog : Form
{
    private readonly RadioButton _without = new() { Text = "Senza password", AutoSize = true, Checked = true };
    private readonly RadioButton _with = new() { Text = "Con le password, cifrate con una password del file", AutoSize = true };
    private readonly TextBox _password = new() { Width = 240, UseSystemPasswordChar = true, Enabled = false };
    private readonly TextBox _confirm = new() { Width = 240, UseSystemPasswordChar = true, Enabled = false };

    public RdpmExportDialog(int count)
    {
        Text = "Esporta connessioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        grid.Controls.Add(new Label { Text = "Password del file", AutoSize = true, Margin = new Padding(20, 6, 8, 3) });
        grid.Controls.Add(_password);
        grid.Controls.Add(new Label { Text = "Ripeti", AutoSize = true, Margin = new Padding(20, 6, 8, 3) });
        grid.Controls.Add(_confirm);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = $"{count} connessioni e le loro cartelle.", AutoSize = true, Margin = new Padding(3, 3, 3, 8) });
        layout.Controls.Add(_without);
        layout.Controls.Add(_with);
        layout.Controls.Add(grid);
        layout.Controls.Add(new Label
        {
            Text = "Cifratura AES-GCM. Senza la password del file le password salvate non si possono leggere: conservala a parte.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(20, 3, 3, 3),
        });

        var ok = new Button { Text = "Esporta…", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
        Controls.Add(buttons);

        _with.CheckedChanged += (_, _) => _password.Enabled = _confirm.Enabled = _with.Checked;
        ok.Click += (_, _) =>
        {
            if (!_with.Checked)
                return;
            string? problem = _password.Text.Length < 8 ? "La password del file deve avere almeno 8 caratteri."
                : _password.Text != _confirm.Text ? "Le due password non coincidono." : null;
            if (problem is null)
                return;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _password.Focus();
        };
    }

    /// <summary>null = password escluse dal file.</summary>
    public string? FilePassword => _with.Checked ? _password.Text : null;
}

/// <summary>Importazione da .rdpm: unione senza doppioni o sostituzione dell'elenco.</summary>
public sealed class RdpmImportDialog : Form
{
    private readonly RadioButton _merge = new() { Text = "Unisci: aggiungi solo le connessioni che mancano", AutoSize = true, Checked = true };
    private readonly RadioButton _replace = new() { Text = "Sostituisci: l'elenco attuale viene cancellato", AutoSize = true };

    public RdpmImportDialog(int incoming, int current, bool hasPasswords)
    {
        Text = "Importa connessioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = $"Nel file: {incoming} connessioni{(hasPasswords ? " (con password)" : " (senza password)")}. Nell'elenco: {current}.",
            AutoSize = true, Margin = new Padding(3, 3, 3, 8),
        });
        layout.Controls.Add(_merge);
        layout.Controls.Add(new Label
        {
            Text = "Doppione = stesso host, porta, utente e dominio. Al doppione già presente si aggiunge solo la password, se non l'aveva.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(20, 0, 3, 6),
        });
        layout.Controls.Add(_replace);

        var ok = new Button { Text = "Importa", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
        Controls.Add(buttons);

        ok.Click += (_, _) =>
        {
            if (_replace.Checked && current > 0 &&
                MessageBox.Show(this, $"Cancellare le {current} connessioni attuali e tenere solo quelle del file?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                DialogResult = DialogResult.None;
        };
    }

    public ImportMode Mode => _replace.Checked ? ImportMode.Replace : ImportMode.Merge;
}

/// <summary>Richiesta della password del file .rdpm.</summary>
public sealed class FilePasswordDialog : Form
{
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };

    private FilePasswordDialog(string fileName, bool retry)
    {
        Text = "Password del file";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        if (retry)
            layout.Controls.Add(new Label { Text = "Password sbagliata. Riprova.", AutoSize = true, ForeColor = Color.Firebrick });
        layout.Controls.Add(new Label { Text = $"«{fileName}» contiene password cifrate.", AutoSize = true });
        layout.Controls.Add(_password);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
        Controls.Add(buttons);
        ThemeManager.Apply(this);
    }

    public static string? Ask(IWin32Window owner, string fileName, bool retry)
    {
        using var dlg = new FilePasswordDialog(fileName, retry);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._password.Text : null;
    }
}
