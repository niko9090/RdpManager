using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace RdpManager.Linux.Dialogs.Tools;

/// <summary>Piccoli attrezzi per costruire in codice le finestre degli strumenti (stesso stile del resto dell'app).</summary>
internal static class DialogKit
{
    public static T Bind<T>(this T control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.Default) where T : Control
    {
        control.Bind(property, new Binding(path, mode));
        return control;
    }

    public static TextBlock Label(string text, string? cls = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (cls is not null)
            t.Classes.Add(cls);
        return t;
    }

    public static TextBlock BoundLabel(string path, string? cls = null) =>
        Label("", cls).Bind(TextBlock.TextProperty, path);

    public static TextBlock Error(string path = "Error")
    {
        var t = BoundLabel(path, "error");
        t.Bind(Visual.IsVisibleProperty, new Binding(path) { Converter = ObjectConverters.IsNotNull });
        return t;
    }

    public static Control Row(string label, Control field, double labelWidth = 170)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*") };
        var l = Label(label, "label");
        grid.Children.Add(l);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    public static Button Button(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click, bool accent = false, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, MinWidth = 96, IsDefault = isDefault, IsCancel = isCancel };
        if (accent)
            b.Classes.Add("accent");
        b.Click += click;
        return b;
    }

    public static StackPanel Buttons(params Control[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        p.Children.AddRange(buttons);
        return p;
    }

    /// <summary>Contenuto standard: corpo che scorre in alto, riga dei pulsanti in basso.</summary>
    public static Control Frame(Control body, Control buttons, Control? above = null)
    {
        var bar = new Border { Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        bar.Classes.Add("bar");
        DockPanel.SetDock(bar, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(bar);
        if (above is not null)
        {
            DockPanel.SetDock(above, Dock.Top);
            dock.Children.Add(above);
        }
        dock.Children.Add(body);
        return dock;
    }

    public static StackPanel Page(params Control[] children)
    {
        var p = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        p.Children.AddRange(children);
        return p;
    }

    public static IBrush Muted => Brushes.Gray;
}
