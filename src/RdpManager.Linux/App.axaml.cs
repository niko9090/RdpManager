using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using RdpManager.Core;
using RdpManager.Linux.Dialogs;
using RdpManager.Linux.Features;
using RdpManager.Linux.Platform;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux;

public sealed class App : Application
{
    private MainWindow? _window;
    private MainWindowViewModel? _viewModel;
    private TrayIcon? _tray;
    private X11GlobalHotkey? _hotkey;
    private bool _trayHost;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialogs = new DialogService();
            var shell = new AvaloniaShellUi();
            MainWindowViewModel? viewModel = null;
            var sessions = new FreeRdpSessionHost(
                gatewayCredentials: c => viewModel?.Document.ResolveGatewayCredentials(c),
                post: a => Dispatcher.UIThread.Post(a));
            sessions.CertificateDecider = prompt => shell.AskCertificateAsync(prompt);
            viewModel = new MainWindowViewModel(new ConnectionStore(), sessions, dialogs);
            _viewModel = viewModel;
            viewModel.StartupConfigFile = Program.StartupConfigFile;
            viewModel.ExitRequested += (_, _) => _exiting = true; // prima del gestore della finestra, che chiude
            desktop.Exit += (_, _) =>
            {
                viewModel.ShutdownBlockB();
                _hotkey?.Dispose();
                sessions.ShutdownAll(TimeSpan.FromSeconds(4));
            };
            var window = new MainWindow { DataContext = viewModel };
            _window = window;
            dialogs.Owner = window;
            shell.Owner = window;
            ApplyTheme(viewModel.Document.Settings.Theme);
            viewModel.SettingsChanged += (_, _) =>
            {
                ApplyTheme(viewModel.Document.Settings.Theme);
                SetupTray();
                SetupHotkey();
            };
            desktop.MainWindow = window;

            viewModel.HasTouchScreen = DesktopSupport.DetectTouchScreen();
            window.Opened += (_, _) =>
            {
                viewModel.StartBlockB(shell);
                SetupHotkey();
                _ = DetectTrayHostAsync();
            };
            window.Closing += OnWindowClosing;
            viewModel.Tabs.CollectionChanged += (_, _) => RebuildTrayMenu();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    // --- Area di notifica ---

    /// <summary>Se qualcosa ospita le icone (StatusNotifierWatcher) l'icona si vede e «resta nell'area di notifica» è sicuro.</summary>
    private async Task DetectTrayHostAsync()
    {
        try
        {
            var result = await new SystemProcessRunner().RunAsync("busctl", ["--user", "list", "--no-legend"]);
            _trayHost = result.Success && DesktopSupport.HasTrayHost(result.StdOut);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _trayHost = false;
        }
        Dispatcher.UIThread.Post(SetupTray);
    }

    private void SetupTray()
    {
        var settings = _viewModel?.Document.Settings;
        if (settings is null)
            return;
        try
        {
            if (!settings.ShowTrayIcon || !_trayHost)
            {
                if (_tray is not null)
                    _tray.IsVisible = false;
                return;
            }
            if (_tray is null)
            {
                _tray = new TrayIcon { Icon = CreateIcon(), ToolTipText = "RdpManager" };
                _tray.Clicked += (_, _) => RestoreWindow();
            }
            _tray.IsVisible = true;
            RebuildTrayMenu();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            _trayHost = false; // ripiego silenzioso: il desktop non supporta l'icona
        }
    }

    private void RebuildTrayMenu()
    {
        if (_tray is null || _viewModel is not { } vm)
            return;
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Apri RdpManager");
        open.Click += (_, _) => RestoreWindow();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        void Group(string title, IEnumerable<RdpConnection> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
                return;
            var sub = new NativeMenu();
            foreach (var c in list)
            {
                var item = new NativeMenuItem(c.DisplayName);
                var connection = c;
                item.Click += (_, _) => { RestoreWindow(); vm.Connect(connection); };
                sub.Add(item);
            }
            menu.Add(new NativeMenuItem(title) { Menu = sub });
        }
        Group("Sessioni aperte", vm.OpenConnections());
        Group("Preferiti", vm.Document.Favorites());
        Group("Recenti", vm.Document.Recent(10));
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Esci");
        exit.Click += (_, _) => { _exiting = true; _window?.Close(); };
        menu.Add(exit);
        _tray.Menu = menu;
    }

    private void RestoreWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _viewModel is not { } vm)
            return;
        // «Resta nell'area di notifica» solo se l'icona c'è davvero: altrimenti la finestra sparirebbe senza modo di riaprirla.
        if (vm.Document.Settings is { ShowTrayIcon: true, CloseToTray: true } && _trayHost && _tray is { IsVisible: true })
        {
            e.Cancel = true;
            _window?.Hide();
        }
    }

    private static WindowIcon CreateIcon()
    {
        var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            var pixels = new int[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    bool screen = x is >= 3 and < 29 && y is >= 5 and < 22;
                    bool stand = x is >= 11 and < 21 && y is >= 24 and < 27;
                    bool inner = x is >= 5 and < 27 && y is >= 7 and < 20;
                    pixels[y * 32 + x] = (screen && !inner) || stand ? unchecked((int)0xFF2563EB) : inner ? unchecked((int)0xFF93B4F5) : 0;
                }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        return new WindowIcon(bitmap);
    }

    // --- Scorciatoia globale (solo X11) ---

    private void SetupHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        var text = _viewModel?.Document.Settings.GlobalHotkey;
        if (string.IsNullOrWhiteSpace(text))
            return;
        var hotkey = new X11GlobalHotkey(a => Dispatcher.UIThread.Post(a), RestoreWindow);
        if (hotkey.TryStart(text, DisplayServer.Current))
            _hotkey = hotkey; // altrimenti (Wayland, combinazione già presa) niente: ripiego silenzioso
    }
}
