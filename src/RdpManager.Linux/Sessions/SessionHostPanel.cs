using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Sessions;

/// <summary>
/// L'area delle sessioni. Ogni sessione ha la sua vista, creata una volta sola e tenuta viva: cambiando scheda si
/// mostra o si nasconde (il modello <c>TabControl</c> la ricreerebbe, e una sessione incorporata in una superficie X11
/// non sopravvive a una vista nuova). Con la vista affiancata due sessioni stanno una accanto all'altra, divise da una
/// barra trascinabile; una sessione staccata (finestra a parte) non ha vista qui.
/// </summary>
public sealed class SessionHostPanel : Grid
{
    private readonly Dictionary<SessionTabViewModel, ContentControl> _views = [];
    private readonly GridSplitter _splitter = new() { Width = 6, ResizeDirection = GridResizeDirection.Columns, IsVisible = false, Background = Brushes.Gray };
    private readonly TextBlock _detachedNote = new()
    {
        Text = "Questa sessione è in una finestra separata. Chiudi quella finestra (o usa «Stacca in una finestra» di nuovo) per riportarla qui.",
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 460,
        Margin = new Thickness(20),
        IsVisible = false,
    };
    private MainWindowViewModel? _vm;

    public SessionHostPanel()
    {
        ColumnDefinitions = [new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(0, GridUnitType.Pixel), new ColumnDefinition(0, GridUnitType.Pixel)];
        _splitter.SetValue(Grid.ColumnProperty, 1);
        Children.Add(_splitter);
        _detachedNote.SetValue(Grid.ColumnSpanProperty, 3);
        Children.Add(_detachedNote);
        _splitter.DragCompleted += (_, _) => RememberRatio();
    }

    public void Bind(MainWindowViewModel vm)
    {
        if (_vm is not null)
            return;
        _vm = vm;
        vm.Tabs.CollectionChanged += OnTabsChanged;
        vm.PropertyChanged += OnVmChanged;
        vm.LayoutChanged += (_, _) => Refresh();
        foreach (var tab in vm.Tabs)
            tab.PropertyChanged += OnTabChanged;
        Refresh();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SessionTabViewModel tab in e.NewItems)
                tab.PropertyChanged += OnTabChanged;
        if (e.OldItems is not null)
            foreach (SessionTabViewModel tab in e.OldItems)
                tab.PropertyChanged -= OnTabChanged;
        Refresh();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTabViewModel.IsDetached))
            Refresh();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            Refresh();
    }

    private void RememberRatio()
    {
        if (_vm is null || !_vm.IsSplit)
            return;
        var left = ColumnDefinitions[0].ActualWidth;
        var right = ColumnDefinitions[2].ActualWidth;
        if (left + right > 0)
            _vm.SplitRatio = left / (left + right);
    }

    private void Refresh()
    {
        if (_vm is null)
            return;
        // Viste delle schede chiuse o staccate.
        foreach (var tab in _views.Keys.Where(t => !_vm.Tabs.Contains(t) || t.IsDetached).ToList())
        {
            Children.Remove(_views[tab]);
            _views.Remove(tab);
        }
        // Viste nuove.
        foreach (var tab in _vm.Tabs.Where(t => !t.IsDetached && !_views.ContainsKey(t)))
        {
            var view = new ContentControl { Content = tab.Content, IsVisible = false };
            _views[tab] = view;
            Children.Insert(0, view);
        }

        var split = _vm.IsSplit && _views.ContainsKey(_vm.SplitLeft!) && _views.ContainsKey(_vm.SplitRight!);
        var ratio = SplitLayout.ClampRatio(_vm.SplitRatio);
        ColumnDefinitions[0].Width = new GridLength(split ? ratio : 1, GridUnitType.Star);
        ColumnDefinitions[1].Width = new GridLength(split ? 6 : 0, GridUnitType.Pixel);
        ColumnDefinitions[2].Width = new GridLength(split ? 1 - ratio : 0, GridUnitType.Star);
        _splitter.IsVisible = split;

        foreach (var (tab, view) in _views)
        {
            if (split && tab == _vm.SplitLeft)
            {
                view.SetValue(Grid.ColumnProperty, 0);
                view.SetValue(Grid.ColumnSpanProperty, 1);
                view.IsVisible = true;
            }
            else if (split && tab == _vm.SplitRight)
            {
                view.SetValue(Grid.ColumnProperty, 2);
                view.SetValue(Grid.ColumnSpanProperty, 1);
                view.IsVisible = true;
            }
            else
            {
                view.SetValue(Grid.ColumnProperty, 0);
                view.SetValue(Grid.ColumnSpanProperty, split ? 1 : 3);
                view.IsVisible = !split && tab == _vm.SelectedTab;
            }
        }
        _detachedNote.IsVisible = !split && _vm.SelectedTab is { IsDetached: true };
    }
}
