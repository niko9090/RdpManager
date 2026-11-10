using Avalonia.Controls;
using Avalonia.Interactivity;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // Una pagina con un valore non valido resta aperta e mostra l'errore.
        if (DataContext is SettingsViewModel vm && !vm.Validate())
            return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
