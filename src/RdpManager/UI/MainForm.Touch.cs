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
        int min = TouchUi.MinTarget(this);
        TouchUi.Apply(_sidebar);
        TouchUi.TouchifyStrip(_menu, min);
        TouchUi.TouchifyStrip(_treeMenu, min);
        TouchUi.TouchifyStrip(_tabMenu, min);
        TouchUi.TouchifyTabs(_tabs, min);
    }

    private void InitTouch()
    {
        ApplyTouch();
        DpiChanged += (_, _) => ApplyTouch();

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
