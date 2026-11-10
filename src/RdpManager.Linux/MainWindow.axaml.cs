using Avalonia.Controls;
using Avalonia.Input;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ConnectionTree.DoubleTapped += (_, _) => (DataContext as MainWindowViewModel)?.ConnectSelected();
        ConnectionTree.KeyDown += OnTreeKeyDown;
        KeyDown += OnWindowKeyDown;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            vm.ExitRequested += (_, _) => Close();
            vm.FocusSearchRequested += (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); };
        };
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
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
}
