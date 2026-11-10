using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Comportamenti della finestra principale legati dal XAML (<c>MainWindow.axaml</c>) senza toccare il codice della finestra:
/// avvio dei servizi in sottofondo, attività dell'utente e battito per il blocco a tempo (#28), selezione multipla e
/// trascinamento nell'albero (#6, #8).
/// </summary>
public static class WindowBehaviors
{
    public static readonly AttachedProperty<bool> BackgroundWorkProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("BackgroundWork", typeof(WindowBehaviors));

    public static readonly AttachedProperty<bool> MultiSelectProperty =
        AvaloniaProperty.RegisterAttached<TreeView, bool>("MultiSelect", typeof(WindowBehaviors));

    public static readonly AttachedProperty<bool> DragDropProperty =
        AvaloniaProperty.RegisterAttached<TreeView, bool>("DragDrop", typeof(WindowBehaviors));

    public static bool GetBackgroundWork(Window w) => w.GetValue(BackgroundWorkProperty);
    public static void SetBackgroundWork(Window w, bool v) => w.SetValue(BackgroundWorkProperty, v);
    public static bool GetMultiSelect(TreeView t) => t.GetValue(MultiSelectProperty);
    public static void SetMultiSelect(TreeView t, bool v) => t.SetValue(MultiSelectProperty, v);
    public static bool GetDragDrop(TreeView t) => t.GetValue(DragDropProperty);
    public static void SetDragDrop(TreeView t, bool v) => t.SetValue(DragDropProperty, v);

    /// <summary>Intervallo del battito: blocco a tempo e, ogni quarto battito, la raggiungibilità dei PC.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    static WindowBehaviors()
    {
        BackgroundWorkProperty.Changed.AddClassHandler<Window>((w, e) => { if (e.NewValue is true) AttachBackgroundWork(w); });
        MultiSelectProperty.Changed.AddClassHandler<TreeView>((t, e) => { if (e.NewValue is true) AttachMultiSelect(t); });
        DragDropProperty.Changed.AddClassHandler<TreeView>((t, e) => { if (e.NewValue is true) AttachDragDrop(t); });
    }

    private static void AttachBackgroundWork(Window window)
    {
        DispatcherTimer? timer = null;
        window.Opened += (_, _) =>
        {
            if (window.DataContext is not MainWindowViewModel vm)
                return;
            vm.StartBackgroundWork();
            timer = new DispatcherTimer { Interval = TickInterval };
            timer.Tick += (_, _) => vm.Tick();
            timer.Start();
        };
        window.Closed += (_, _) => timer?.Stop();
        void Activity(object? s, RoutedEventArgs e) => (window.DataContext as MainWindowViewModel)?.NotifyActivity();
        window.AddHandler(InputElement.KeyDownEvent, Activity, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, Activity, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, Activity, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void AttachMultiSelect(TreeView tree)
    {
        tree.SelectionMode = SelectionMode.Multiple;
        tree.SelectionChanged += (_, _) =>
        {
            if (tree.DataContext is MainWindowViewModel vm)
                vm.SetSelection(tree.SelectedItems.OfType<TreeNodeViewModel>());
        };
    }

    private const string DragFormat = "rdpmanager/tree-node";

    private static void AttachDragDrop(TreeView tree)
    {
        Point? start = null;
        TreeNodeViewModel? pressed = null;
        DragDrop.SetAllowDrop(tree, true);
        tree.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pressed = NodeAt(e.Source);
            start = e.GetPosition(tree);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        tree.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (pressed is null || start is null || !e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
                return;
            var delta = e.GetPosition(tree) - start.Value;
            if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
                return;
            var node = pressed;
            pressed = null;
            var data = new DataObject();
            data.Set(DragFormat, node.Key);
#pragma warning disable CS0618 // l'API di trascinamento classica resta valida in Avalonia 11
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        tree.AddHandler(InputElement.PointerReleasedEvent, (_, _) => pressed = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        tree.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
#pragma warning disable CS0618
            e.DragEffects = e.Data.Contains(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
#pragma warning restore CS0618
        });
        tree.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (tree.DataContext is not MainWindowViewModel vm)
                return;
#pragma warning disable CS0618
            if (e.Data.Get(DragFormat) is not string key)
                return;
#pragma warning restore CS0618
            var source = vm.FindNode(key);
            if (source is null)
                return;
            var target = NodeAt(e.Source);
            if (target != source)
                vm.MoveNode(source, target);
            e.Handled = true;
        });
    }

    private static TreeNodeViewModel? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as TreeNodeViewModel;
}
