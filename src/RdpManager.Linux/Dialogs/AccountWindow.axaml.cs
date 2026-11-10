using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RdpManager.Linux.Dialogs;

public sealed partial class AccountWindow : Window
{
    public AccountWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
