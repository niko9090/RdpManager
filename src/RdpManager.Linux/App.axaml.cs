using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using RdpManager.Core;
using RdpManager.Linux.Dialogs;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialogs = new DialogService();
            MainWindowViewModel? viewModel = null;
            var sessions = new FreeRdpSessionHost(
                gatewayCredentials: c => viewModel?.Document.ResolveGatewayCredentials(c),
                post: a => Dispatcher.UIThread.Post(a));
            viewModel = new MainWindowViewModel(new ConnectionStore(), sessions, dialogs);
            desktop.Exit += (_, _) => sessions.ShutdownAll(TimeSpan.FromSeconds(4));
            var window = new MainWindow { DataContext = viewModel };
            dialogs.Owner = window;
            ApplyTheme(viewModel.Document.Settings.Theme);
            viewModel.SettingsChanged += (_, _) => ApplyTheme(viewModel.Document.Settings.Theme);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
