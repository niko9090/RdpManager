using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<SessionTabViewModel, Window> _detached = [];
    private readonly TouchGestureRecognizer _touch = new();
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private double _sidebarWidth = WindowSidebar.DefaultWidth;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        ConnectionTree.DoubleTapped += (_, _) => (DataContext as MainWindowViewModel)?.ConnectSelected();
        ConnectionTree.KeyDown += OnTreeKeyDown;
        KeyDown += OnWindowKeyDown;
        // Le scorciatoie dell'app si intercettano prima che i controlli (per esempio il TabControl con Ctrl+Tab) le consumino.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnTouchDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTouchMove, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTouchUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SidebarEdge.PointerEntered += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.SidebarHover = true; };
        SidebarPanel.PointerExited += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.SidebarHover = false; };
        PositionChanged += (_, _) => UpdateCurrentMonitor();
        Closing += (_, e) =>
        {
            // Da qui in poi le finestre staccate non riagganciano più le schede; se la chiusura viene annullata
            // (per esempio nascondendo la finestra nel vassoio) il flag torna com'era.
            _closing = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (e.Cancel) _closing = false; },
                Avalonia.Threading.DispatcherPriority.Background);
        };
        Closed += (_, _) =>
        {
            _closing = true;
            foreach (var window in _detached.Values.ToList())
                window.Close();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            vm.ExitRequested += (_, _) => Close();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsLocked))
                    ApplyLockToDetachedWindows(vm.IsLocked);
            };
            vm.FocusSearchRequested += (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); };
            vm.FullScreenChanged += (_, on) => ApplyFullScreen(vm, on);
            vm.MoveToNextMonitorRequested += (_, _) => MoveToNextMonitor(vm);
            vm.MonitorRequested += (_, name) => MoveToMonitor(vm, name);
            vm.DetachRequested += (_, tab) => ToggleDetach(vm, tab);
            vm.LayoutChanged += (_, _) => ApplyLayout(vm);
            vm.SettingsChanged += (_, _) =>
            {
                vm.ReloadShortcuts();
                _touch.Map = vm.Document.Settings.GestureMap;
                ApplyLayout(vm);
            };
            ScreenProvider.Source = () => ScreenProvider.FromAvalonia(Screens);
            SessionHost.Bind(vm);
            _sidebarWidth = vm.Document.Settings.SidebarWidth;
            _touch.Map = vm.Document.Settings.GestureMap;
            Opened += (_, _) =>
            {
                UpdateCurrentMonitor();
                if (vm.Document.Settings.PreferredMonitor is { } preferred)
                    MoveToMonitor(vm, preferred);
            };
            ApplyLayout(vm);
        };
    }

    // --- Disposizione: barra laterale, modalità tocco ---

    private void ApplyLayout(MainWindowViewModel vm)
    {
        var columns = MainGrid.ColumnDefinitions;
        var shown = vm.SidebarShown;
        if (!shown && SidebarPanel.IsVisible && columns[0].ActualWidth >= WindowSidebar.MinWidth)
            _sidebarWidth = Math.Clamp(columns[0].ActualWidth, WindowSidebar.MinWidth, WindowSidebar.MaxWidth);
        SidebarPanel.IsVisible = shown;
        SidebarSplitter.IsVisible = shown;
        var edge = !shown && !vm.SidebarPinned;
        SidebarEdge.IsVisible = edge;
        columns[0].Width = new GridLength(shown ? _sidebarWidth : edge ? 6 : 0);
        Classes.Set("touch", vm.TouchActive);
    }

    // --- Schermo intero e monitor ---

    private void ApplyFullScreen(MainWindowViewModel vm, bool on)
    {
        if (on)
        {
            _stateBeforeFullScreen = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            if (vm.Document.Settings.PreferredMonitor is { } preferred)
            {
                WindowState = WindowState.Normal;
                MoveToMonitor(vm, preferred);
            }
            WindowState = WindowState.FullScreen;
        }
        else
            WindowState = _stateBeforeFullScreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullScreen;
    }

    private Rectangle WindowRectangle() =>
        new(Position.X, Position.Y, Math.Max(1, (int)(Bounds.Width * RenderScaling)), Math.Max(1, (int)(Bounds.Height * RenderScaling)));

    private void UpdateCurrentMonitor()
    {
        if (DataContext is MainWindowViewModel vm)
            vm.CurrentMonitor = MonitorLayout.ScreenOf(ScreenProvider.All(), WindowRectangle())?.DeviceName;
    }

    private void MoveToNextMonitor(MainWindowViewModel vm)
    {
        var screens = ScreenProvider.All();
        if (screens.Count < 2)
        {
            vm.ShowNotice("C'è un solo monitor collegato.");
            return;
        }
        var ordered = MonitorLayout.Ordered(screens);
        var next = MonitorLayout.NextScreen(ordered, MonitorLayout.ScreenOf(ordered, WindowRectangle()));
        if (next is not null)
            MoveWindowTo(vm, next);
    }

    private void MoveToMonitor(MainWindowViewModel vm, string? name)
    {
        var screens = ScreenProvider.All();
        if (name is null || screens.Count == 0)
            return;
        var target = screens.FirstOrDefault(s => s.DeviceName == name);
        if (target is not null)
            MoveWindowTo(vm, target);
    }

    private void MoveWindowTo(MainWindowViewModel vm, ScreenInfo target)
    {
        var from = MonitorLayout.ScreenOf(ScreenProvider.All(), WindowRectangle()) ?? target;
        var wasFull = WindowState == WindowState.FullScreen;
        if (wasFull)
            WindowState = WindowState.Normal;
        var moved = MonitorLayout.MoveToScreen(WindowRectangle(), from, target);
        Position = new PixelPoint(moved.X, moved.Y);
        if (wasFull)
            WindowState = WindowState.FullScreen;
        vm.CurrentMonitor = target.DeviceName;
    }

    // --- Sessione in una finestra staccata ---

    /// <summary>Con l'app bloccata le finestre delle sessioni staccate si nascondono (sono fuori dalla schermata del PIN); allo sblocco tornano.</summary>
    private void ApplyLockToDetachedWindows(bool locked)
    {
        foreach (var window in _detached.Values.ToList())
        {
            if (locked)
            {
                window.WindowState = WindowState.Minimized;
                window.Hide();
            }
            else
                window.Show();
        }
    }

    private void ToggleDetach(MainWindowViewModel vm, SessionTabViewModel tab)
    {
        if (_detached.TryGetValue(tab, out var existing))
        {
            existing.Close(); // chiudendo la finestra la sessione torna nella scheda
            return;
        }
        if (tab.Session is not FreeRdpSession session)
        {
            vm.ShowNotice("Questa scheda non ha una sessione da staccare.");
            return;
        }
        session.Rehost();
        tab.IsDetached = true;
        var window = new Window
        {
            Title = tab.Title,
            Width = 1100,
            Height = 700,
            Content = new ContentControl { Content = tab.Content },
        };
        _detached[tab] = window;
        Dialogs.WindowBehaviors.TrackActivity(window);
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionTabViewModel.Title)) window.Title = tab.Title; };
        session.Closed += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            _detached.Remove(tab);
            if (_closing || !vm.Tabs.Contains(tab))
                return;
            session.Rehost();
            tab.IsDetached = false;
        };
        window.Show(this);
        vm.ShowNotice("Sessione staccata: la sessione si è riaperta nella nuova finestra.");
    }

    // --- Tastiera ---

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLocked)
            return;
        switch (e.Key)
        {
            case Key.Enter when vm.SelectedNode?.Connection is not null:
                vm.ConnectSelected();
                e.Handled = true;
                break;
            case Key.Delete:
                vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                vm.EditCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.IsLocked ?? false)
            return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N)
        {
            (DataContext as MainWindowViewModel)?.NewConnectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLocked)
            return;
        var command = (e.Key, e.KeyModifiers) switch
        {
            (Key.F1, KeyModifiers.None) => vm.ShortcutsHelpCommand,
            (Key.F5, KeyModifiers.None) => vm.ReconnectCommand,
            (Key.F11, KeyModifiers.None) => vm.FullScreenCommand,
            (Key.F4, KeyModifiers.Control) => vm.CloseCurrentTabCommand,
            (Key.Tab, KeyModifiers.Control) => vm.NextTabCommand,
            (Key.Tab, KeyModifiers.Control | KeyModifiers.Shift) => vm.PreviousTabCommand,
            (Key.D, KeyModifiers.Control | KeyModifiers.Shift) => vm.DetachTabCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null))
                command.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None && vm.TryRunShortcut(KeyCombo.From(e.Key, e.KeyModifiers)))
            e.Handled = true;
    }

    // --- Tocco ---

    private static System.Drawing.Point ToPoint(Avalonia.Point p) => new((int)p.X, (int)p.Y);

    private void RunTouch(IReadOnlyList<TouchAction> actions)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLocked)
            return;
        foreach (var action in actions)
            if (TouchActionKinds.IsWindowCommand(action.Kind) || TouchActionKinds.IsRemoteKeys(action.Kind))
                vm.RunTouchAction(action.Kind);
    }

    private void OnTouchDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
            return;
        (DataContext as MainWindowViewModel)?.NoteTouchSeen();
        RunTouch(_touch.Down((int)e.Pointer.Id, ToPoint(e.GetPosition(this)), DateTime.UtcNow));
    }

    private void OnTouchMove(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
            RunTouch(_touch.Move((int)e.Pointer.Id, ToPoint(e.GetPosition(this)), DateTime.UtcNow));
    }

    private void OnTouchUp(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
            RunTouch(_touch.Up((int)e.Pointer.Id, ToPoint(e.GetPosition(this)), DateTime.UtcNow));
    }

    /// <summary>Il clic destro su una scheda la seleziona, così il menu della scheda agisce su quella.</summary>
    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || DataContext is not MainWindowViewModel { IsLocked: false } vm)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<TabItem>()?.DataContext is SessionTabViewModel tab)
            vm.SelectedTab = tab;
    }
}
