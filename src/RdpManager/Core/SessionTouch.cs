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

/// <summary>Cosa fare in seguito a un gesto del dito (mouse, tasti al PC remoto o comandi della finestra di RdpManager).</summary>
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
    /// <summary>Pizzico: Ctrl+rotella verso il PC remoto; <see cref="TouchAction.Amount"/> positivo = dita che si allargano (ingrandisce), negativo = dita che si avvicinano.</summary>
    Zoom,
    /// <summary>Alt+Tab sul PC remoto.</summary>
    AltTab,
    /// <summary>Win+D sul PC remoto: mostra il desktop.</summary>
    ShowDesktop,
    /// <summary>Win+Tab sul PC remoto: vista attività.</summary>
    TaskView,
    /// <summary>Comando per RdpManager, non per il PC remoto: scheda di sessione precedente.</summary>
    PreviousTab,
    /// <summary>Comando per RdpManager: scheda di sessione successiva.</summary>
    NextTab,
    /// <summary>Comando per RdpManager: mostra la barra comandi della sessione.</summary>
    ShowBar,
    /// <summary>Comando per RdpManager: nasconde la barra comandi della sessione.</summary>
    HideBar,
}

public static class TouchActionKinds
{
    /// <summary>L'azione riguarda la finestra di RdpManager (schede, barra) e non va mandata al PC remoto.</summary>
    public static bool IsWindowCommand(TouchActionKind kind) =>
        kind is TouchActionKind.PreviousTab or TouchActionKind.NextTab or TouchActionKind.ShowBar or TouchActionKind.HideBar;

    /// <summary>L'azione è una combinazione di tasti per il PC remoto.</summary>
    public static bool IsRemoteKeys(TouchActionKind kind) =>
        kind is TouchActionKind.AltTab or TouchActionKind.ShowDesktop or TouchActionKind.TaskView;
}

public readonly record struct TouchAction(TouchActionKind Kind, Point Location, int Amount = 0);

/// <summary>I gesti a più dita che scattano una volta sola (scorrimento e pizzico sono continui e non stanno qui).</summary>
public enum TouchGesture
{
    TwoFingerTap,
    ThreeFingerTap,
    ThreeFingerSwipeLeft,
    ThreeFingerSwipeRight,
    ThreeFingerSwipeUp,
    ThreeFingerSwipeDown,
    FourFingerSwipeUp,
    FourFingerSwipeDown,
}

/// <summary>
/// Tabella gesto → azione, in un solo posto. Qui ci sono i valori predefiniti; il costruttore accetta una tabella
/// diversa (pronta per le Impostazioni). Un gesto senza voce non fa nulla.
/// </summary>
public sealed class TouchGestureMap
{
    /// <summary>
    /// Valori predefiniti. Il tocco a tre dita fa Alt+Tab sul PC remoto (e non apre la barra: la barra ha già i suoi due
    /// scorrimenti su/giù, e Alt+Tab da tablet è ciò che manca di più).
    /// </summary>
    public static IReadOnlyDictionary<TouchGesture, TouchActionKind> Defaults { get; } = new Dictionary<TouchGesture, TouchActionKind>
    {
        [TouchGesture.TwoFingerTap] = TouchActionKind.RightClick,
        [TouchGesture.ThreeFingerTap] = TouchActionKind.AltTab,
        [TouchGesture.ThreeFingerSwipeLeft] = TouchActionKind.PreviousTab,
        [TouchGesture.ThreeFingerSwipeRight] = TouchActionKind.NextTab,
        [TouchGesture.ThreeFingerSwipeUp] = TouchActionKind.ShowBar,
        [TouchGesture.ThreeFingerSwipeDown] = TouchActionKind.HideBar,
        [TouchGesture.FourFingerSwipeUp] = TouchActionKind.TaskView,
        [TouchGesture.FourFingerSwipeDown] = TouchActionKind.ShowDesktop,
    };

    private readonly IReadOnlyDictionary<TouchGesture, TouchActionKind> _table;

    public TouchGestureMap(IReadOnlyDictionary<TouchGesture, TouchActionKind>? table = null) => _table = table ?? Defaults;

    public static TouchGestureMap Default { get; } = new();

    /// <summary>L'azione del gesto, o null se non ne ha.</summary>
    public TouchActionKind? ActionFor(TouchGesture gesture) => _table.TryGetValue(gesture, out var kind) ? kind : null;
}

/// <summary>
/// Trasforma i tocchi col dito in azioni: logica pura, senza schermo (l'orologio lo passa chi chiama).
/// <list type="bullet">
/// <item>Un dito: tocco = clic, secondo tocco vicino e rapido = doppio clic, fermo a lungo = clic destro, trascinamento = trascinare col tasto sinistro.</item>
/// <item>Due dita: se si muovono insieme, rotella (il dito che sale fa scendere il contenuto); se la distanza cambia, pizzico = zoom
/// (Ctrl+rotella); un tocco breve e fermo = clic destro nel punto medio.</item>
/// <item>Tre dita: scorrimento a sinistra/destra/su/giù, oppure tocco breve. Quattro dita: scorrimento su/giù. Cosa fa ogni gesto lo dice <see cref="TouchGestureMap"/>.</item>
/// </list>
/// Il numero di dita del gesto è quello appoggiato entro <c>joinWindow</c> dal primo dito: un dito che arriva dopo, quando
/// ce ne sono già due o più, è ignorato (non trasforma uno scorrimento a due dita in uno a tre). Il gesto si sceglie una volta
/// sola, quando il movimento supera la soglia (il doppio della tolleranza): dopo, scorrimento e pizzico non si scambiano.
/// </summary>
public sealed class TouchGestureRecognizer
{
    public const int WheelNotch = 120;

    private enum Phase { Undecided, Scroll, Pinch, Done }

    private readonly Dictionary<int, Point> _fingers = [];
    private readonly Dictionary<int, Point> _start = [];
    private readonly HashSet<int> _ignored = [];
    private readonly LongPressDetector _longPress;
    private readonly int _tolerance;
    private readonly int _slop;
    private readonly int _scrollStep;
    private readonly int _pinchStep;
    private readonly int _swipeDistance;
    private readonly TimeSpan _doubleTap;
    private readonly TimeSpan _joinWindow;
    private readonly TimeSpan _tapMax;
    private readonly TouchGestureMap _map;
    private int _primary = -1;
    private int _groupSize;
    private bool _dragging, _multi, _longFired, _moved;
    private Phase _phase;
    private Point _origin;
    private DateTime _firstDown;
    private double _scrollAccumulator, _pinchAccumulator, _lastDistance;
    private Point _lastCentroid;
    private DateTime _lastTapTime = DateTime.MinValue;
    private Point _lastTapPoint;

    public TouchGestureRecognizer(TimeSpan? hold = null, int tolerance = TouchMetrics.LongPressTolerance, int scrollStep = 28, TimeSpan? doubleTap = null,
        int pinchStep = 28, int swipeDistance = 90, TimeSpan? joinWindow = null, TimeSpan? tapMax = null, TouchGestureMap? map = null)
    {
        _longPress = new LongPressDetector(hold, tolerance);
        _tolerance = tolerance;
        _slop = tolerance * 2;
        _scrollStep = Math.Max(1, scrollStep);
        _pinchStep = Math.Max(1, pinchStep);
        _swipeDistance = Math.Max(_slop, swipeDistance);
        _doubleTap = doubleTap ?? TimeSpan.FromMilliseconds(400);
        _joinWindow = joinWindow ?? TimeSpan.FromMilliseconds(250);
        _tapMax = tapMax ?? TimeSpan.FromMilliseconds(350);
        _map = map ?? TouchGestureMap.Default;
    }

    /// <summary>Il prossimo tocco (o la prossima pressione lunga) vale come clic destro, poi torna normale (pulsante «Clic destro» della barra).</summary>
    public bool NextTapIsRightClick { get; set; }

    /// <summary>Almeno un dito è appoggiato.</summary>
    public bool IsActive => _fingers.Count > 0;

    public IReadOnlyList<TouchAction> Down(int id, Point p, DateTime now)
    {
        var result = new List<TouchAction>();
        if (_fingers.ContainsKey(id) || _ignored.Contains(id))
            return result;
        if (_fingers.Count == 0)
        {
            Reset();
            _fingers[id] = p;
            _start[id] = p;
            _primary = id;
            _origin = p;
            _firstDown = now;
            _groupSize = 1;
            _longPress.Press(p, now);
            return result;
        }
        if (_groupSize >= 2 && now - _firstDown > _joinWindow)
        {
            _ignored.Add(id); // dito arrivato in ritardo: non cambia il gesto già in corso
            return result;
        }
        // Dito in più: niente clic né trascinamento.
        if (_dragging)
            result.Add(new TouchAction(TouchActionKind.LeftUp, _fingers[_primary]));
        _dragging = false;
        _longPress.Release();
        _fingers[id] = p;
        _start[id] = p;
        _groupSize++;
        _multi = true;
        if (_phase != Phase.Undecided)
        {
            // Il terzo dito arriva a scorrimento o pizzico già cominciati: si ricomincia da capo come gesto a più dita.
            _phase = Phase.Undecided;
            foreach (var key in _fingers.Keys.ToList())
                _start[key] = _fingers[key];
        }
        _lastCentroid = Centroid();
        _lastDistance = Distance();
        return result;
    }

    public IReadOnlyList<TouchAction> Move(int id, Point p, DateTime now)
    {
        var result = new List<TouchAction>();
        if (!_fingers.ContainsKey(id))
            return result;
        _fingers[id] = p;
        if (_multi)
        {
            MoveMulti(id, p, result);
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
        if (_ignored.Remove(id) || !_fingers.ContainsKey(id))
            return result;
        _fingers[id] = p;
        if (!_multi && id == _primary)
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
        if (_fingers.Count == 0)
        {
            if (_multi)
                result.AddRange(FinishMulti(now));
            Reset();
        }
        else if (_multi)
        {
            _lastCentroid = Centroid();
            _lastDistance = Distance();
        }
        return result;
    }

    /// <summary>Il dito appoggiato non si è mosso: se è ora, scatta il clic destro (una volta sola).</summary>
    public IReadOnlyList<TouchAction> Poll(DateTime now)
    {
        if (_multi || _dragging || !_longPress.Poll(now))
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
        _ignored.Clear();
        _longPress.Release();
        Reset();
        return result;
    }

    private void Reset()
    {
        _start.Clear();
        _primary = -1;
        _groupSize = 0;
        _dragging = _multi = _longFired = _moved = false;
        _phase = Phase.Undecided;
        _scrollAccumulator = _pinchAccumulator = 0;
    }

    private void MoveMulti(int id, Point p, List<TouchAction> result)
    {
        if (_start.TryGetValue(id, out var s) && Dist(s, p) > _slop)
            _moved = true;
        var (dx, dy) = Displacement();
        double pan = Math.Sqrt(dx * dx + dy * dy);
        switch (_phase)
        {
            case Phase.Undecided when _groupSize >= 3:
                if (pan >= _swipeDistance)
                {
                    _phase = Phase.Done;
                    _moved = true;
                    if (SwipeGesture(dx, dy) is { } gesture)
                        result.AddRange(Emit(gesture, Centroid()));
                }
                break;
            case Phase.Undecided:
                double pinch = _fingers.Count == 2 ? Distance() - StartDistance() : 0;
                if (Math.Max(Math.Abs(pinch), pan) < _slop)
                    break;
                _moved = true;
                if (Math.Abs(pinch) > pan)
                {
                    _phase = Phase.Pinch;
                    _pinchAccumulator = pinch;
                    _lastDistance = Distance();
                    PinchSteps(result);
                }
                else
                {
                    _phase = Phase.Scroll;
                    _scrollAccumulator = dy;
                    _lastCentroid = Centroid();
                    ScrollSteps(result);
                }
                break;
            case Phase.Scroll:
                var c = Centroid();
                _scrollAccumulator += c.Y - _lastCentroid.Y;
                _lastCentroid = c;
                ScrollSteps(result);
                break;
            case Phase.Pinch when _fingers.Count == 2:
                double d = Distance();
                _pinchAccumulator += d - _lastDistance;
                _lastDistance = d;
                PinchSteps(result);
                break;
        }
    }

    private void ScrollSteps(List<TouchAction> result)
    {
        var c = Centroid();
        while (Math.Abs(_scrollAccumulator) >= _scrollStep)
        {
            // Il dito sale (Y negativa) = si scorre verso il basso = rotella negativa.
            int sign = _scrollAccumulator < 0 ? -1 : 1;
            result.Add(new TouchAction(TouchActionKind.Scroll, c, sign * WheelNotch));
            _scrollAccumulator -= sign * _scrollStep;
        }
    }

    private void PinchSteps(List<TouchAction> result)
    {
        var c = Centroid();
        while (Math.Abs(_pinchAccumulator) >= _pinchStep)
        {
            int sign = _pinchAccumulator < 0 ? -1 : 1;
            result.Add(new TouchAction(TouchActionKind.Zoom, c, sign * WheelNotch));
            _pinchAccumulator -= sign * _pinchStep;
        }
    }

    private TouchGesture? SwipeGesture(double dx, double dy)
    {
        bool horizontal = Math.Abs(dx) > Math.Abs(dy);
        return _groupSize switch
        {
            3 when horizontal => dx < 0 ? TouchGesture.ThreeFingerSwipeLeft : TouchGesture.ThreeFingerSwipeRight,
            3 => dy < 0 ? TouchGesture.ThreeFingerSwipeUp : TouchGesture.ThreeFingerSwipeDown,
            4 when !horizontal => dy < 0 ? TouchGesture.FourFingerSwipeUp : TouchGesture.FourFingerSwipeDown,
            _ => null,
        };
    }

    /// <summary>Tutte le dita sono alzate: se era un tocco breve e fermo, scatta il gesto del tocco.</summary>
    private IEnumerable<TouchAction> FinishMulti(DateTime now)
    {
        if (_moved || _phase != Phase.Undecided || now - _firstDown > _tapMax)
            return [];
        TouchGesture? gesture = _groupSize switch
        {
            2 => TouchGesture.TwoFingerTap,
            3 => TouchGesture.ThreeFingerTap,
            _ => null,
        };
        if (gesture is null || _start.Count == 0)
            return [];
        var mid = new Point(_start.Values.Sum(f => f.X) / _start.Count, _start.Values.Sum(f => f.Y) / _start.Count);
        return Emit(gesture.Value, mid);
    }

    private IEnumerable<TouchAction> Emit(TouchGesture gesture, Point at)
    {
        if (_map.ActionFor(gesture) is not { } kind)
            yield break;
        // Il clic parte dal punto del gesto; i tasti e i comandi della finestra non hanno un punto.
        if (kind is TouchActionKind.RightClick or TouchActionKind.Click)
            yield return new TouchAction(TouchActionKind.Move, at);
        yield return new TouchAction(kind, at, kind == TouchActionKind.Click ? 1 : 0);
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
        int n = Math.Max(1, _fingers.Count);
        return new Point(_fingers.Values.Sum(f => f.X) / n, _fingers.Values.Sum(f => f.Y) / n);
    }

    /// <summary>Spostamento medio delle dita ancora appoggiate rispetto a dove si erano posate (non dipende dall'ordine di arrivo).</summary>
    private (double Dx, double Dy) Displacement()
    {
        double sx = 0, sy = 0;
        int n = 0;
        foreach (var (id, cur) in _fingers)
        {
            if (!_start.TryGetValue(id, out var s))
                continue;
            sx += cur.X - s.X;
            sy += cur.Y - s.Y;
            n++;
        }
        return n == 0 ? (0, 0) : (sx / n, sy / n);
    }

    /// <summary>Distanza fra le due dita appoggiate (0 se non sono due).</summary>
    private double Distance() => _fingers.Count == 2 ? Dist(_fingers.Values.First(), _fingers.Values.Last()) : 0;

    private double StartDistance() => _fingers.Count == 2 ? Dist(_start[_fingers.Keys.First()], _start[_fingers.Keys.Last()]) : 0;

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
