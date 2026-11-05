using RdpManager.Core;

namespace RdpManager.UI;

// Uso col dito (dalla 1.6.2): azioni principali con pulsanti grandi in fondo alla barra laterale (che resta a
// sinistra: niente nella parte alta dello schermo), tocco prolungato al posto del clic destro, finestra nello schermo.
public sealed partial class MainForm
{
    private readonly Button _touchConnect = TouchButton("Collega", "Apre la connessione scelta (o tutta la cartella scelta)");
    private readonly Button _touchDisconnect = TouchButton("Scollega", "Disconnette la sessione visibile e chiude la scheda");
    private readonly Button _touchPrevious = TouchButton("‹ Precedente", "Passa alla sessione precedente");
    private readonly Button _touchNext = TouchButton("Successiva ›", "Passa alla sessione successiva");
    private readonly Button _touchFullScreen = TouchButton("Schermo intero", "Schermo intero (si esce dalla barra a schermo intero o con F11)");
    private readonly Button _touchKeyboard = TouchButton("Tastiera", "Apre la tastiera su schermo di Windows: i tasti vanno alla sessione visibile");
    private LongPress? _treeLongPress;
    private bool _longPressMenuOpen;

    private static Button TouchButton(string text, string tip) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(2), Tag = tip, UseMnemonic = false, TabStop = false };

    /// <summary>Pulsanti grandi per le azioni principali, senza tasto destro né scorciatoie. Vanno a capo se la barra è stretta.</summary>
    private Control BuildTouchBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2),
        };
        Button[] buttons = [_touchConnect, _touchDisconnect, _touchPrevious, _touchNext, _touchFullScreen, _touchKeyboard];
        foreach (var b in buttons)
        {
            _tips.SetToolTip(b, (string)b.Tag!);
            bar.Controls.Add(b);
        }
        _touchConnect.Click += (_, _) => TouchConnect();
        _touchDisconnect.Click += (_, _) => { if (CurrentView is { } v) CloseSession(v); };
        _touchPrevious.Click += (_, _) => CycleTab(-1);
        _touchNext.Click += (_, _) => CycleTab(1);
        _touchFullScreen.Click += (_, _) => ToggleFullScreen();
        _touchKeyboard.Click += (_, _) => OpenScreenKeyboard();
        UpdateTouchBar();
        return bar;
    }

    /// <summary>Per le prove automatiche: la barra dei pulsanti da dito.</summary>
    internal IReadOnlyList<Button> TouchButtons => [_touchConnect, _touchDisconnect, _touchPrevious, _touchNext, _touchFullScreen, _touchKeyboard];

    internal Button TouchConnectButton => _touchConnect;
    internal Button TouchDisconnectButton => _touchDisconnect;
    internal Button TouchPreviousButton => _touchPrevious;
    internal Button TouchNextButton => _touchNext;
    internal Button TouchFullScreenButton => _touchFullScreen;
    internal Button TouchKeyboardButton => _touchKeyboard;

    private void TouchConnect()
    {
        if (_tree.SelectedNode?.Tag is FolderTag f)
            ConnectFolder(f.Path);
        else
            ConnectSelected();
    }

    /// <summary>Tastiera su schermo di Windows, poi il fuoco torna alla sessione visibile.</summary>
    internal void OpenScreenKeyboard()
    {
        if (CurrentView is { } v)
            v.OpenScreenKeyboard();
        else
            TouchUi.OpenScreenKeyboard(this);
    }

    /// <summary>Pulsanti che non servono (nessuna sessione) spenti, quelli del cambio sessione solo con almeno due.</summary>
    private void UpdateTouchBar()
    {
        _touchDisconnect.Enabled = _touchFullScreen.Enabled = CurrentView is not null;
        _touchPrevious.Enabled = _touchNext.Enabled = _tabs.TabCount > 1;
    }

    /// <summary>Adatta al dito ciò che è già costruito (e ciò che i menu aggiungeranno dopo); si rifà se cambiano i dpi.</summary>
    private void ApplyTouch()
    {
        // 40 px logici sempre (come dalla 1.6.2); 48 e le altre comodità del dito solo in modalità tocco.
        int min = TouchUi.Px(this, TouchPolicy.TargetSize(_touchActive));
        TouchUi.Apply(_sidebar, min);
        TouchUi.TouchifyStrip(_menu, min);
        TouchUi.TouchifyStrip(_treeMenu, min);
        TouchUi.TouchifyStrip(_tabMenu, min);
        _tree.ItemHeight = _touchActive ? TouchUi.Px(this, TouchPolicy.RowHeight) : TouchUi.MinTarget(this);
        TouchUi.SetWideTabs(_tabs, _touchActive, min);
    }

    private bool _touchActive;
    private PanScroller? _pan;
    private bool _touchPressed;
    private Point _tabsDown;
    private bool _tabsDownOnStrip;

    /// <summary>Modalità tocco in corso (per le prove automatiche).</summary>
    internal bool TouchModeActive => _touchActive;

    /// <summary>Si ricalcola se la modalità tocco è attiva (impostazione, schermo touch) e si applicano le misure.</summary>
    internal void RefreshTouchMode()
    {
        bool active = TouchPolicy.IsActive(_doc.Settings.TouchMode, TouchUi.HasTouchScreen());
        if (active == _touchActive && _touchModeApplied)
            return;
        _touchActive = active;
        _touchModeApplied = true;
        ApplyTouch();
    }

    private bool _touchModeApplied;

    /// <summary>Tocco sulla X di una scheda larga: chiude quella sessione. Falso se il punto non è su una X.</summary>
    internal bool TryCloseTabAt(Point location)
    {
        if (!_touchActive || !UiHasTabs())
            return false;
        var rects = Enumerable.Range(0, _tabs.TabCount).Select(_tabs.GetTabRect).ToList();
        int index = TabCloseLayout.HitClose(rects, location, DeviceDpi);
        if (index < 0 || _tabs.TabPages[index].Tag is not RdpSessionView view)
            return false;
        CloseSession(view);
        return true;
    }

    private bool UiHasTabs() => _tabs.TabCount > 0;

    /// <summary>Scorrimento laterale sulla riga delle schede: passa alla successiva o alla precedente. Restituisce lo spostamento fatto.</summary>
    internal int SwipeTabs(Point down, Point up)
    {
        int direction = SwipeDetector.Direction(down, up, TouchUi.Px(this, SwipeDetector.MinDistance));
        if (direction != 0 && _tabs.TabCount > 1)
            CycleTab(direction);
        return direction;
    }

    private void WireTouchGestures()
    {
        // Elenco: trascinare col dito scorre (la barra sottile non serve); col mouse resta il trascinamento delle connessioni.
        _tree.MouseDown += (_, e) =>
        {
            _touchPressed = e.Button == MouseButtons.Left && _touchActive && TouchUi.IsTouchInput();
            if (!_touchPressed)
                return;
            _pan = new PanScroller(_tree.ItemHeight, TouchUi.Px(this, 8));
            _pan.Begin(e.Y);
        };
        _tree.MouseMove += (_, e) =>
        {
            if (_touchPressed && _pan is { } pan && e.Button == MouseButtons.Left && pan.Move(e.Y) is var rows and not 0)
                NativeMethods.ScrollRows(_tree, rows);
        };
        _tree.MouseUp += (_, _) => _pan?.End();
        // Le cartelle si aprono con un tocco sulla riga (il segno +/- è troppo piccolo).
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.Clicks != 1 || !_touchPressed || _pan?.Active == true || e.Node.Nodes.Count == 0)
                return;
            if (e.Node.Tag is not (FolderTag or SectionTag))
                return;
            if (_tree.HitTest(e.Location).Location == TreeViewHitTestLocations.PlusMinus)
                return; // il controllo l'ha già aperta o chiusa
            e.Node.Toggle();
        };

        // Ricerca: un tocco col dito apre la tastiera su schermo.
        _search.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && TouchPolicy.ShouldOpenKeyboardOnTap(_touchActive, TouchUi.IsTouchInput()))
                TouchUi.OpenScreenKeyboardQuietly();
        };

        // Schede: X grande per chiudere, scorrimento laterale per cambiare.
        _tabs.MouseDown += (_, e) =>
        {
            _tabsDown = e.Location;
            _tabsDownOnStrip = e.Button == MouseButtons.Left && _touchActive && TouchUi.IsTouchInput() && e.Y < _tabs.DisplayRectangle.Top;
        };
        _tabs.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (_tabsDownOnStrip)
            {
                _tabsDownOnStrip = false;
                if (SwipeTabs(_tabsDown, e.Location) != 0)
                    return;
            }
            TryCloseTabAt(e.Location);
        };
    }

    private void InitTouch()
    {
        RefreshTouchMode();
        ApplyTouch(); // di nuovo: i menu e la barra possono aver aggiunto voci dopo la prima volta
        DpiChanged += (_, _) => ApplyTouch();
        // Un portatile che si trasforma in tablet (o uno schermo che si collega) cambia la modalità automatica.
        Activated += (_, _) => { if (_doc.Settings.TouchMode == TouchMode.Auto) RefreshTouchMode(); };
        WireTouchGestures();

        // Tenere premuto sull'elenco o su una scheda = clic destro.
        _treeLongPress = LongPress.Attach(_tree, ShowTreeMenuAt);
        _treeMenu.Opening += (_, e) =>
        {
            // Lo stesso gesto fa aprire anche il menu nativo di Windows: una volta sola.
            if (!_longPressMenuOpen && _treeLongPress?.FiredRecently == true)
                e.Cancel = true;
        };
        LongPress.Attach(_tabs, ShowTabMenuAt);

        // La finestra non esce dallo schermo (1400x900 non sta in 1366x912 al 150%).
        Load += (_, _) => KeepInsideWorkArea();
    }

    internal void ShowTreeMenuAt(Point location)
    {
        if (_tree.GetNodeAt(location) is not { } node)
            return;
        _tree.SelectedNode = node;
        _longPressMenuOpen = true;
        try { _treeMenu.Show(_tree, location); }
        finally { _longPressMenuOpen = false; }
    }

    internal void ShowTabMenuAt(Point location)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            if (!_tabs.GetTabRect(i).Contains(location))
                continue;
            _tabs.SelectedIndex = i;
            _tabMenu.Show(_tabs, location);
            return;
        }
    }

    /// <summary>Per le prove automatiche: il tocco prolungato sull'elenco.</summary>
    internal LongPress? TreeLongPress => _treeLongPress;

    private void KeepInsideWorkArea()
    {
        if (WindowState != FormWindowState.Normal)
            return;
        var area = TouchUi.WorkArea(this);
        var size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        var location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        if (size != Size || location != Location)
            Bounds = new Rectangle(location, size);
    }
}
