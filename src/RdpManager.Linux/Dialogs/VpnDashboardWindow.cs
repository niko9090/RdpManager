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
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Menu «Plancia»: i PC della VPN con il riepilogo «N di M in linea», la ricerca, il nome scelto dall'utente (con il nome
/// vero del PC sotto), IP, stato (cerchio verde o grigio) e ultimo contatto. Menu col tasto destro: apri, rinomina,
/// nome originale, invia file, copia IP. Si aggiorna da sola ogni 30 secondi; «Apri desktop remoto» (o doppio clic)
/// chiude la finestra e collega il PC scelto.
/// </summary>
internal sealed class VpnDashboardWindow : ToolWindow
{
    private const string Columns = "28,*,130,110,150";

    public VpnDashboardWindow(VpnDashboardViewModel vm) : base(vm, "Plancia marshmelloVPN", 860, 520)
    {
        var error = DialogKit.Error(nameof(vm.ErrorText));
        error.Margin = new Thickness(14, 8, 14, 0);
        var info = DialogKit.BoundLabel(nameof(vm.InfoText), "muted");
        info.Margin = new Thickness(14, 8, 14, 0);
        var sendInfo = DialogKit.BoundLabel(nameof(vm.SendInfoText), "muted");
        sendInfo.Margin = new Thickness(14, 8, 14, 0);
        var sendError = DialogKit.Error(nameof(vm.SendErrorText));
        sendError.Margin = new Thickness(14, 8, 14, 0);

        // In cima: quanti PC sono in linea e la ricerca.
        var summary = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.TextProperty, nameof(vm.SummaryText));
        var search = new TextBox { Watermark = "Cerca per nome, IP o proprietario", Width = 280 }
            .Bind(TextBox.TextProperty, nameof(vm.Query), BindingMode.TwoWay);
        var bar = new DockPanel { Margin = new Thickness(14, 12, 14, 4) };
        DockPanel.SetDock(search, Dock.Right);
        bar.Children.Add(search);
        bar.Children.Add(summary);
        var top = new StackPanel { Children = { bar, error, info, sendInfo, sendError } };

        var header = Header();
        var list = new ListBox { ItemsSource = vm.Rows, Background = Brushes.Transparent }
            .Bind(SelectingItemsControl.SelectedItemProperty, nameof(vm.Selected), BindingMode.TwoWay);
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VpnDashboardItem>((item, _) => Row(item));
        list.DoubleTapped += (_, _) => vm.OpenSelected();
        var copyIp = new RelayCommand(async () =>
        {
            if (vm.Selected is { } pc && Clipboard is { } clip)
                await clip.SetTextAsync(pc.Address);
        });
        list.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                new MenuItem { Header = "Apri desktop remoto", Command = vm.OpenCommand },
                new MenuItem { Header = "Rinomina…", Command = vm.RenameCommand },
                new MenuItem { Header = "Torna al nome originale", Command = vm.ResetNameCommand },
                new MenuItem { Header = "Invia file…", Command = vm.SendFileCommand, IsVisible = vm.CanSendFeature },
                new Separator(),
                new MenuItem { Header = "Copia l'indirizzo IP", Command = copyIp },
            },
        };
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(list);

        var refresh = new Button { Content = "Aggiorna", MinWidth = 96, Command = vm.RefreshCommand };
        var rename = new Button { Content = "Rinomina…", MinWidth = 96, Command = vm.RenameCommand };
        var open = new Button { Content = "Apri desktop remoto", MinWidth = 96, Command = vm.OpenCommand };
        open.Classes.Add("accent");
        var sendFile = new Button { Content = "Invia file…", MinWidth = 96, Command = vm.SendFileCommand, IsVisible = vm.CanSendFeature };
        Content = DialogKit.Frame(body,
            DialogKit.Buttons(refresh, rename, sendFile, open, DialogKit.Button("Chiudi", (_, _) => Close(false), isCancel: true)), top);

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

    private static Grid Header()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(18, 6, 18, 2) };
        string[] titles = ["Nome", "IP nella VPN", "Stato", "Ultimo contatto"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold, Opacity = 0.7 };
            Grid.SetColumn(t, i + 1);
            g.Children.Add(t);
        }
        return g;
    }

    private static Control Row(VpnDashboardItem? item)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(4, 4) };
        if (item is null)
            return g;
        g.Children.Add(new Ellipse
        {
            Width = 12, Height = 12, Fill = Brush.Parse(item.StatusColor), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        name.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.HasSubText)
        {
            var sub = new TextBlock { Text = item.SubText, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.Classes.Add("muted");
            name.Children.Add(sub);
        }
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        string[] cells = [item.Address, item.StateText, item.LastContactText];
        for (int i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (!item.Connected)
                t.Classes.Add("muted");
            Grid.SetColumn(t, i + 2);
            g.Children.Add(t);
        }
        return g;
    }
}
