using System.Drawing;

namespace RdpManager.Core;

/// <summary>Griglia della Panoramica: quante colonne e righe e dove sta ogni miniatura.</summary>
public static class OverviewLayout
{
    public const int Margin = 12;

    /// <summary>Spazio sotto ogni miniatura per nome e stato.</summary>
    public const int CaptionHeight = 26;

    /// <summary>
    /// Colonne e righe che danno le miniature più grandi con il rapporto <paramref name="aspect"/> (larghezza/altezza)
    /// dentro <paramref name="area"/>. 0 elementi = 0×0.
    /// </summary>
    public static (int Columns, int Rows) Grid(int count, Size area, double aspect = 16.0 / 9.0)
    {
        if (count <= 0)
            return (0, 0);
        if (aspect <= 0)
            aspect = 16.0 / 9.0;
        int bestCols = 1;
        double bestWidth = -1;
        for (int cols = 1; cols <= count; cols++)
        {
            int rows = (count + cols - 1) / cols;
            double cellW = (area.Width - Margin * (cols + 1)) / (double)cols;
            double cellH = (area.Height - Margin * (rows + 1)) / (double)rows - CaptionHeight;
            double width = Math.Min(cellW, cellH * aspect);
            if (width > bestWidth + 0.5)
            {
                bestWidth = width;
                bestCols = cols;
            }
        }
        return (bestCols, (count + bestCols - 1) / bestCols);
    }

    /// <summary>Rettangoli delle miniature (senza la didascalia), centrati nell'area, riga per riga.</summary>
    public static IReadOnlyList<Rectangle> Cells(int count, Size area, double aspect = 16.0 / 9.0)
    {
        var (cols, rows) = Grid(count, area, aspect);
        var cells = new List<Rectangle>(count);
        if (cols == 0)
            return cells;
        double cellW = (area.Width - Margin * (cols + 1)) / (double)cols;
        double cellH = (area.Height - Margin * (rows + 1)) / (double)rows - CaptionHeight;
        int w = Math.Max(1, (int)Math.Min(cellW, cellH * aspect));
        int h = Math.Max(1, (int)(w / aspect));
        int gridW = cols * w + (cols - 1) * Margin;
        int gridH = rows * (h + CaptionHeight) + (rows - 1) * Margin;
        int left = Math.Max(Margin, (area.Width - gridW) / 2);
        int top = Math.Max(Margin, (area.Height - gridH) / 2);
        for (int i = 0; i < count; i++)
        {
            int r = i / cols, c = i % cols;
            cells.Add(new Rectangle(left + c * (w + Margin), top + r * (h + CaptionHeight + Margin), w, h));
        }
        return cells;
    }

    /// <summary>Indice della miniatura sotto il punto (didascalia compresa), oppure -1.</summary>
    public static int HitTest(IReadOnlyList<Rectangle> cells, Point p)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            var r = cells[i];
            if (new Rectangle(r.X, r.Y, r.Width, r.Height + CaptionHeight).Contains(p))
                return i;
        }
        return -1;
    }

    /// <summary>Miniatura dopo un tasto freccia nella griglia (senza uscire dai bordi).</summary>
    public static int Move(int index, int count, int columns, int dx, int dy)
    {
        if (count <= 0 || columns <= 0)
            return -1;
        index = Math.Clamp(index, 0, count - 1);
        int target = index + dx + dy * columns;
        if (dx != 0 && target / columns != index / columns)
            return index;
        return target >= 0 && target < count ? target : index;
    }
}

/// <summary>Divisione della vista affiancata fra due sessioni.</summary>
public static class SplitLayout
{
    public const double DefaultRatio = 0.5, MinRatio = 0.15, MaxRatio = 0.85;

    public static double ClampRatio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : DefaultRatio;

    /// <summary>Posizione del divisore (in pixel dal bordo sinistro) per la larghezza data.</summary>
    public static int SplitterDistance(int totalWidth, double ratio, int splitterWidth = 6)
    {
        int usable = Math.Max(0, totalWidth - splitterWidth);
        return (int)Math.Round(usable * ClampRatio(ratio));
    }

    /// <summary>Rapporto dalla posizione del divisore spostato dall'utente.</summary>
    public static double RatioFromDistance(int distance, int totalWidth, int splitterWidth = 6)
    {
        int usable = totalWidth - splitterWidth;
        return usable <= 0 ? DefaultRatio : ClampRatio(distance / (double)usable);
    }
}
