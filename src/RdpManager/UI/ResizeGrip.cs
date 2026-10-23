using RdpManager.Core;

namespace RdpManager.UI;

internal enum GripPhase
{
    Start,
    Move,
    End,
}

/// <param name="Start">Dove è stato premuto il tasto del mouse (coordinate dello schermo).</param>
/// <param name="Current">Dove si trova adesso (coordinate dello schermo).</param>
internal sealed class GripDragEventArgs(GripPhase phase, Point start, Point current) : EventArgs
{
    public GripPhase Phase { get; } = phase;
    public Point Start { get; } = start;
    public Point Current { get; } = current;
}

/// <summary>
/// Bordo di una barra da trascinare con il mouse per cambiarne la larghezza (o l'altezza, se la barra è in basso),
/// dalla 1.5.1. Non decide la misura: la calcola chi lo usa, con i limiti del Core.
/// </summary>
internal sealed class ResizeGrip : Control
{
    private Point? _start;
    private bool _hover;

    public ResizeGrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Edge = DockStyle.Right;
    }

    public event EventHandler<GripDragEventArgs>? Drag;

    /// <summary>Lato della barra su cui sta il bordo: destra o sinistra si trascina in orizzontale, in alto in verticale.</summary>
    public DockStyle Edge
    {
        get => Dock;
        set
        {
            Dock = value;
            bool vertical = value is DockStyle.Top or DockStyle.Bottom;
            Cursor = vertical ? Cursors.SizeNS : Cursors.SizeWE;
            if (vertical)
                Height = FullScreenBarLayout.GripThickness;
            else
                Width = FullScreenBarLayout.GripThickness;
        }
    }

    /// <summary>Colore della linea che si accende con il mouse sopra o durante il trascinamento.</summary>
    public Color HighlightColor { get; set; } = Color.FromArgb(70, 130, 210);

    /// <summary>Vero mentre si trascina: la barra a comparsa non deve richiudersi.</summary>
    public bool IsDragging => _start is not null;

    /// <summary>Per le prove automatiche: un trascinamento completo da un punto all'altro dello schermo.</summary>
    internal void SimulateDrag(Point start, Point end)
    {
        Drag?.Invoke(this, new GripDragEventArgs(GripPhase.Start, start, start));
        Drag?.Invoke(this, new GripDragEventArgs(GripPhase.Move, start, end));
        Drag?.Invoke(this, new GripDragEventArgs(GripPhase.End, start, end));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        var p = MousePosition;
        _start = p;
        Capture = true;
        Invalidate();
        Drag?.Invoke(this, new GripDragEventArgs(GripPhase.Start, p, p));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is { } start)
            Drag?.Invoke(this, new GripDragEventArgs(GripPhase.Move, start, MousePosition));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Finish();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        Finish();
    }

    private void Finish()
    {
        if (_start is not { } start)
            return;
        _start = null;
        Capture = false;
        Invalidate();
        Drag?.Invoke(this, new GripDragEventArgs(GripPhase.End, start, MousePosition));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (!_hover && !IsDragging)
            return;
        using var brush = new SolidBrush(HighlightColor);
        var r = ClientRectangle;
        // Linea di 2 px nel mezzo del bordo.
        var line = Dock is DockStyle.Top or DockStyle.Bottom
            ? new Rectangle(r.Left, r.Top + (r.Height - 2) / 2, r.Width, 2)
            : new Rectangle(r.Left + (r.Width - 2) / 2, r.Top, 2, r.Height);
        e.Graphics.FillRectangle(brush, line);
    }
}
