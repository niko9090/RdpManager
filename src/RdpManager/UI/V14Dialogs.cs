using RdpManager.Core;

namespace RdpManager.UI;

// ---------- blocco con PIN ----------

/// <summary>
/// Schermata di blocco: copre il monitor della finestra principale (che intanto è nascosta) e chiede il PIN.
/// «Ho dimenticato il PIN» toglie il PIN e cancella le password salvate, tenendo le connessioni.
/// </summary>
internal sealed class LockForm : Form
{
    private readonly TextBox _pin = new() { Width = 220, UseSystemPasswordChar = true, MaxLength = PinLock.MaxLength, TextAlign = HorizontalAlignment.Center };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(240, 120, 110), Text = "" };
    private readonly Func<string, bool> _tryUnlock;
    private int _failures;
    private DateTime _waitUntil = DateTime.MinValue;

    /// <param name="tryUnlock">Verifica il PIN e sblocca; falso se sbagliato.</param>
    public LockForm(Rectangle screen, Func<string, bool> tryUnlock)
    {
        _tryUnlock = tryUnlock;
        Text = "RdpManager è bloccato";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        BackColor = Color.FromArgb(24, 25, 29);
        ForeColor = Color.FromArgb(235, 235, 240);
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 11f);
        KeyPreview = true;

        var title = new Label { Text = "RdpManager è bloccato", AutoSize = true, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold) };
        var hint = new Label { Text = "Le sessioni restano aperte ma nascoste; le password salvate restano cifrate finché non sblocchi.", AutoSize = true, ForeColor = Color.FromArgb(160, 162, 170), MaximumSize = new Size(520, 0) };
        _pin.Font = new Font(Font.FontFamily, 16f);
        var unlock = new Button { Text = "Sblocca", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 95, 165), ForeColor = Color.White };
        var forgot = new LinkLabel { Text = "Ho dimenticato il PIN", AutoSize = true, LinkColor = Color.FromArgb(120, 170, 255) };
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = BackColor };
        panel.Controls.AddRange([title, hint, new Label { Text = "PIN", AutoSize = true, Margin = new Padding(3, 18, 3, 3) }, _pin, unlock, _error, forgot]);
        Controls.Add(panel);
        Resize += (_, _) => panel.Location = new Point((ClientSize.Width - panel.Width) / 2, (ClientSize.Height - panel.Height) / 2);
        Shown += (_, _) => { panel.Location = new Point((ClientSize.Width - panel.Width) / 2, (ClientSize.Height - panel.Height) / 2); _pin.Focus(); };

        AcceptButton = unlock;
        unlock.Click += (_, _) => TryUnlock();
        forgot.LinkClicked += (_, _) => ForgotRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>L'utente dice di aver dimenticato il PIN (chi apre la schermata chiede conferma).</summary>
    public event EventHandler? ForgotRequested;

    internal TextBox PinBox => _pin;
    internal string ErrorText => _error.Text;

    /// <summary>Prova il PIN scritto (anche per le prove automatiche).</summary>
    internal void TryUnlock()
    {
        if (DateTime.UtcNow < _waitUntil)
        {
            _error.Text = $"Troppi tentativi: riprova tra {(int)Math.Ceiling((_waitUntil - DateTime.UtcNow).TotalSeconds)} s.";
            return;
        }
        if (_tryUnlock(_pin.Text))
        {
            _pin.Clear();
            return;
        }
        _failures++;
        _pin.Clear();
        // Dopo 5 tentativi sbagliati, attese crescenti (5, 10, 20… s, al massimo 5 minuti).
        if (_failures >= 5)
            _waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(300, 5 << Math.Min(_failures - 5, 6)));
        _error.Text = "PIN sbagliato.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Si chiude solo dopo lo sblocco (chi l'ha aperta la chiude); Alt+F4 non sblocca.
        if (e.CloseReason == CloseReason.UserClosing && !AllowClose)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    public bool AllowClose { get; set; }
}

/// <summary>Impostazione del PIN: due volte, solo cifre.</summary>
internal sealed class PinSetDialog : Form
{
    private readonly TextBox _pin = new() { Width = 180, UseSystemPasswordChar = true, MaxLength = PinLock.MaxLength };
    private readonly TextBox _confirm = new() { Width = 180, UseSystemPasswordChar = true, MaxLength = PinLock.MaxLength };

    public PinSetDialog()
    {
        DialogStyle.Setup(this, "PIN di blocco");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.Controls.Add(new Label { Text = "PIN", AutoSize = true, Margin = new Padding(3, 7, 10, 3) });
        grid.Controls.Add(_pin);
        grid.Controls.Add(new Label { Text = "Ripeti", AutoSize = true, Margin = new Padding(3, 7, 10, 3) });
        grid.Controls.Add(_confirm);
        grid.Controls.Add(new Label());
        grid.Controls.Add(DialogStyle.Note($"Da {PinLock.MinLength} a {PinLock.MaxLength} cifre. Si salva solo un'impronta (PBKDF2): se lo dimentichi le connessioni restano, ma le password salvate vanno reinserite.", 260));
        var ok = DialogStyle.Button("OK", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(grid);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            var error = PinLock.Validate(_pin.Text) ?? (_pin.Text != _confirm.Text ? "I due PIN non coincidono." : null);
            if (error is null)
                return;
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        };
        ThemeManager.Apply(this);
    }

    /// <summary>Impronta del PIN scelto (si calcola qui: il PIN non esce dal dialogo).</summary>
    public PinHash? Result => DialogResult == DialogResult.OK ? PinLock.Create(_pin.Text) : null;
}

// ---------- importazione CSV ----------

/// <summary>Anteprima dell'importazione da CSV: associazione delle colonne ai campi, cartella, esito di ogni riga.</summary>
internal sealed class CsvImportDialog : Form
{
    private readonly CsvTable _table;
    private readonly ConnectionsDocument _doc;
    private readonly FlowLayoutPanel _columns = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, MaximumSize = new Size(1000, 0) };
    private readonly List<ComboBox> _maps = [];
    private readonly TextBox _folder = new() { Width = 220 };
    private readonly ListView _preview = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly Button _ok = DialogStyle.Button("Importa", DialogResult.OK);
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    private IReadOnlyList<CsvRow> _rows = [];

    public CsvImportDialog(string fileName, CsvTable table, ConnectionsDocument doc, string folder)
    {
        _table = table;
        _doc = doc;
        DialogStyle.Setup(this, $"Importa da CSV: {fileName}", resizable: true);
        Size = new Size(980, 600);
        MinimumSize = new Size(700, 400);

        var guessed = CsvImport.GuessMapping(table.Header);
        for (int i = 0; i < table.Header.Count; i++)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            box.Items.AddRange(Enum.GetValues<CsvField>().Select(f => (object)CsvImport.Describe(f)).ToArray());
            box.SelectedIndex = (int)guessed[i];
            box.SelectedIndexChanged += (_, _) => Refill();
            _maps.Add(box);
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(3, 3, 10, 3) };
            cell.Controls.Add(new Label { Text = table.Header[i].Length > 0 ? table.Header[i] : $"(colonna {i + 1})", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            cell.Controls.Add(box);
            _columns.Controls.Add(cell);
        }

        _preview.Columns.Add("Riga", 50);
        _preview.Columns.Add("Nome", 160);
        _preview.Columns.Add("Host", 170);
        _preview.Columns.Add("Porta", 55);
        _preview.Columns.Add("Utente", 120);
        _preview.Columns.Add("Cartella", 140);
        _preview.Columns.Add("Esito", 230);

        _folder.Text = folder;
        _folder.TextChanged += (_, _) => Refill();
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        top.Controls.AddRange([new Label { Text = $"Separatore «{table.Separator}» · cartella per le righe senza cartella", AutoSize = true, Margin = new Padding(3, 7, 6, 3) }, _folder, _summary]);
        var head = DialogStyle.Note("Scegli per ogni colonna il campo della connessione (riconosciuti in automatico dall'intestazione). Le righe senza host, con porta o MAC non validi e i doppioni non vengono importati.", 900);
        head.Dock = DockStyle.Top;

        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(_preview);
        Controls.Add(top);
        Controls.Add(_columns);
        Controls.Add(head);
        Controls.Add(DialogStyle.Buttons(cancel, _ok));
        CancelButton = cancel;
        Refill();
        ThemeManager.Apply(this);
    }

    public IReadOnlyList<CsvRow> Rows => _rows;
    internal IReadOnlyList<ComboBox> MappingBoxes => _maps;
    internal ListView PreviewList => _preview;

    private IReadOnlyList<CsvField> Mapping => _maps.Select(b => (CsvField)Math.Max(0, b.SelectedIndex)).ToList();

    private void Refill()
    {
        _rows = CsvImport.Preview(_table, Mapping, _doc, _folder.Text);
        _preview.BeginUpdate();
        _preview.Items.Clear();
        foreach (var r in _rows)
        {
            var c = r.Connection;
            var item = new ListViewItem(r.Line.ToString());
            item.SubItems.AddRange([c.Name, c.Host, c.Port.ToString(), c.Username, c.Group,
                r.Error is { } e ? "Esclusa: " + e : r.Duplicate ? "Esclusa: c'è già" : "Da importare"]);
            if (!r.CanImport)
                item.ForeColor = ThemeManager.Current.GrayText;
            _preview.Items.Add(item);
        }
        _preview.EndUpdate();
        int ok = _rows.Count(r => r.CanImport);
        _summary.Text = $"{ok} da importare, {_rows.Count - ok} escluse";
        _ok.Text = $"Importa {ok}";
        _ok.Enabled = ok > 0;
    }
}

// ---------- riepilogo delle scorciatoie (F1) ----------

internal sealed class ShortcutsDialog : Form
{
    public ShortcutsDialog(IReadOnlyList<ShortcutLine> lines)
    {
        DialogStyle.Setup(this, "Scorciatoie da tastiera", resizable: true);
        Size = new Size(640, 620);
        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        list.Columns.Add("Tasti", 230);
        list.Columns.Add("Azione", 360);
        foreach (var section in lines.GroupBy(l => l.Section))
        {
            var group = new ListViewGroup(section.Key);
            list.Groups.Add(group);
            foreach (var l in section)
                list.Items.Add(new ListViewItem([l.Keys, l.Action], group));
        }
        var note = DialogStyle.Note("Le scorciatoie della prima sezione si cambiano in File › Impostazioni › Scorciatoie; quelle degli spazi di lavoro in Spazi di lavoro › Gestisci.", 600);
        note.Dock = DockStyle.Top;
        var close = DialogStyle.Button("Chiudi", DialogResult.OK);
        Controls.Add(list);
        Controls.Add(note);
        Controls.Add(DialogStyle.Buttons(close));
        AcceptButton = close;
        CancelButton = close;
        ThemeManager.Apply(this);
        List = list;
    }

    internal ListView List { get; }
}

// ---------- spazi di lavoro ----------

internal sealed class WorkspacesDialog : Form
{
    private readonly ConnectionsDocument _doc;
    private readonly ListBox _list = new() { Width = 380, Height = 220, IntegralHeight = false };

    public WorkspacesDialog(ConnectionsDocument doc)
    {
        _doc = doc;
        DialogStyle.Setup(this, "Spazi di lavoro");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var rename = DialogStyle.Button("Rinomina…");
        var shortcut = DialogStyle.Button("Scorciatoia…");
        var delete = DialogStyle.Button("Elimina");
        var up = DialogStyle.Button("Su");
        var down = DialogStyle.Button("Giù");
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        side.Controls.AddRange([rename, shortcut, delete, up, down]);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange([_list, side]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(DialogStyle.Note("Uno spazio di lavoro riapre le sue connessioni con lo stesso layout. Si salva da Spazi di lavoro › Salva lo spazio attuale.", 520));
        layout.Controls.Add(row);
        var close = DialogStyle.Button("Chiudi", DialogResult.OK);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(close));
        CancelButton = close;

        rename.Click += (_, _) =>
        {
            if (Selected is not { } w || TextPromptDialog.Ask(this, "Rinomina", "Nome dello spazio di lavoro:", w.Name) is not { Length: > 0 } name)
                return;
            w.Name = name;
            Changed = true;
            Fill(w);
        };
        shortcut.Click += (_, _) =>
        {
            if (Selected is not { } w)
                return;
            var text = TextPromptDialog.Ask(this, "Scorciatoia", "Combinazione (es. Ctrl+Alt+Maiusc+1; vuoto = nessuna):", w.Shortcut);
            if (text is null)
                return;
            if (text.Length > 0 && !GlobalHotkey.TryParse(text, out _))
            {
                MessageBox.Show(this, "Combinazione non valida: serve Ctrl o Alt (es. Ctrl+Alt+Maiusc+1).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var combo = text.Length > 0 ? KeyCombo.Parse(text) : default;
            var clash = combo.IsEmpty ? null
                : GlobalHotkey.ConflictWith(combo, doc.Settings.GetShortcuts()) is { } a ? ShortcutMap.Describe(a)
                : doc.Workspaces.FirstOrDefault(o => o != w && o.Shortcut.Length > 0 && KeyCombo.Parse(o.Shortcut) == combo)?.Name;
            if (clash is not null)
            {
                MessageBox.Show(this, $"{combo} è già usata per «{clash}».", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            w.Shortcut = combo.IsEmpty ? "" : combo.ToString();
            Changed = true;
            Fill(w);
        };
        delete.Click += (_, _) =>
        {
            if (Selected is not { } w || MessageBox.Show(this, $"Eliminare lo spazio di lavoro «{w.Name}»? Le connessioni restano.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            doc.Workspaces.Remove(w);
            Changed = true;
            Fill(null);
        };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        Fill(null);
        ThemeManager.Apply(this);
    }

    public bool Changed { get; private set; }

    private sealed record Entry(Workspace W)
    {
        public override string ToString() => $"{W.Name}  ({W.Connections.Count} connessioni{(W.Shortcut.Length > 0 ? " · " + W.Shortcut : "")})";
    }

    private Workspace? Selected => (_list.SelectedItem as Entry)?.W;

    private void Fill(Workspace? select)
    {
        _list.Items.Clear();
        foreach (var w in _doc.Workspaces)
        {
            int i = _list.Items.Add(new Entry(w));
            if (w == select)
                _list.SelectedIndex = i;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void MoveSelected(int delta)
    {
        if (Selected is not { } w)
            return;
        int from = _doc.Workspaces.IndexOf(w), to = Math.Clamp(from + delta, 0, _doc.Workspaces.Count - 1);
        if (from == to)
            return;
        _doc.Workspaces.RemoveAt(from);
        _doc.Workspaces.Insert(to, w);
        Changed = true;
        Fill(w);
    }
}

// ---------- scelta da un elenco (azioni di gruppo) ----------

internal sealed class ChoiceDialog : Form
{
    private readonly ComboBox _choice = new() { Width = 320 };

    /// <param name="editable">Vero = si può anche scrivere un valore nuovo (es. una cartella nuova).</param>
    public ChoiceDialog(string title, string label, IEnumerable<string> choices, string initial, bool editable)
    {
        DialogStyle.Setup(this, title);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _choice.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
        _choice.Items.AddRange(choices.Cast<object>().ToArray());
        if (editable)
            _choice.Text = initial;
        else
            _choice.SelectedIndex = Math.Max(0, _choice.Items.IndexOf(initial));
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = label, AutoSize = true });
        layout.Controls.Add(_choice);
        var ok = DialogStyle.Button("OK", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
        ThemeManager.Apply(this);
    }

    public string Value => _choice.Text.Trim();
    public int SelectedIndex => _choice.SelectedIndex;
}
