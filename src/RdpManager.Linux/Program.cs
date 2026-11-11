using Avalonia;
using RdpManager.Linux.Platform;

namespace RdpManager.Linux;

internal static class Program
{
    /// <summary>File di configurazione passato come argomento (null se non c'è): lo prende <see cref="App"/> all'avvio.</summary>
    internal static string? StartupConfigFile { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        LinuxPlatformInit.Register();
        // «rdpmanager file.rdpmconfig» (anche dal gestore di file, via MimeType del .desktop): si importa all'avvio.
        StartupConfigFile = RdpManager.Core.RdpmConfigFile.FindArgument(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
