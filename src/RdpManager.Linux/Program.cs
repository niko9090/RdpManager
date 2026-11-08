using Avalonia;
using RdpManager.Linux.Platform;

namespace RdpManager.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LinuxPlatformInit.Register();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
