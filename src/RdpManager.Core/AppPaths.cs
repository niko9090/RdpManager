namespace RdpManager.Core;

/// <summary>
/// Cartelle dell'app. Su Windows restano %APPDATA%\RdpManager e %LOCALAPPDATA%\RdpManager (come sempre);
/// altrove seguono XDG: configurazione in $XDG_CONFIG_HOME (o ~/.config), dati in $XDG_DATA_HOME (o ~/.local/share).
/// Le funzioni con parametri servono alle prove (sistema e variabili d'ambiente finti).
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "RdpManager";

    /// <summary>Configurazione e archivio (Windows: %APPDATA%\RdpManager; Linux: ~/.config/RdpManager).</summary>
    public static string ConfigFolder => ResolveConfig(
        OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Dati locali: log, aggiornamenti, riserva cifrata (Windows: %LOCALAPPDATA%\RdpManager; Linux: ~/.local/share/RdpManager).</summary>
    public static string DataFolder => ResolveData(
        OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string ResolveConfig(bool windows, Func<string, string?> env, string windowsBase) =>
        windows ? Path.Combine(windowsBase, AppFolderName)
                : Path.Combine(XdgBase(env, "XDG_CONFIG_HOME", ".config"), AppFolderName);

    public static string ResolveData(bool windows, Func<string, string?> env, string windowsBase) =>
        windows ? Path.Combine(windowsBase, AppFolderName)
                : Path.Combine(XdgBase(env, "XDG_DATA_HOME", Path.Combine(".local", "share")), AppFolderName);

    // Da specifica XDG, un valore vuoto o non assoluto si ignora.
    private static string XdgBase(Func<string, string?> env, string variable, string homeRelative)
    {
        var value = env(variable);
        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
            return value;
        var home = env("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, homeRelative);
    }
}
