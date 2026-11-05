using System.Drawing;

namespace RdpManager.Core;

/// <summary>Come il dito agisce dentro la sessione remota (dalla 1.9.1).</summary>
public enum SessionTouchMode
{
    /// <summary>Il tocco (anche con più dita) va al PC remoto, se il controllo lo permette; altrimenti Windows lo trasforma in mouse.</summary>
    Remote,
    /// <summary>Il dito fa da mouse: tocco = clic, doppio tocco = doppio clic, pressione lunga = clic destro, due dita = rotella.</summary>
    Mouse,
}

public static class SessionTouchModes
{
    public static string Describe(SessionTouchMode mode) => mode switch
    {
        SessionTouchMode.Mouse => "Il dito fa da mouse (pressione lunga = clic destro, due dita = scorrimento)",
        _ => "Tocco passato al PC remoto (se Windows remoto lo supporta)",
    };

    /// <summary>Etichetta breve per il pulsante della barra.</summary>
    public static string Short(SessionTouchMode mode) => mode == SessionTouchMode.Mouse ? "Tocco: mouse" : "Tocco: remoto";

    public static SessionTouchMode Toggle(SessionTouchMode mode) =>
        mode == SessionTouchMode.Mouse ? SessionTouchMode.Remote : SessionTouchMode.Mouse;
}

/// <summary>Cosa fare col mouse in seguito a un gesto del dito.</summary>
public enum TouchActionKind
{
    /// <summary>Sposta il cursore nel punto.</summary>
    Move,
    /// <summary>Clic sinistro; <see cref="TouchAction.Amount"/> = 1 o 2 (secondo tocco ravvicinato = doppio clic).</summary>
    Click,
    RightClick,
    LeftDown,
    LeftUp,
    /// <summary>Rotella; <see cref="TouchAction.Amount"/> positivo = in su, negativo = in giù (multipli di 120).</summary>
    Scroll,
}

public readonly record struct TouchAction(TouchActionKind Kind, Point Location, int Amount = 0);

/// <summary>
/// Trasforma i tocchi col dito in azioni da mouse: logica pura, senza schermo (l'orologio lo passa chi chiama).
/// Un dito: tocco = clic, secondo tocco vicino e rapido = doppio clic, fermo a lungo = clic destro, trascinamento =
/// trascinare col tasto sinistro. Due dita: scorrimento con la rotella (il dito che sale fa scendere il contenuto).
/// </summary>
public sealed class TouchGestureRecognizer
{
    public const int WheelNotch = 120;

    private readonly Dictionary<int, Point> _fingers = [];
    private readonly LongPressDetector _longPress;
    private readonly int _tolerance;
    private readonly int _scrollStep;
    private readonly TimeSpan _doubleTap;
    private int _primary = -1;
    private bool _dragging, _scrolling, _longFired;
    private Point _origin;
    private double _scrollAccumulator;
    private Point _lastCentroid;
    private DateTime _lastTapTime = DateTime.MinValue;
    private Point _lastTapPoint;

    public TouchGestureRecognizer(TimeSpan? hold = null, int tolerance = TouchMetrics.LongPressTolerance, int scrollStep = 28, TimeSpan? doubleTap = null)
    {
        _longPress = new LongPressDetector(hold, tolerance);
        _tolerance = tolerance;
        _scrollStep = Math.Max(1, scrollStep);
        _doubleTap = doubleTap ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>Il prossimo tocco (o la prossima pressione lunga) vale come clic destro, poi torna normale (pulsante «Clic destro» della barra).</summary>
    public bool NextTapIsRightClick { get; set; }

    /// <summary>Almeno un dito è appoggiato.</summary>
    public bool IsActive => _fingers.Count > 0;

    public IReadOnlyList<TouchAction> Down(int id, Point p, DateTime now)
    {
        var result = new List<TouchAction>();
        _fingers[id] = p;
        if (_fingers.Count == 1)
        {
            _primary = id;
            _origin = p;
            _dragging = _scrolling = _longFired = false;
            _longPress.Press(p, now);
        }
        else
        {
            // Secondo dito: niente clic né trascinamento, solo scorrimento.
            if (_dragging)
                result.Add(new TouchAction(TouchActionKind.LeftUp, _fingers[_primary]));
            _dragging = false;
            _longPress.Release();
            _scrolling = true;
            _scrollAccumulator = 0;
            _lastCentroid = Centroid();
        }
        return result;
    }

    public IReadOnlyList<TouchAction> Move(int id, Point p, DateTime now)
    {
        var result = new List<TouchAction>();
        if (!_fingers.ContainsKey(id))
            return result;
        _fingers[id] = p;
        if (_scrolling)
        {
            var c = Centroid();
            _scrollAccumulator += c.Y - _lastCentroid.Y;
            _lastCentroid = c;
            while (Math.Abs(_scrollAccumulator) >= _scrollStep)
            {
                // Il dito sale (Y negativa) = si scorre verso il basso = rotella negativa.
                int sign = _scrollAccumulator < 0 ? -1 : 1;
                result.Add(new TouchAction(TouchActionKind.Scroll, c, sign * WheelNotch));
                _scrollAccumulator -= sign * _scrollStep;
            }
            return result;
        }
        if (id != _primary || _longFired)
            return result;
        _longPress.Move(p);
        if (!_dragging)
        {
            int dx = p.X - _origin.X, dy = p.Y - _origin.Y;
            if (dx * dx + dy * dy <= _tolerance * _tolerance)
                return result;
            _dragging = true;
            result.Add(new TouchAction(TouchActionKind.Move, _origin));
            result.Add(new TouchAction(TouchActionKind.LeftDown, _origin));
        }
        result.Add(new TouchAction(TouchActionKind.Move, p));
        return result;
    }

    public IReadOnlyList<TouchAction> Up(int id, Point p, DateTime now)
    {
        var result = new List<TouchAction>();
        if (!_fingers.ContainsKey(id))
            return result;
        _fingers[id] = p;
        bool wasPrimary = id == _primary;
        bool lastFinger = _fingers.Count == 1;
        if (!_scrolling && wasPrimary)
        {
            _longPress.Release();
            if (_dragging)
            {
                result.Add(new TouchAction(TouchActionKind.Move, p));
                result.Add(new TouchAction(TouchActionKind.LeftUp, p));
            }
            else if (!_longFired)
            {
                result.AddRange(Tap(_origin, now));
            }
        }
        _fingers.Remove(id);
        if (lastFinger)
        {
            _primary = -1;
            _dragging = _scrolling = _longFired = false;
        }
        else if (_scrolling)
        {
            _lastCentroid = Centroid();
        }
        return result;
    }

    /// <summary>Il dito appoggiato non si è mosso: se è ora, scatta il clic destro (una volta sola).</summary>
    public IReadOnlyList<TouchAction> Poll(DateTime now)
    {
        if (_scrolling || _dragging || !_longPress.Poll(now))
            return [];
        _longFired = true;
        NextTapIsRightClick = false;
        return [new TouchAction(TouchActionKind.Move, _origin), new TouchAction(TouchActionKind.RightClick, _origin)];
    }

    /// <summary>Annulla tutto (la finestra perde il fuoco, il tocco esce dal controllo): se si stava trascinando rilascia il tasto.</summary>
    public IReadOnlyList<TouchAction> Cancel()
    {
        var result = new List<TouchAction>();
        if (_dragging && _fingers.TryGetValue(_primary, out var p))
            result.Add(new TouchAction(TouchActionKind.LeftUp, p));
        _fingers.Clear();
        _longPress.Release();
        _primary = -1;
        _dragging = _scrolling = _longFired = false;
        return result;
    }

    private IEnumerable<TouchAction> Tap(Point at, DateTime now)
    {
        yield return new TouchAction(TouchActionKind.Move, at);
        if (NextTapIsRightClick)
        {
            NextTapIsRightClick = false;
            _lastTapTime = DateTime.MinValue;
            yield return new TouchAction(TouchActionKind.RightClick, at);
            yield break;
        }
        int dx = at.X - _lastTapPoint.X, dy = at.Y - _lastTapPoint.Y;
        bool second = now - _lastTapTime <= _doubleTap && dx * dx + dy * dy <= 4 * _tolerance * _tolerance;
        if (second)
        {
            // Stesso punto del primo tocco, così Windows remoto lo riconosce come doppio clic.
            _lastTapTime = DateTime.MinValue;
            yield return new TouchAction(TouchActionKind.Move, _lastTapPoint);
            yield return new TouchAction(TouchActionKind.Click, _lastTapPoint, 2);
        }
        else
        {
            _lastTapTime = now;
            _lastTapPoint = at;
            yield return new TouchAction(TouchActionKind.Click, at, 1);
        }
    }

    private Point Centroid()
    {
        int n = _fingers.Count;
        return new Point(_fingers.Values.Sum(f => f.X) / n, _fingers.Values.Sum(f => f.Y) / n);
    }
}

/// <summary>Adattamento della sessione allo schermo quando il tablet è piccolo o ruota.</summary>
public static class SessionFit
{
    /// <summary>
    /// Con risoluzione fissa, se lo spazio disponibile è più piccolo del desktop remoto conviene scalare l'immagine
    /// (zoom per adattamento) invece di avere barre di scorrimento da gestire col dito.
    /// </summary>
    public static bool ShouldSmartSize(ResolutionMode mode, Size desktop, Size available) =>
        mode == ResolutionMode.Fixed && available.Width > 0 && available.Height > 0
        && (available.Width < desktop.Width || available.Height < desktop.Height);
}
