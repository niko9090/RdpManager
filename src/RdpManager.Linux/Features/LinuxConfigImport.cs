using RdpManager.Core;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Features;

/// <summary>
/// Import del file di configurazione su Linux: la logica è quella del Core (<see cref="ConfigFileImporter"/>), qui si collega
/// l'accensione del tunnel a <see cref="LinuxWireGuard.EnsureConnectedAsync"/> (NetworkManager o wg-quick con pkexec).
/// </summary>
public static class LinuxConfigImport
{
    /// <summary>Importa il file e restituisce il riepilogo («Fatto» o «Da completare») oppure il motivo per cui non è valido.</summary>
    public static async Task<string> ImportAsync(string path, SyncService sync, LinuxWireGuard vpn, string? profilePath = null,
        string? dataFolder = null, CancellationToken ct = default)
    {
        try
        {
            var report = await ConfigFileImporter.ImportFileAsync(path, sync,
                async (config, token) =>
                {
                    var result = await vpn.EnsureConnectedAsync(config, token);
                    return result.Success ? null : result.Message;
                },
                new ConfigImportOptions { ProfilePath = profilePath, DataFolder = dataFolder }, ct);
            AppLog.Default?.Info("File di configurazione importato: " + (report.Complete ? "completo" : "da completare"));
            return report.Text;
        }
        catch (ConfigFileException ex)
        {
            return "Il file di configurazione non è valido.\n\n" + ex.Message;
        }
        catch (FileNotFoundException ex)
        {
            return ex.Message;
        }
    }
}
