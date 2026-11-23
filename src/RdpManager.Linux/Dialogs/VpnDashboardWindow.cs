using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using RdpManager.Core;
using RdpManager.Linux.Dialogs.Tools;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// «Strumenti → Plancia VPN»: nome, indirizzo nella VPN, stato (cerchio verde o grigio) e ultimo contatto di ogni PC.
/// Si aggiorna da sola ogni 30 secondi; «Apri desktop remoto» (o doppio clic) chiude la finestra e collega il PC scelto.
/// </summary>
internal sealed class VpnDashboardWindow : ToolWindow
{
    private const string Columns = "24,200,130,130,*";

    public VpnDashboardWindow(VpnDashboardViewModel vm) : base(vm, "Plancia VPN", 820, 480)
    {
        var error = DialogKit.Error(nameof(vm.ErrorText));
        error.Margin = new Thickness(14, 8, 14, 0);
        var info = DialogKit.BoundLabel(nameof(vm.InfoText), "muted");
        info.Margin = new Thickness(14, 8, 14, 0);
        var top = new StackPanel { Children = { error, info } };

        var header = Row("Nome", "IP nella VPN", "Stato", "Ultimo contatto", null, bold: true);
        var list = new ListBox { ItemsSource = vm.Rows, Background = Brushes.Transparent }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Selected), BindingMode.TwoWay);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VpnDashboardItem>((item, _) =>
            Row(item?.Name ?? "", item?.Address ?? "", item?.StateText ?? "", item?.LastContactText ?? "", item, bold: false));
        list.DoubleTapped += (_, _) => vm.OpenSelected();
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(list);

        var refresh = new Button { Content = "Aggiorna", MinWidth = 96, Command = vm.RefreshCommand };
        var open = new Button { Content = "Apri desktop remoto", MinWidth = 96, Command = vm.OpenCommand };
        open.Classes.Add("accent");
        Content = DialogKit.Frame(body,
            DialogKit.Buttons(refresh, open, DialogKit.Button("Chiudi", (_, _) => Close(false), isCancel: true)), top);

        vm.CloseRequested += (_, _) => Close(true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VpnDashboardModel.RefreshSeconds) };
        timer.Tick += async (_, _) => await vm.RefreshAsync();
        Opened += async (_, _) =>
        {
            timer.Start();
            await vm.RefreshAsync();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            vm.Cancel();
        };
    }

    private static Grid Row(string a, string b, string c, string d, VpnDashboardItem? item, bool bold)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(14, 3) };
        if (item is not null)
        {
            var circle = new Ellipse
            {
                Width = 12, Height = 12, Fill = Brush.Parse(item.StatusColor), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            g.Children.Add(circle);
        }
        string[] cells = [a, b, c, d];
        for (int i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (bold)
                t.FontWeight = FontWeight.SemiBold;
            if (item is { Connected: false } && !bold)
                t.Classes.Add("muted");
            Grid.SetColumn(t, i + 1);
            g.Children.Add(t);
        }
        return g;
    }
}
