using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Regola comune di ogni dialogo (la stessa di <c>TouchForm</c> su Windows): la finestra sta nell'area di lavoro dello
/// schermo su cui si apre, anche piccolo (640x480); se il contenuto è più grande, scorre. Prima ogni finestra aveva
/// misure fisse (560x680, 880x560...) e su uno schermo basso il bordo e i pulsanti restavano fuori dallo schermo.
/// Si applica in un posto solo, <see cref="DialogTracker"/>, così vale anche per i dialoghi futuri.
/// </summary>
public static class DialogFit
{
    /// <summary>Per le prove: area di lavoro (pixel logici, scala 1) da usare al posto di quella dello schermo.</summary>
    internal static Size? WorkAreaForTests { get; set; }

    /// <summary>Margine (pixel logici) lasciato fra la finestra e il bordo dell'area di lavoro.</summary>
    public const double Margin = 12;

    /// <summary>Misura più grande che un dialogo può avere in un'area di lavoro (pixel logici).</summary>
    public static Size MaxSize(Size workArea) =>
        new(Math.Max(1, workArea.Width - 2 * Margin), Math.Max(1, workArea.Height - 2 * Margin));

    /// <summary>
    /// Porta la finestra dentro la misura massima: Width/Height/Min/Max ridotti, e per le finestre che si adattano al
    /// contenuto (SizeToContent) il contenuto va in un riquadro che scorre. Si può chiamare prima di aprirla; è idempotente.
    /// </summary>
    public static void Constrain(Window window, Size workArea)
    {
        var max = MaxSize(workArea);
        window.MaxWidth = Math.Min(window.MaxWidth, max.Width);
        window.MaxHeight = Math.Min(window.MaxHeight, max.Height);
        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, max.Width);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, max.Height);
        window.MinWidth = Math.Min(window.MinWidth, max.Width);
        window.MinHeight = Math.Min(window.MinHeight, max.Height);

        if (window.SizeToContent != SizeToContent.Manual && window.Content is Control content and not ScrollViewer)
        {
            window.Content = null;
            window.Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }
    }

    /// <summary>Posizione (pixel veri) che tiene la finestra dentro l'area di lavoro.</summary>
    public static PixelPoint ClampPosition(PixelPoint position, PixelSize size, PixelRect area) =>
        new(Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));

    /// <summary>L'area di lavoro dello schermo della finestra, in pixel logici e in pixel veri (se lo schermo si conosce).</summary>
    private static bool TryWorkArea(Window window, out Size logical, out PixelRect pixels)
    {
        if (WorkAreaForTests is { } test)
        {
            logical = test;
            pixels = new PixelRect(0, 0, (int)test.Width, (int)test.Height);
            return true;
        }
        var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
        if (screen is null)
        {
            logical = default;
            pixels = default;
            return false;
        }
        pixels = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        logical = new Size(pixels.Width / scale, pixels.Height / scale);
        return true;
    }

    /// <summary>Prepara il dialogo per aprirsi sopra <paramref name="owner"/>: misura sull'area di lavoro e posizione dentro lo schermo.</summary>
    public static void Prepare(Window window, Window owner)
    {
        // Le finestre della VPN sono in lavorazione a parte: ci si arriva con questa stessa regola dopo.
        if (window.GetType().Name.StartsWith("Vpn", StringComparison.Ordinal))
            return;
        if (TryWorkArea(owner, out var logical, out _))
            Constrain(window, logical);
        window.Opened += (_, _) =>
        {
            if (!TryWorkArea(window, out _, out var area))
                return;
            double scale = window.RenderScaling;
            var frame = window.FrameSize ?? window.Bounds.Size;
            var size = new PixelSize((int)Math.Ceiling(frame.Width * scale), (int)Math.Ceiling(frame.Height * scale));
            var fitted = ClampPosition(window.Position, size, area);
            if (fitted != window.Position)
                window.Position = fitted;
        };
    }
}
