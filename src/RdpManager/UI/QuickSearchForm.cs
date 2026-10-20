using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Ricerca rapida (Ctrl+Alt+Spazio): si scrive parte del nome, dell'host o dell'etichetta (anche con un errore
/// di battitura) e Invio porta alla sessione aperta o ne apre una nuova. Frecce per scegliere, Esc per chiudere.
/// </summary>
internal sealed class QuickSearchForm : Form
{
    private readonly TextBox _query = new() { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
    private readonly ListBox _results = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false,
    };
    private readonly Label _hint = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
    private readonly IReadOnlyList<RdpConnection> _connections;
    private readonly IReadOnlyList<Guid> _openIds;
    private IReadOnlyList<QuickSearchResult> _found = [];

    public QuickSearchForm(IReadOnlyList<RdpConnection> connections, IReadOnlyList<Guid> openIds)
    {
        _connections = connections;
        _openIds = openIds;
        Text = "Ricerca rapida";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Size = new Size(560, 380);
        Padding = new Padding(8);
        _query.Font = new Font(Font.FontFamily, 13f);
        _query.PlaceholderText = "Nome, host o etichetta…";
        _results.ItemHeight = 40;
        _hint.Text = "Invio: apri o passa alla sessione · ↑↓ scegli · Esc chiudi";
        _hint.ForeColor = SystemColors.GrayText;

        var spacer = new Panel { Dock = DockStyle.Top, Height = 6 };
        Controls.Add(_results);
        Controls.Add(spacer);
        Controls.Add(_query);
        Controls.Add(_hint);

        _query.TextChanged += (_, _) => Search();
        _results.DrawItem += DrawResult;
        _results.DoubleClick += (_, _) => Accept();
        _results.Click += (_, _) => { if (_results.SelectedIndex >= 0) Accept(); };
        Shown += (_, _) => _query.Focus();
        Search();
    }

    /// <summary>Risultato scelto con Invio o clic (null se chiusa con Esc).</summary>
    public QuickSearchResult? Chosen { get; private set; }

    internal TextBox QueryBox => _query;
    internal IReadOnlyList<QuickSearchResult> Results => _found;

    /// <summary>Al centro in alto della finestra indicata (anche a schermo intero).</summary>
    public void PlaceOver(Rectangle ownerBounds)
    {
        Location = new Point(ownerBounds.Left + (ownerBounds.Width - Width) / 2, ownerBounds.Top + Math.Min(120, ownerBounds.Height / 6));
    }

    private void Search()
    {
        _found = QuickSearch.Find(_query.Text, _connections, _openIds);
        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var r in _found)
            _results.Items.Add(r);
        _results.EndUpdate();
        if (_results.Items.Count > 0)
            _results.SelectedIndex = 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyData)
        {
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                break;
            case Keys.Enter:
                Accept();
                break;
            case Keys.Down:
            case Keys.Up:
                if (_results.Items.Count > 0)
                {
                    int i = _results.SelectedIndex + (e.KeyData == Keys.Down ? 1 : -1);
                    _results.SelectedIndex = Math.Clamp(i, 0, _results.Items.Count - 1);
                }
                break;
            default:
                return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>Sceglie il risultato selezionato (anche per le prove automatiche).</summary>
    internal void Accept()
    {
        if (_results.SelectedItem is not QuickSearchResult r)
            return;
        Chosen = r;
        DialogResult = DialogResult.OK;
    }

    private void DrawResult(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _results.Items[e.Index] is not QuickSearchResult r)
            return;
        var p = ThemeManager.Current;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? p.Highlight : _results.BackColor))
            e.Graphics.FillRectangle(back, e.Bounds);
        var c = r.Connection;
        if (ConnectionDialog.ToColor(c.Color) is { } stripe)
        {
            using var b = new SolidBrush(stripe);
            e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 4, 4, e.Bounds.Height - 8);
        }
        var fore = selected ? p.HighlightText : _results.ForeColor;
        var gray = selected ? p.HighlightText : p.GrayText;
        using var bold = new Font(Font, FontStyle.Bold);
        var name = c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
        TextRenderer.DrawText(e.Graphics, name, bold, new Point(e.Bounds.X + 12, e.Bounds.Y + 3), fore);
        var detail = RdpFile.FormatAddress(c.Host, c.Port) + (c.Group.Length > 0 ? $" · {c.Group}" : "");
        TextRenderer.DrawText(e.Graphics, detail, Font, new Point(e.Bounds.X + 12, e.Bounds.Y + 21), gray);
        var kind = r.Kind == QuickSearchKind.OpenSession ? "Passa alla sessione" : "Apri";
        var size = TextRenderer.MeasureText(kind, Font);
        TextRenderer.DrawText(e.Graphics, kind, Font, new Point(e.Bounds.Right - size.Width - 8, e.Bounds.Y + 12), gray);
    }
}
