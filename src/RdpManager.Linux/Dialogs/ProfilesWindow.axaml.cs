using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RdpManager.Linux.Dialogs;

public sealed partial class ProfilesWindow : Window
{
    public ProfilesWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
