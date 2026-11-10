using RdpManager.Core;

namespace RdpManager.Linux.Services;

/// <summary>Registro e registro dei guasti (#57): come in Windows, si accendono all'avvio e seguono l'impostazione «log dettagliato».</summary>
public static class LogSetup
{
    /// <summary>Crea (se mancano) <see cref="AppLog.Default"/> e <see cref="CrashLog.Default"/> nella cartella dei log e li collega.</summary>
    public static void Initialize(AppSettings settings, string? directory = null)
    {
        directory ??= AppLog.DefaultDirectory;
        if (AppLog.Default is null)
        {
            AppLog.Default = new AppLog(directory);
            AppLog.Default.Info($"Avvio di RdpManager {AppInfo.VersionText} (Linux)");
        }
        if (CrashLog.Default is null)
        {
            var crash = CrashLog.Default = new CrashLog(directory);
            crash.Attach();
        }
        ApplySettings(settings);
    }

    /// <summary>Applica «Registro dettagliato» al registro già acceso.</summary>
    public static void ApplySettings(AppSettings settings)
    {
        if (AppLog.Default is { } log)
            log.Verbose = settings.VerboseLog;
    }
}
