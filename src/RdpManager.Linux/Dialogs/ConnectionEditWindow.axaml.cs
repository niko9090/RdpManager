using Avalonia.Controls;
using Avalonia.Interactivity;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

public sealed partial class ConnectionEditWindow : Window
{
    public ConnectionEditWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionEditViewModel vm && vm.TryCommit())
            Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
