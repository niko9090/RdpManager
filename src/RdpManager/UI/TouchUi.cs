using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Base dei dialoghi (dalla 1.6.2): scala con i dpi del monitor, porta i controlli a bersagli da dito (almeno 40 px
/// logici) e tiene la finestra dentro l'area di lavoro, anche su uno schermo piccolo (vedi <see cref="TouchUi"/>).
/// </summary>
public class TouchForm : Form
{
    public TouchForm()
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>Vero dopo che i controlli sono stati adattati al tocco e la finestra misurata sull'area di lavoro.</summary>
    internal bool TouchApplied { get; private set; }

    /// <summary>Per i dialoghi che decidono da soli la misura (dopo che i controlli sono diventati da dito, prima di stringerla allo schermo).</summary>
    protected virtual void ArrangeForTouch()
    {
    }

    protected override void OnLoad(EventArgs e)
    {
        // La scala dei dpi va fatta prima di misurare: così i pixel logici di sotto sono quelli veri.
        PerformAutoScale();
        TouchUi.Apply(this);
        ArrangeForTouch();
        TouchUi.FitDialog(this);
        TouchApplied = true;
        base.OnLoad(e);
    }
}

/// <summary>Adatta i controlli all'uso col dito: bersagli grandi, elenchi con righe alte, menu comodi, dialoghi che stanno nello schermo.</summary>
internal static class TouchUi
{
    /// <summary>Per le prove automatiche: area di lavoro (in pixel veri) da usare al posto di quella dello schermo.</summary>
    internal static Rectangle? WorkAreaForTests { get; set; }

    /// <summary>Da pixel logici a pixel veri per il monitor del controllo.</summary>
    public static int Px(Control c, int logical) => TouchMetrics.Scale(logical, c.DeviceDpi);

    /// <summary>Altezza minima di un bersaglio per il monitor del controllo.</summary>
    public static int MinTarget(Control c) => Px(c, TouchMetrics.MinTarget);

    public static Rectangle WorkArea(Control c)
    {
        if (WorkAreaForTests is { } area)
            return area;
        var owner = c is Form { Owner: { } o } ? o : c;
        return Screen.FromControl(owner).WorkingArea;
    }

    /// <summary>Per le prove automatiche: se impostata, sostituisce l'apertura vera della tastiera (restituisce se è riuscita).</summary>
    internal static Func<bool>? ScreenKeyboardForTests { get; set; }

    /// <summary>Apre la tastiera su schermo di Windows; se non c'è lo dice.</summary>
    public static void OpenScreenKeyboard(IWin32Window? owner)
    {
        bool ok = ScreenKeyboardForTests is { } test ? test() : ScreenKeyboard.TryOpen();
        if (!ok)
            MessageBox.Show(owner, "Non riesco ad aprire la tastiera su schermo di Windows (né TabTip né osk.exe).\nSi attiva da Impostazioni › Accessibilità › Tastiera.",
                "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- bersagli ----------

    /// <summary>Porta il controllo e tutti i suoi figli a bersagli da dito (idempotente).</summary>
    public static void Apply(Control root)
    {
        int min = MinTarget(root);
        Touchify(root, min);
        foreach (var c in Descendants(root))
            Touchify(c, min);
    }

    private static void Touchify(Control c, int min)
    {
        switch (c)
        {
            case Form or StatusStrip:
                break;
            case ToolStrip strip:
                TouchifyStrip(strip, min);
                break;
            case TabControl tabs:
                TouchifyTabs(tabs, min);
                break;
            case Button or CheckBox or RadioButton:
                GrowTo(c, min, widthToo: c is Button);
                break;
            case TextBox { Multiline: false } box:
                box.AutoSize = false;
                box.Height = Math.Max(box.Height, min);
                break;
            case NumericUpDown up:
                up.AutoSize = false;
                up.Height = Math.Max(up.Height, min);
                break;
            case ComboBox combo:
                TouchifyCombo(combo, min);
                break;
            case CheckedListBox checkedList:
                checkedList.CheckOnClick = true;
                checkedList.ItemHeight = Math.Max(checkedList.ItemHeight, min);
                break;
            case ListBox list when list.DrawMode == DrawMode.Normal:
                TouchifyList(list, min);
                break;
            case ListView { View: View.Details or View.List } view:
                TouchifyListView(view, min);
                break;
            case TreeView tree:
                tree.ItemHeight = Math.Max(tree.ItemHeight, min);
                break;
        }
    }

    private static void GrowTo(Control c, int min, bool widthToo)
    {
        c.MinimumSize = new Size(widthToo ? Math.Max(c.MinimumSize.Width, min) : c.MinimumSize.Width, Math.Max(c.MinimumSize.Height, min));
        if (c.Height < min)
            c.Height = min;
        if (widthToo && c.Width < min)
            c.Width = min;
    }

    /// <summary>Caselle a tendina: il campo (e la riga dell'elenco) alto quanto un bersaglio.</summary>
    private static void TouchifyCombo(ComboBox combo, int min)
    {
        if (combo.DrawMode == DrawMode.Normal)
        {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += DrawComboItem;
        }
        // Il campo è un po' più alto della riga: la riga di 4 px in meno dà un campo da dito.
        combo.ItemHeight = Math.Max(combo.ItemHeight, min - 6);
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0)
            return;
        e.DrawBackground();
        string text = combo.GetItemText(combo.Items[e.Index]);
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        var color = combo.Enabled ? (selected ? SystemColors.HighlightText : combo.ForeColor) : SystemColors.GrayText;
        TextRenderer.DrawText(e.Graphics, text, combo.Font, new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height),
            color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }

    private static void TouchifyList(ListBox list, int min)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = Math.Max(list.ItemHeight, min);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;
            e.DrawBackground();
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var color = selected ? SystemColors.HighlightText : list.ForeColor;
            TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), list.Font,
                new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    /// <summary>Le righe di un elenco a colonne si alzano con un'immagine alta e stretta (non si vede).</summary>
    private static void TouchifyListView(ListView view, int min)
    {
        view.FullRowSelect = true;
        if (view.SmallImageList is { } images)
        {
            if (images.ImageSize.Height < min)
                images.ImageSize = new Size(images.ImageSize.Width, min);
            return;
        }
        view.SmallImageList = new ImageList { ImageSize = new Size(1, min) };
    }

    /// <summary>Linguette delle schede abbastanza alte (il bordo in più sta nel riempimento verticale).</summary>
    internal static void TouchifyTabs(TabControl tabs, int min)
    {
        // L'altezza di una linguetta è quella del testo più due volte il riempimento verticale (più 2 px di bordo).
        int pad = Math.Max(tabs.Padding.Y, TouchMetrics.PaddingToReach(tabs.Font.Height + 2, min));
        if (pad != tabs.Padding.Y)
            tabs.Padding = new Point(tabs.Padding.X, pad);
    }

    // ---------- menu e barre di pulsanti ----------

    /// <summary>Voci alte almeno quanto un bersaglio, anche quelle che i menu aggiungono dopo, all'apertura.</summary>
    public static void TouchifyStrip(ToolStrip strip, int min)
    {
        foreach (ToolStripItem item in strip.Items)
            TouchifyItem(item, min);
        if (Watched.TryGetValue(strip, out _))
            return;
        Watched.Add(strip, new object());
        strip.ItemAdded += (_, e) => { if (e.Item is { } item) TouchifyItem(item, min); };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStrip, object> Watched = new();

    private static void TouchifyItem(ToolStripItem item, int min)
    {
        if (item is ToolStripSeparator)
            return;
        int height = item.GetPreferredSize(Size.Empty).Height;
        if (height < min)
        {
            int extra = TouchMetrics.PaddingToReach(height, min);
            item.Padding = new Padding(item.Padding.Left, item.Padding.Top + extra, item.Padding.Right, item.Padding.Bottom + extra);
        }
        if (item is ToolStripDropDownItem { HasDropDown: true } dropDown)
        {
            TouchifyStrip(dropDown.DropDown, min);
            dropDown.DropDown.Padding = new Padding(dropDown.DropDown.Padding.Left, dropDown.DropDown.Padding.Top + 2,
                dropDown.DropDown.Padding.Right, dropDown.DropDown.Padding.Bottom + 2);
        }
    }

    /// <summary>Un menu contestuale (o altro menu a comparsa) da dito: le sue voci ora e quelle future.</summary>
    public static void TouchifyMenu(ToolStripDropDown menu, Control dpiSource) => TouchifyStrip(menu, MinTarget(dpiSource));

    // ---------- dialoghi sull'area di lavoro ----------

    /// <summary>
    /// Il dialogo sta nell'area di lavoro: se è più grande si riduce; se si adatta da solo al contenuto e il contenuto
    /// è troppo grande, il contenuto passa in un riquadro che scorre. Poi si ricentra (sempre dentro l'area).
    /// </summary>
    public static void FitDialog(Form f)
    {
        var area = WorkArea(f);
        int dpi = f.DeviceDpi;
        var max = TouchMetrics.MaxDialogSize(area, dpi);
        if (f.AutoSize)
        {
            var preferred = f.GetPreferredSize(Size.Empty);
            if (preferred.Width > max.Width || preferred.Height > max.Height)
                MakeScrollable(f, preferred, max);
        }
        else
        {
            var fitted = TouchMetrics.FitSize(f.Size, area, dpi);
            if (fitted != f.Size)
                f.Size = fitted;
        }
        if (f.MinimumSize.Width > max.Width || f.MinimumSize.Height > max.Height)
            f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, max.Width), Math.Min(f.MinimumSize.Height, max.Height));
        Place(f, area);
    }

    private static void MakeScrollable(Form f, Size preferred, Size max)
    {
        var nonClient = new Size(f.Width - f.ClientSize.Width, f.Height - f.ClientSize.Height);
        var content = new Size(preferred.Width - nonClient.Width, preferred.Height - nonClient.Height);
        f.AutoSize = false;
        var inner = new Panel { Size = content, Padding = f.Padding };
        f.Padding = Padding.Empty;
        inner.Controls.AddRange(f.Controls.Cast<Control>().ToArray());
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        host.Controls.Add(inner);
        f.Controls.Add(host);
        int bar = SystemInformation.VerticalScrollBarWidth;
        bool tooTall = preferred.Height > max.Height;
        var size = new Size(Math.Min(preferred.Width + (tooTall ? bar : 0), max.Width), Math.Min(preferred.Height, max.Height));
        f.Size = size;
        ScrollHosts.Add(f, new object());
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> ScrollHosts = new();

    /// <summary>Vero se il contenuto del dialogo è stato messo in un riquadro che scorre perché non stava nello schermo.</summary>
    internal static bool IsScrolling(Form f) => ScrollHosts.TryGetValue(f, out _);

    private static void Place(Form f, Rectangle area)
    {
        Rectangle? owner = f.Owner is { } o ? o.Bounds : null;
        if (f.StartPosition is FormStartPosition.CenterParent or FormStartPosition.CenterScreen)
        {
            bool onScreen = f.StartPosition == FormStartPosition.CenterScreen;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = TouchMetrics.Place(f.Size, onScreen ? null : owner, area);
        }
        else if (f.StartPosition == FormStartPosition.Manual)
        {
            f.Location = TouchMetrics.Place(f.Size, new Rectangle(f.Location, f.Size), area);
        }
    }

    internal static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is AxHost)
                continue;
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }
}

/// <summary>
/// Tenere premuto al posto del clic destro: dopo mezzo secondo di pressione col dito (o il mouse) fermo scatta
/// <c>onLongPress</c> col punto premuto. Il movimento oltre la tolleranza lo annulla (è un trascinamento).
/// </summary>
internal sealed class LongPress : IDisposable
{
    private readonly Control _control;
    private readonly Action<Point> _onLongPress;
    private readonly Func<DateTime> _now;
    private readonly LongPressDetector _detector;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private DateTime _lastFired = DateTime.MinValue;

    private LongPress(Control control, Action<Point> onLongPress, Func<DateTime> now)
    {
        _control = control;
        _onLongPress = onLongPress;
        _now = now;
        _detector = new LongPressDetector(tolerance: TouchMetrics.Scale(TouchMetrics.LongPressTolerance, control.DeviceDpi));
        _timer.Tick += (_, _) => Tick();
        control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Press(e.Location); };
        control.MouseMove += (_, e) => _detector.Move(e.Location);
        control.MouseUp += (_, _) => Release();
        control.MouseLeave += (_, _) => Release();
        control.Disposed += (_, _) => Dispose();
    }

    public static LongPress Attach(Control control, Action<Point> onLongPress, Func<DateTime>? now = null) =>
        new(control, onLongPress, now ?? (() => DateTime.UtcNow));

    /// <summary>Vero se il tocco prolungato è appena scattato: il menu nativo dello stesso gesto non deve aprirsi due volte.</summary>
    public bool FiredRecently => _now() - _lastFired < TimeSpan.FromMilliseconds(1500);

    internal void Press(Point location)
    {
        _detector.Press(location, _now());
        _timer.Start();
    }

    internal void Release()
    {
        _detector.Release();
        _timer.Stop();
    }

    /// <summary>Controlla se è ora di scattare (la chiama il timer; le prove la chiamano a mano).</summary>
    internal void Tick()
    {
        if (!_detector.Poll(_now()))
            return;
        _timer.Stop();
        _lastFired = _now();
        _onLongPress(_detector.Origin);
    }

    public void Dispose() => _timer.Dispose();
}
