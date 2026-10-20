using System.Drawing;

namespace RdpManager.Core;

/// <summary>Un monitor come lo vede l'app: area intera, area di lavoro (senza barra delle applicazioni) e DPI.</summary>
public sealed record ScreenInfo(string DeviceName, Rectangle Bounds, Rectangle WorkingArea, bool Primary, int Dpi = 96)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>Scelta del monitor e ricollocazione delle finestre fra più schermi, senza dipendere da WinForms.</summary>
public static class MonitorLayout
{
    /// <summary>
    /// Lo schermo salvato (per nome di dispositivo) se c'è ancora; altrimenti il principale, altrimenti il primo.
    /// null solo se l'elenco è vuoto.
    /// </summary>
    public static ScreenInfo? ChooseScreen(IReadOnlyList<ScreenInfo> screens, string? preferredDevice)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (!string.IsNullOrWhiteSpace(preferredDevice)
            && screens.FirstOrDefault(s => s.DeviceName.Equals(preferredDevice, StringComparison.OrdinalIgnoreCase)) is { } saved)
            return saved;
        return screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault();
    }

    /// <summary>Lo schermo che contiene la parte più grande del rettangolo; se non ne tocca nessuno, il più vicino.</summary>
    public static ScreenInfo? ScreenOf(IReadOnlyList<ScreenInfo> screens, Rectangle window)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
            return null;
        var best = screens.MaxBy(s => Area(Rectangle.Intersect(s.Bounds, window)))!;
        if (Area(Rectangle.Intersect(best.Bounds, window)) > 0)
            return best;
        var center = new Point(window.X + window.Width / 2, window.Y + window.Height / 2);
        return screens.MinBy(s => DistanceSquared(s.Bounds, center));
    }

    /// <summary>Lo schermo successivo (in ordine da sinistra a destra, dall'alto in basso), con ritorno al primo.</summary>
    public static ScreenInfo? NextScreen(IReadOnlyList<ScreenInfo> screens, ScreenInfo? current, bool forward = true)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var ordered = Ordered(screens);
        if (ordered.Count == 0)
            return null;
        int i = current is null ? -1 : ordered.FindIndex(s => s.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return forward ? ordered[0] : ordered[^1];
        return ordered[((i + (forward ? 1 : -1)) % ordered.Count + ordered.Count) % ordered.Count];
    }

    public static List<ScreenInfo> Ordered(IEnumerable<ScreenInfo> screens) =>
        screens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    /// <summary>Area che copre tutti i monitor (per una sessione che li usa tutti).</summary>
    public static Rectangle VirtualBounds(IEnumerable<ScreenInfo> screens) =>
        screens.Select(s => s.Bounds).DefaultIfEmpty(Rectangle.Empty).Aggregate(Rectangle.Union);

    /// <summary>
    /// Sposta una finestra da uno schermo all'altro mantenendo la posizione relativa e la dimensione percepita
    /// (scalata col rapporto dei DPI), poi la fa stare nell'area di lavoro di destinazione.
    /// </summary>
    public static Rectangle MoveToScreen(Rectangle window, ScreenInfo from, ScreenInfo to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        double ratio = to.Scale / from.Scale;
        int width = (int)Math.Round(window.Width * ratio);
        int height = (int)Math.Round(window.Height * ratio);
        var src = from.WorkingArea;
        var dst = to.WorkingArea;
        double rx = src.Width > width ? (double)(window.X - src.X) / Math.Max(1, src.Width - window.Width) : 0;
        double ry = src.Height > height ? (double)(window.Y - src.Y) / Math.Max(1, src.Height - window.Height) : 0;
        rx = Math.Clamp(rx, 0, 1);
        ry = Math.Clamp(ry, 0, 1);
        var moved = new Rectangle(
            dst.X + (int)Math.Round(rx * Math.Max(0, dst.Width - width)),
            dst.Y + (int)Math.Round(ry * Math.Max(0, dst.Height - height)),
            width, height);
        return FitInto(moved, dst);
    }

    /// <summary>
    /// Ricolloca una finestra salvata se il suo schermo non c'è più o se è rimasta fuori dalle aree visibili:
    /// se ne è visibile abbastanza la lascia dov'è, altrimenti la porta sullo schermo preferito (o principale).
    /// </summary>
    public static Rectangle EnsureVisible(Rectangle window, IReadOnlyList<ScreenInfo> screens, string? preferredDevice = null,
        int minVisible = 64)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
            return window;
        // La barra del titolo deve essere afferrabile: si controlla la striscia in alto della finestra.
        var titleBar = new Rectangle(window.X, window.Y, window.Width, Math.Min(window.Height, 32));
        bool visible = screens.Any(s =>
        {
            var part = Rectangle.Intersect(s.WorkingArea, titleBar);
            return part.Width >= Math.Min(minVisible, window.Width) && part.Height > 0;
        });
        if (visible)
            return window;
        var target = ChooseScreen(screens, preferredDevice)!;
        return Center(window.Size, target.WorkingArea);
    }

    /// <summary>Riduce la finestra se è più grande dell'area e la sposta dentro.</summary>
    public static Rectangle FitInto(Rectangle window, Rectangle area)
    {
        int width = Math.Min(window.Width, area.Width);
        int height = Math.Min(window.Height, area.Height);
        int x = Math.Clamp(window.X, area.X, area.Right - width);
        int y = Math.Clamp(window.Y, area.Y, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    public static Rectangle Center(Size size, Rectangle area)
    {
        int width = Math.Min(size.Width, area.Width);
        int height = Math.Min(size.Height, area.Height);
        return new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    /// <summary>Dimensione in pixel logici (a 96 DPI) di un'area in pixel fisici, per scegliere la risoluzione remota.</summary>
    public static Size ToLogical(Size physical, int dpi) =>
        dpi <= 0 ? physical : new Size((int)Math.Round(physical.Width * 96.0 / dpi), (int)Math.Round(physical.Height * 96.0 / dpi));

    /// <summary>Fattore di scala (100, 125, 150...) da passare alla sessione remota, arrotondato ai valori accettati da RDP.</summary>
    public static int DesktopScaleFactor(int dpi)
    {
        int[] allowed = [100, 125, 150, 175, 200, 250, 300, 400, 500];
        int percent = (int)Math.Round(Math.Max(96, dpi) * 100.0 / 96);
        return allowed.MinBy(a => Math.Abs(a - percent));
    }

    private static long Area(Rectangle r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;

    private static long DistanceSquared(Rectangle r, Point p)
    {
        long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
        long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }
}
