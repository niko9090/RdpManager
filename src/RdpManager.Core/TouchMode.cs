using System.Drawing;

namespace RdpManager.Core;

/// <summary>Modalità tocco della finestra principale: automatica (se c'è uno schermo touch), sempre accesa o sempre spenta.</summary>
public enum TouchMode { Auto, On, Off }

/// <summary>
/// Decisioni sulla modalità tocco, senza finestre né API di sistema (così si provano): quando è attiva, se un
/// segnale di input viene dal dito, se toccare la casella di ricerca deve aprire la tastiera su schermo.
/// </summary>
public static class TouchPolicy
{
    /// <summary>Lato dei bersagli in modalità tocco (pixel logici): più comodo del minimo di <see cref="TouchMetrics.MinTarget"/>.</summary>
    public const int ComfortTarget = 48;

    /// <summary>Altezza delle righe dell'elenco connessioni in modalità tocco (pixel logici).</summary>
    public const int RowHeight = 52;

    /// <summary>Modalità tocco attiva: sempre con «On», mai con «Off», con «Auto» solo se il PC ha uno schermo touch.</summary>
    public static bool IsActive(TouchMode mode, bool hasTouchScreen) => mode switch
    {
        TouchMode.On => true,
        TouchMode.Off => false,
        _ => hasTouchScreen,
    };

    /// <summary>Lato minimo dei bersagli per la modalità: 48 px logici col dito, 40 (il minimo di sempre) senza.</summary>
    public static int TargetSize(bool touchActive) => touchActive ? ComfortTarget : TouchMetrics.MinTarget;

    /// <summary>Valore ripulito da un file di impostazioni (un numero sconosciuto torna «Auto»).</summary>
    public static TouchMode Normalize(TouchMode mode) => Enum.IsDefined(mode) ? mode : TouchMode.Auto;

    // GetSystemMetrics(SM_DIGITIZER): bit 0x01 touch integrato, 0x02 touch esterno, 0x80 pronto all'uso.
    private const int NidIntegratedTouch = 0x01, NidExternalTouch = 0x02, NidReady = 0x80;

    /// <summary>Dai valori di <c>SM_DIGITIZER</c> e <c>SM_MAXIMUMTOUCHES</c>: c'è uno schermo (o digitalizzatore) a tocco pronto.</summary>
    public static bool IsTouchCapable(int digitizer, int maximumTouches) =>
        (digitizer & NidReady) != 0 && (digitizer & (NidIntegratedTouch | NidExternalTouch)) != 0 && maximumTouches > 0;

    /// <summary>
    /// Windows marca col valore 0xFF515700 (nei byte alti) gli eventi del mouse che nascono da un dito o dalla penna
    /// (<c>GetMessageExtraInfo</c>): così si distingue il dito dal mouse vero.
    /// </summary>
    public static bool IsTouchOrigin(long messageExtraInfo) => (messageExtraInfo & 0xFFFFFF00L) == 0xFF515700L;

    /// <summary>Toccando la casella di ricerca si apre la tastiera su schermo solo in modalità tocco e solo se il tocco è di un dito.</summary>
    public static bool ShouldOpenKeyboardOnTap(bool touchActive, bool fromTouch) => touchActive && fromTouch;

    /// <summary>Il trascinamento di una connessione (sposta nelle cartelle) vale solo col mouse: col dito trascinare fa scorrere l'elenco.</summary>
    public static bool AllowItemDrag(bool touchActive, bool fromTouch) => !(touchActive && fromTouch);
}

/// <summary>Geometria della X di chiusura nelle schede larghe da dito.</summary>
public static class TabCloseLayout
{
    /// <summary>Larghezza di una scheda in modalità tocco (pixel logici).</summary>
    public const int TabWidth = 210;

    /// <summary>Lato del bersaglio della X (pixel logici) e distanza dal bordo destro della scheda.</summary>
    public const int CloseSize = 36;
    public const int CloseMargin = 10;

    /// <summary>Il riquadro della X dentro la scheda <paramref name="tab"/>: quadrato, centrato in verticale, lontano dal bordo.</summary>
    public static Rectangle CloseRect(Rectangle tab, int dpi)
    {
        int size = Math.Min(TouchMetrics.Scale(CloseSize, dpi), Math.Max(1, tab.Height));
        int margin = TouchMetrics.Scale(CloseMargin, dpi);
        return new Rectangle(tab.Right - margin - size, tab.Y + (tab.Height - size) / 2, size, size);
    }

    /// <summary>Lo spazio del testo: la scheda senza la X e il suo margine.</summary>
    public static Rectangle TextArea(Rectangle tab, int dpi, int left)
    {
        var close = CloseRect(tab, dpi);
        int x = tab.X + left;
        return new Rectangle(x, tab.Y, Math.Max(1, close.X - x), tab.Height);
    }

    /// <summary>Indice della scheda sotto <paramref name="point"/> se il tocco è sulla sua X, altrimenti -1.</summary>
    public static int HitClose(IReadOnlyList<Rectangle> tabs, Point point, int dpi)
    {
        for (int i = 0; i < tabs.Count; i++)
            if (tabs[i].Contains(point))
                return CloseRect(tabs[i], dpi).Contains(point) ? i : -1;
        return -1;
    }
}

/// <summary>
/// Scorrimento trascinando col dito: dopo una piccola soglia il movimento verticale diventa righe da scorrere
/// (col resto riportato al movimento successivo). Chi chiama passa le posizioni e applica i passi restituiti.
/// </summary>
public sealed class PanScroller
{
    private readonly int _rowHeight;
    private readonly int _threshold;
    private int _startY, _lastY, _carry;
    private bool _down;

    public PanScroller(int rowHeight, int threshold)
    {
        _rowHeight = Math.Max(1, rowHeight);
        _threshold = Math.Max(1, threshold);
    }

    /// <summary>Il dito si è mosso oltre la soglia: è un trascinamento, non un tocco.</summary>
    public bool Active { get; private set; }

    public void Begin(int y)
    {
        _startY = _lastY = y;
        _carry = 0;
        _down = true;
        Active = false;
    }

    /// <summary>Righe da scorrere (positive = verso il fondo dell'elenco: il dito sale) per la nuova posizione.</summary>
    public int Move(int y)
    {
        if (!_down)
            return 0;
        if (!Active)
        {
            if (Math.Abs(y - _startY) < _threshold)
                return 0;
            Active = true;
            _lastY = _startY;
        }
        _carry += _lastY - y;
        _lastY = y;
        int steps = _carry / _rowHeight; // tronca verso zero
        _carry -= steps * _rowHeight;
        return steps;
    }

    /// <summary>Il dito si solleva. <see cref="Active"/> resta com'era fino al prossimo <see cref="Begin"/> (per ignorare il clic che segue).</summary>
    public void End() => _down = false;
}

/// <summary>Passaggio fra schede con uno scorrimento laterale del dito sulla riga delle schede.</summary>
public static class SwipeDetector
{
    /// <summary>Distanza minima del gesto (pixel logici).</summary>
    public const int MinDistance = 70;

    /// <summary>+1 = scheda successiva (dito verso sinistra), -1 = precedente, 0 = non è uno scorrimento laterale.</summary>
    public static int Direction(Point down, Point up, int minDistance)
    {
        int dx = up.X - down.X, dy = up.Y - down.Y;
        if (Math.Abs(dx) < minDistance || Math.Abs(dx) < 2 * Math.Abs(dy))
            return 0;
        return dx < 0 ? 1 : -1;
    }
}
