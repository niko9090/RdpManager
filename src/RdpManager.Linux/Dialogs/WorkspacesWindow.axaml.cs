using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RdpManager.Linux.Dialogs;

public sealed partial class WorkspacesWindow : Window
{
    public WorkspacesWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
