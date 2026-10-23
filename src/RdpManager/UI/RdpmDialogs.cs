using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Esportazione in .rdpm: password cifrate con una password del file (proposta quando ci sono password salvate,
/// dalla 1.5.1) oppure escluse, con un avviso se se ne perderebbero.
/// </summary>
public sealed class RdpmExportDialog : Form
{
    private readonly RadioButton _without = new() { Text = "Senza password", AutoSize = true };
    private readonly RadioButton _with = new() { Text = "Con le password, cifrate con una password del file", AutoSize = true };
    private readonly TextBox _password = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly TextBox _confirm = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly string? _withoutWarning;
    private readonly CheckBox _settings = new()
    {
        Text = "Includi le impostazioni dell'app (scorciatoie, tema, aggiornamenti…)", AutoSize = true, Margin = new Padding(3, 10, 3, 3),
    };

    public RdpmExportDialog(ConnectionsDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        int count = doc.Connections.Count;
        int passwords = RdpmFile.CountExportablePasswords(doc);
        _withoutWarning = RdpmFile.WithoutPasswordsWarning(doc);
        bool withPasswords = RdpmFile.ProposeWithPasswords(doc);
        _with.Checked = withPasswords;
        _without.Checked = !withPasswords;
        _password.Enabled = _confirm.Enabled = withPasswords;
        Text = "Esporta connessioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;
        Confirm = question => MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        ShowProblem = problem => MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        grid.Controls.Add(new Label { Text = "Password del file", AutoSize = true, Margin = new Padding(20, 6, 8, 3) });
        grid.Controls.Add(_password);
        grid.Controls.Add(new Label { Text = "Ripeti", AutoSize = true, Margin = new Padding(20, 6, 8, 3) });
        grid.Controls.Add(_confirm);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        var profiles = doc.CredentialProfiles.Count switch { 0 => "", 1 => ", 1 profilo di credenziali", var n => $", {n} profili di credenziali" };
        var saved = passwords switch { 0 => "nessuna password salvata", 1 => "1 password salvata", _ => $"{passwords} password salvate" };
        layout.Controls.Add(new Label
        {
            Text = $"{count} connessioni{profiles} e le loro cartelle; {saved}.", AutoSize = true, MaximumSize = new Size(380, 0),
            Margin = new Padding(3, 3, 3, 8),
        });
        layout.Controls.Add(_without);
        layout.Controls.Add(_with);
        layout.Controls.Add(grid);
        layout.Controls.Add(new Label
        {
            Text = "Cifratura AES-GCM. Senza la password del file le password salvate non si possono leggere: conservala a parte.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(20, 3, 3, 3),
        });
        layout.Controls.Add(_settings);
        layout.Controls.Add(new Label
        {
            Text = "Senza il monitor scelto e le sessioni da riaprire, che valgono solo per questo PC. Nessuna password.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(20, 0, 3, 3),
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
            if (!CanAccept())
                DialogResult = DialogResult.None;
        };
        Shown += (_, _) =>
        {
            if (_with.Checked)
                _password.Focus();
        };
    }

    /// <summary>Domanda sì/no (l'avviso «senza password»); le prove la sostituiscono.</summary>
    internal Func<string, bool> Confirm { get; set; }

    /// <summary>Messaggio di errore (password del file troppo corta o diversa); le prove lo sostituiscono.</summary>
    internal Action<string> ShowProblem { get; set; }

    internal bool WithPasswordsChosen => _with.Checked;

    /// <summary>Per le prove: sceglie con o senza password e scrive la password del file.</summary>
    internal void ChooseForTests(bool withPasswords, string password = "", string? confirm = null)
    {
        _with.Checked = withPasswords;
        _without.Checked = !withPasswords;
        _password.Text = password;
        _confirm.Text = confirm ?? password;
    }

    /// <summary>
    /// Controlli prima di esportare: con le password, la password del file (almeno 8 caratteri, ripetuta uguale);
    /// senza, la conferma dell'avviso se ci sono password salvate che non andrebbero nel file.
    /// </summary>
    internal bool CanAccept()
    {
        if (!_with.Checked)
            return _withoutWarning is null || Confirm(_withoutWarning);
        string? problem = _password.Text.Length < 8 ? "La password del file deve avere almeno 8 caratteri."
            : _password.Text != _confirm.Text ? "Le due password non coincidono." : null;
        if (problem is null)
            return true;
        ShowProblem(problem);
        _password.Focus();
        return false;
    }

    /// <summary>null = password escluse dal file.</summary>
    public string? FilePassword => _with.Checked ? _password.Text : null;

    public bool IncludeSettings => _settings.Checked;
}

/// <summary>Importazione da .rdpm: unione senza doppioni o sostituzione dell'elenco.</summary>
public sealed class RdpmImportDialog : Form
{
    private readonly RadioButton _merge = new() { Text = "Unisci: aggiungi solo le connessioni che mancano", AutoSize = true, Checked = true };
    private readonly RadioButton _replace = new() { Text = "Sostituisci: le connessioni attuali vengono cancellate (profili e testi rapidi restano)", AutoSize = true };
    private readonly CheckBox _applySettings = new()
    {
        Text = "Applica anche le impostazioni dell'app contenute nel file", AutoSize = true, Margin = new Padding(3, 10, 3, 3), Visible = false,
    };

    public RdpmImportDialog(int incoming, int current, bool hasPasswords, bool hasSettings = false)
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
        _applySettings.Visible = hasSettings;
        layout.Controls.Add(_applySettings);

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

    public bool ApplySettings => _applySettings.Visible && _applySettings.Checked;
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
