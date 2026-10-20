using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Impostazioni comuni dei dialoghi semplici.</summary>
internal static class DialogStyle
{
    public static void Setup(Form f, string title, bool resizable = false)
    {
        f.Text = title;
        f.FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        f.MaximizeBox = resizable;
        f.MinimizeBox = false;
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.CenterParent;
        f.Padding = new Padding(10);
        f.Font = SystemFonts.MessageBoxFont ?? f.Font;
    }

    /// <summary>Riga di pulsanti in basso a destra (il primo è il più a destra).</summary>
    public static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    public static Button Button(string text, DialogResult result = DialogResult.None) =>
        new() { Text = text, AutoSize = true, DialogResult = result, MinimumSize = new Size(88, 0) };

    public static Label Note(string text, int width = 380) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(width, 0), Margin = new Padding(3, 3, 3, 8) };
}

// ---------- profili di credenziali ----------

/// <summary>Aggiunta o modifica di un profilo di credenziali. Modifica l'oggetto solo con OK.</summary>
internal sealed class ProfileEditDialog : Form
{
    private readonly CredentialProfile _target;
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _domain = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = "Mostra", AutoSize = true };
    private readonly string _originalPassword;

    public ProfileEditDialog(CredentialProfile target, string title)
    {
        _target = target;
        DialogStyle.Setup(this, title);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            grid.Controls.Add(control);
        }
        var pwd = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        pwd.Controls.AddRange([_password, _show]);
        Row("Nome", _name);
        Row("Utente", _user);
        Row("Dominio", _domain);
        Row("Password", pwd);
        grid.Controls.Add(new Label());
        grid.Controls.Add(DialogStyle.Note("Facoltativa. Cifrata con DPAPI per il tuo utente Windows; nei file .rdpm esclusa o cifrata con la password del file.", 300));

        var ok = DialogStyle.Button("OK", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(grid);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;

        _name.Text = target.Name;
        _user.Text = target.Username;
        _domain.Text = target.Domain;
        _originalPassword = target.Password ?? "";
        _password.Text = _originalPassword;
        if (target.Password is null && !string.IsNullOrEmpty(target.ProtectedPassword))
            _password.PlaceholderText = "(password salvata non leggibile da questo utente)";
        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_show.Checked;
        ok.Click += (_, _) =>
        {
            if (_user.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Indica l'utente.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            _target.Username = _user.Text.Trim();
            _target.Domain = _domain.Text.Trim();
            _target.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _target.Account;
            if (_password.Text != _originalPassword)
            {
                _target.Password = _password.Text.Length > 0 ? _password.Text : null;
                _target.ProtectedPassword = null;
            }
        };
        ThemeManager.Apply(this);
    }
}

/// <summary>Elenco dei profili di credenziali: nuovo, modifica, elimina. Lavora direttamente sul documento.</summary>
internal sealed class CredentialProfilesDialog : Form
{
    private readonly ConnectionsDocument _doc;
    private readonly ListBox _list = new() { Width = 420, Height = 220, IntegralHeight = false };

    public CredentialProfilesDialog(ConnectionsDocument doc)
    {
        _doc = doc;
        DialogStyle.Setup(this, "Profili di credenziali");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var add = DialogStyle.Button("Nuovo…");
        var edit = DialogStyle.Button("Modifica…");
        var delete = DialogStyle.Button("Elimina");
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        side.Controls.AddRange([add, edit, delete]);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange([_list, side]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(DialogStyle.Note("Un profilo tiene utente, dominio e password una volta sola: nella modifica di una connessione si sceglie in «Credenziali». Cambiando il profilo cambiano tutte le connessioni che lo usano.", 520));
        layout.Controls.Add(row);

        var close = DialogStyle.Button("Chiudi", DialogResult.OK);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(close));
        AcceptButton = close;
        CancelButton = close;

        add.Click += (_, _) => Add();
        edit.Click += (_, _) => Edit();
        delete.Click += (_, _) => Delete();
        _list.DoubleClick += (_, _) => Edit();
        _list.SelectedIndexChanged += (_, _) => edit.Enabled = delete.Enabled = _list.SelectedItem is not null;
        Fill(null);
        ThemeManager.Apply(this);
    }

    /// <summary>Vero se qualcosa è cambiato (da salvare).</summary>
    public bool Changed { get; private set; }

    private sealed record Entry(CredentialProfile Profile, int Uses)
    {
        public override string ToString() =>
            Profile.DisplayName + (Profile.HasPassword ? "" : "  (senza password)") + (Uses > 0 ? $"  · usato da {Uses}" : "");
    }

    private void Fill(CredentialProfile? select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _doc.CredentialProfiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            int i = _list.Items.Add(new Entry(p, _doc.ConnectionsUsingProfile(p.Id).Count));
            if (p == select)
                _list.SelectedIndex = i;
        }
        _list.EndUpdate();
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
        if (_list.Items.Count == 0)
            _list.Items.Add("(nessun profilo: «Nuovo…» per crearne uno)");
    }

    private CredentialProfile? Selected => (_list.SelectedItem as Entry)?.Profile;

    private void Add()
    {
        var p = new CredentialProfile();
        using var dlg = new ProfileEditDialog(p, "Nuovo profilo di credenziali");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _doc.CredentialProfiles.Add(p);
        Changed = true;
        Fill(p);
    }

    private void Edit()
    {
        if (Selected is not { } p)
            return;
        using var dlg = new ProfileEditDialog(p, $"Profilo: {p.DisplayName}");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        Changed = true;
        Fill(p);
    }

    private void Delete()
    {
        if (Selected is not { } p)
            return;
        int uses = _doc.ConnectionsUsingProfile(p.Id).Count;
        var message = uses == 0
            ? $"Eliminare il profilo «{p.DisplayName}»?"
            : $"Il profilo «{p.DisplayName}» è usato da {uses} connessioni: tengono utente e dominio, la password andrà reinserita se non ne hanno una propria.\n\nEliminarlo?";
        if (MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _doc.DeleteProfile(p.Id);
        Changed = true;
        Fill(null);
    }
}

// ---------- testi rapidi ----------

internal sealed class QuickTextEditDialog : Form
{
    private readonly QuickText _target;
    private readonly TextBox _name = new() { Width = 460 };
    private readonly TextBox _text = new() { Width = 460, Height = 200, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, AcceptsTab = true };

    public QuickTextEditDialog(QuickText target, string title)
    {
        _target = target;
        DialogStyle.Setup(this, title);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _text.Font = new Font(FontFamily.GenericMonospace, 10f);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Nome (facoltativo)", AutoSize = true });
        layout.Controls.Add(_name);
        layout.Controls.Add(new Label { Text = "Testo da digitare nella sessione", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        layout.Controls.Add(_text);
        layout.Controls.Add(DialogStyle.Note("Gli a capo diventano Invio. Non usare i testi rapidi per le password: sono salvati in chiaro.", 460));

        var ok = DialogStyle.Button("OK", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        CancelButton = cancel;

        _name.Text = target.Name;
        _text.Text = target.Text.ReplaceLineEndings("\r\n");
        ok.Click += (_, _) =>
        {
            if (QuickText.Validate(_name.Text, _text.Text) is { } error)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            _target.Name = _name.Text.Trim();
            _target.Text = _text.Text;
        };
        ThemeManager.Apply(this);
    }
}

/// <summary>Elenco dei testi rapidi: nuovo, modifica, elimina, ordine. Lavora direttamente sul documento.</summary>
internal sealed class QuickTextsDialog : Form
{
    private readonly ConnectionsDocument _doc;
    private readonly ListBox _list = new() { Width = 420, Height = 240, IntegralHeight = false };

    public QuickTextsDialog(ConnectionsDocument doc)
    {
        _doc = doc;
        DialogStyle.Setup(this, "Testi rapidi");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var add = DialogStyle.Button("Nuovo…");
        var edit = DialogStyle.Button("Modifica…");
        var delete = DialogStyle.Button("Elimina");
        var up = DialogStyle.Button("Su");
        var down = DialogStyle.Button("Giù");
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        side.Controls.AddRange([add, edit, delete, up, down]);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange([_list, side]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(DialogStyle.Note("Frammenti di testo da digitare nella sessione attiva: menu Sessione › Testi rapidi, «Invia» nella barra della sessione o «Testi» a schermo intero.", 520));
        layout.Controls.Add(row);

        var close = DialogStyle.Button("Chiudi", DialogResult.OK);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(close));
        CancelButton = close;

        add.Click += (_, _) => Add();
        edit.Click += (_, _) => Edit();
        delete.Click += (_, _) => Delete();
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        _list.DoubleClick += (_, _) => Edit();
        Fill(null);
        ThemeManager.Apply(this);
    }

    public bool Changed { get; private set; }

    private sealed record Entry(QuickText Text)
    {
        public override string ToString() =>
            Text.Name.Trim().Length > 0 ? $"{Text.Name.Trim()} — {QuickText.Preview(Text.Text, 40)}" : Text.DisplayName;
    }

    private QuickText? Selected => (_list.SelectedItem as Entry)?.Text;

    private void Fill(QuickText? select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in _doc.QuickTexts)
        {
            int i = _list.Items.Add(new Entry(t));
            if (t == select)
                _list.SelectedIndex = i;
        }
        _list.EndUpdate();
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void Add()
    {
        var t = new QuickText();
        using var dlg = new QuickTextEditDialog(t, "Nuovo testo rapido");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        _doc.QuickTexts.Add(t);
        Changed = true;
        Fill(t);
    }

    private void Edit()
    {
        if (Selected is not { } t)
            return;
        using var dlg = new QuickTextEditDialog(t, "Modifica testo rapido");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        Changed = true;
        Fill(t);
    }

    private void Delete()
    {
        if (Selected is not { } t)
            return;
        if (MessageBox.Show(this, $"Eliminare «{t.DisplayName}»?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _doc.QuickTexts.Remove(t);
        Changed = true;
        Fill(null);
    }

    private void MoveSelected(int delta)
    {
        if (Selected is { } t && _doc.MoveQuickText(t.Id, delta))
        {
            Changed = true;
            Fill(t);
        }
    }
}

// ---------- cronologia ----------

/// <summary>Cronologia delle connessioni: inizio, fine, durata, motivo; filtro per connessione e svuotamento.</summary>
internal sealed class HistoryDialog : Form
{
    private readonly ConnectionHistory _history;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, GridLines = false,
    };
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    private readonly List<(Guid? Id, string Text)> _choices = [(null, "Tutte le connessioni")];

    public HistoryDialog(ConnectionHistory history, ConnectionsDocument doc, Guid? connectionId)
    {
        _history = history;
        DialogStyle.Setup(this, "Cronologia delle connessioni", resizable: true);
        Size = new Size(900, 520);
        MinimumSize = new Size(600, 300);

        _list.Columns.Add("Connessione", 190);
        _list.Columns.Add("Host", 170);
        _list.Columns.Add("Inizio", 130);
        _list.Columns.Add("Fine", 130);
        _list.Columns.Add("Durata", 90);
        _list.Columns.Add("Motivo della fine", 260);

        // Connessioni dell'elenco più quelle eliminate che compaiono ancora nella cronologia.
        foreach (var g in history.Entries.GroupBy(e => e.ConnectionId))
        {
            var name = doc.Find(g.Key)?.DisplayName ?? g.Last().Name + " (eliminata)";
            _choices.Add((g.Key, name));
        }
        _choices.Sort(1, _choices.Count - 1, Comparer<(Guid?, string Text)>.Create((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Text, b.Text)));
        _filter.Items.AddRange(_choices.Select(c => (object)c.Text).ToArray());
        _filter.SelectedIndex = Math.Max(0, _choices.FindIndex(c => c.Id == connectionId));

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        top.Controls.AddRange([new Label { Text = "Mostra", AutoSize = true, Margin = new Padding(3, 7, 6, 3) }, _filter, _summary]);

        var clearShown = DialogStyle.Button("Svuota quelle mostrate");
        var close = DialogStyle.Button("Chiudi", DialogResult.OK);
        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(DialogStyle.Buttons(close, clearShown));
        CancelButton = close;

        _filter.SelectedIndexChanged += (_, _) => Fill();
        clearShown.Click += (_, _) => ClearShown();
        Fill();
        ThemeManager.Apply(this);
    }

    public bool Changed { get; private set; }

    internal ListView List => _list;

    private Guid? FilterId => _choices[Math.Max(0, _filter.SelectedIndex)].Id;

    private void Fill()
    {
        var entries = _history.Recent(FilterId);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in entries)
        {
            var item = new ListViewItem(e.Name) { Tag = e };
            item.SubItems.Add(e.Host);
            item.SubItems.Add(e.StartUtc.ToLocalTime().ToString("g"));
            item.SubItems.Add(e.EndUtc is { } end ? end.ToLocalTime().ToString("g") : "in corso");
            item.SubItems.Add(HistoryEntry.FormatDuration(e.Duration));
            item.SubItems.Add(e.Reason);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        var total = entries.Where(e => e.Duration is not null).Aggregate(TimeSpan.Zero, (s, e) => s + e.Duration!.Value);
        _summary.Text = $"{entries.Count} sessioni · tempo totale {HistoryEntry.FormatDuration(total)}";
    }

    private void ClearShown()
    {
        var id = FilterId;
        var what = id is null ? "tutta la cronologia" : $"la cronologia di «{_filter.Text}»";
        if (MessageBox.Show(this, $"Svuotare {what}?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (id is Guid g)
            _history.Clear(g);
        else
            _history.Clear();
        Changed = true;
        Fill();
    }
}

// ---------- cronologia di mstsc ----------

/// <summary>Scelta dei PC da importare dalla cronologia di mstsc (letta solo dopo la richiesta dell'utente).</summary>
internal sealed class MstscImportDialog : Form
{
    private readonly CheckedListBox _list = new() { Width = 480, Height = 260, CheckOnClick = true, IntegralHeight = false };
    private readonly IReadOnlyList<MstscHistoryItem> _items;

    public MstscImportDialog(IReadOnlyList<MstscHistoryItem> items, ConnectionsDocument doc)
    {
        _items = items;
        DialogStyle.Setup(this, "Importa dalla cronologia di mstsc");
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var item in items)
        {
            bool present = MstscHistory.IsAlreadyPresent(doc, item);
            var text = item.Address + (item.UsernameHint.Length > 0 ? $"  ({item.UsernameHint})" : "") + (present ? "  — già nell'elenco" : "");
            _list.Items.Add(text, !present);
        }
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(DialogStyle.Note($"PC che il client Desktop remoto di Windows ricorda per il tuo utente (solo indirizzi e nomi utente, mai password). Quelli spuntati vanno nella cartella «{MstscHistory.DefaultFolder}».", 480));
        layout.Controls.Add(_list);

        var ok = DialogStyle.Button("Importa", DialogResult.OK);
        var cancel = DialogStyle.Button("Annulla", DialogResult.Cancel);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(cancel, ok));
        AcceptButton = ok;
        CancelButton = cancel;
        ThemeManager.Apply(this);
    }

    public IReadOnlyList<MstscHistoryItem> CheckedItems =>
        _list.CheckedIndices.Cast<int>().Select(i => _items[i]).ToList();
}
