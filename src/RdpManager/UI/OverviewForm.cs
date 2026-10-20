using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Panoramica (Ctrl+Alt+G): griglia di miniature dal vivo delle sessioni connesse. È una finestra a sé, senza
/// bordi, sopra quella principale: così la cattura delle sessioni (PrintWindow) non riprende la panoramica stessa.
/// Un clic (o Invio) porta alla sessione; Esc o di nuovo Ctrl+Alt+G chiude.
/// </summary>
internal sealed class OverviewForm : Form
{
    public sealed class Tile(RdpSessionView view)
    {
        public RdpSessionView View { get; } = view;
        public Bitmap? Thumbnail { get; set; }
        public DateTime? CapturedUtc { get; set; }
    }

    private const int HeaderHeight = 44;
    private static readonly Color Back = Color.FromArgb(24, 25, 29);
    private static readonly Color TileBack = Color.FromArgb(40, 42, 48);
    private static readonly Color Accent = Color.FromArgb(70, 140, 230);

    private readonly List<Tile> _tiles = [];
    private IReadOnlyList<Rectangle> _cells = [];
    private int _selected;
    private int _hover = -1;
    private int _columns = 1;

    public OverviewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        KeyPreview = true;
        DoubleBuffered = true;
        Text = "Panoramica - RdpManager";
        Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 10f);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>
    /// Vero se la panoramica è esclusa dalle catture dello schermo (Windows 10 2004 e successivi): allora una copia
    /// dello schermo nella zona di una sessione mostra la sessione che sta sotto, non la panoramica.
    /// </summary>
    public bool ExcludedFromCapture { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { ExcludedFromCapture = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); }
        catch (EntryPointNotFoundException) { ExcludedFromCapture = false; }
    }

    /// <summary>Una sessione scelta (clic, Invio): chi ha aperto la panoramica la porta in primo piano.</summary>
    public event EventHandler<RdpSessionView>? SessionChosen;

    public IReadOnlyList<Tile> Tiles => _tiles;

    /// <summary>Rettangoli delle miniature nell'area client (per le prove automatiche).</summary>
    internal IReadOnlyList<Rectangle> Cells => _cells;

    internal int SelectedIndex => _selected;

    public void SetSessions(IEnumerable<RdpSessionView> views, RdpSessionView? current)
    {
        foreach (var t in _tiles)
            t.Thumbnail?.Dispose();
        _tiles.Clear();
        _tiles.AddRange(views.Select(v => new Tile(v)));
        _selected = Math.Max(0, _tiles.FindIndex(t => t.View == current));
        Relayout();
    }

    /// <summary>Nuova immagine di una sessione (ridotta a una miniatura); null = immagine non disponibile ora.</summary>
    public void UpdateThumbnail(RdpSessionView view, Bitmap? capture)
    {
        var tile = _tiles.FirstOrDefault(t => t.View == view);
        if (tile is null || capture is null)
        {
            capture?.Dispose();
            return;
        }
        var old = tile.Thumbnail;
        tile.Thumbnail = Shrink(capture, 640);
        tile.CapturedUtc = DateTime.UtcNow;
        if (!ReferenceEquals(tile.Thumbnail, capture))
            capture.Dispose();
        old?.Dispose();
        Invalidate();
    }

    private static Bitmap Shrink(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth)
            return source;
        int h = Math.Max(1, source.Height * maxWidth / source.Width);
        var small = new Bitmap(maxWidth, h);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(source, 0, 0, maxWidth, h);
        return small;
    }

    private void Relayout()
    {
        var area = new Size(ClientSize.Width, Math.Max(1, ClientSize.Height - HeaderHeight));
        _cells = OverviewLayout.Cells(_tiles.Count, area)
            .Select(r => new Rectangle(r.X, r.Y + HeaderHeight, r.Width, r.Height)).ToList();
        _columns = Math.Max(1, OverviewLayout.Grid(_tiles.Count, area).Columns);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        using var text = new SolidBrush(Color.FromArgb(235, 235, 240));
        using var gray = new SolidBrush(Color.FromArgb(160, 162, 170));
        using var title = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        g.DrawString("Panoramica", title, text, 16, 10);
        g.DrawString(_tiles.Count == 0
                ? "Nessuna sessione connessa. Esc per chiudere."
                : "Clic o Invio per passare alla sessione · frecce per scegliere · Esc per chiudere",
            Font, gray, 150, 15);

        using var tileBack = new SolidBrush(TileBack);
        for (int i = 0; i < _tiles.Count && i < _cells.Count; i++)
        {
            var tile = _tiles[i];
            var r = _cells[i];
            if (tile.Thumbnail is { } bmp)
                g.DrawImage(bmp, r);
            else
            {
                g.FillRectangle(tileBack, r);
                using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("Anteprima in arrivo…", Font, gray, r, center);
            }
            if (ConnectionDialog.ToColor(tile.View.Connection.Color) is { } stripe)
            {
                using var b = new SolidBrush(stripe);
                g.FillRectangle(b, r.X, r.Y, 6, r.Height);
            }
            bool selected = i == _selected, hover = i == _hover;
            if (selected || hover)
            {
                using var pen = new Pen(selected ? Accent : Color.FromArgb(120, 125, 135), selected ? 4 : 2);
                g.DrawRectangle(pen, Rectangle.Inflate(r, 2, 2));
            }

            var c = tile.View.Connection;
            using var dot = new SolidBrush(RdpSessionView.StateColor(tile.View.State));
            g.FillEllipse(dot, r.X, r.Bottom + 9, 9, 9);
            var caption = (i < 9 ? $"{i + 1}  " : "") + c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
            var captionRect = new RectangleF(r.X + 14, r.Bottom + 5, r.Width - 14, OverviewLayout.CaptionHeight - 4);
            using var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(caption, Font, text, captionRect, trim);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = OverviewLayout.HitTest(_cells, e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int hit = OverviewLayout.HitTest(_cells, e.Location);
        if (e.Button == MouseButtons.Left && hit >= 0)
            Choose(hit);
        else if (hit < 0 && e.Y > HeaderHeight)
            Close();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        int cols = _columns;
        switch (keyData)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Enter or Keys.Space:
                if (_tiles.Count > 0)
                    Choose(_selected);
                return true;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                if (_tiles.Count > 0)
                {
                    int dx = keyData == Keys.Left ? -1 : keyData == Keys.Right ? 1 : 0;
                    int dy = keyData == Keys.Up ? -1 : keyData == Keys.Down ? 1 : 0;
                    _selected = OverviewLayout.Move(_selected, _tiles.Count, cols, dx, dy);
                    Invalidate();
                }
                return true;
        }
        if (keyData is >= Keys.D1 and <= Keys.D9 && keyData - Keys.D1 < _tiles.Count)
        {
            Choose(keyData - Keys.D1);
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>Sceglie la miniatura <paramref name="index"/> (anche per le prove automatiche).</summary>
    internal void Choose(int index)
    {
        if (index < 0 || index >= _tiles.Count)
            return;
        var view = _tiles[index].View;
        Close();
        SessionChosen?.Invoke(this, view);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var t in _tiles)
                t.Thumbnail?.Dispose();
        base.Dispose(disposing);
    }
}
