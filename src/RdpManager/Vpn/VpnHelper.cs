using System.Security.Cryptography;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Parte che gira elevata (<c>RdpManager.exe --vpn-helper su|giu|rimuovi [file.conf]</c>), l'unica che chiede UAC:
/// <list type="bullet">
/// <item><c>su</c>: sposta la configurazione in %ProgramData% (leggibile solo da SYSTEM e amministratori), registra il servizio
/// <c>RdpManager.exe --vpn-service</c> dando agli utenti il permesso di avviarlo e fermarlo, lo avvia e aspetta che sia attivo;</item>
/// <item><c>giu</c>: ferma il servizio (resta installato: la volta dopo si riaccende senza UAC);</item>
/// <item><c>rimuovi</c>: ferma e cancella il servizio e la configurazione.</item>
/// </list>
/// </summary>
public sealed class VpnHelper(IServiceManager services, string programDataDirectory, string exePath)
{
    public const string Argument = "--vpn-helper";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
    /// <summary>Restringe i permessi del file ai soli amministratori/SYSTEM (sostituibile nelle prove).</summary>
    public Action<string> Restrict { get; init; } = VpnFiles.RestrictToAdministrators;

    /// <summary>Prepara la cartella d'ingresso per i cambi di configurazione senza UAC (sostituibile nelle prove).</summary>
    public Action<string> PrepareInbox { get; init; } = VpnInbox.Prepare;

    public static bool IsHelperMode(string[] args) => args.Length > 0 && args[0].Equals(Argument, StringComparison.OrdinalIgnoreCase);

    /// <summary>SHA-256 (esadecimale) del testo della configurazione, per riconoscere se il servizio installato è ancora quello giusto.</summary>
    public static string HashConfig(string confText) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(confText)));

    public int Run(string[] args)
    {
        if (!IsHelperMode(args) || args.Length < 2)
            return WireGuardNtTunnel.ExitBadArguments;
        try
        {
            return args[1].ToLowerInvariant() switch
            {
                "su" when args.Length >= 3 => Up(args[2]),
                "giu" => Down(),
                "rimuovi" => Remove(),
                _ => WireGuardNtTunnel.ExitBadArguments,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        {
            AppLog.Default?.Error("VPN: errore nell'assistente elevato", ex);
            return WireGuardNtTunnel.ExitFailed;
        }
    }

    private int Up(string stagingFile)
    {
        var text = File.ReadAllText(stagingFile);
        var config = WireGuardConfig.Parse(text);
        if (!config.IsValid)
            return WireGuardNtTunnel.ExitFailed;

        Directory.CreateDirectory(VpnPaths.Directory(programDataDirectory));
        PrepareInbox(programDataDirectory);
        var target = VpnPaths.Conf(programDataDirectory, VpnDefaults.TunnelName);
        var hashFile = VpnPaths.InstalledHash(programDataDirectory);
        if (File.Exists(target))
            File.Delete(target);
        File.Delete(hashFile);
        File.Move(stagingFile, target); // la chiave in chiaro lascia la cartella dell'utente
        Restrict(target);

        var name = WireGuardNtTunnel.ServiceName;
        try
        {
            if (services.Exists(name))
                RemoveService(name); // ricreato ogni volta: il percorso dell'exe può essere cambiato con un aggiornamento
            services.Create(name, "RdpManager VPN (WireGuard)", $"\"{exePath}\" {VpnServiceArgs.Argument} {VpnDefaults.TunnelName}");
            services.GrantUserControl(name);
            services.Start(name);
            if (!WaitFor(name, TunnelState.Acceso))
                throw new InvalidOperationException("Il servizio della VPN non è partito.");
            File.WriteAllText(hashFile, HashConfig(text));
            return WireGuardNtTunnel.ExitOk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Default?.Error("VPN: accensione non riuscita", ex);
            TryRemoveAll(name);
            return WireGuardNtTunnel.ExitFailed;
        }
    }

    private int Down()
    {
        var name = WireGuardNtTunnel.ServiceName;
        if (services.Exists(name) && services.Query(name) != TunnelState.Spento)
        {
            services.Stop(name);
            WaitFor(name, TunnelState.Spento);
        }
        return WireGuardNtTunnel.ExitOk;
    }

    private int Remove()
    {
        var name = WireGuardNtTunnel.ServiceName;
        if (services.Exists(name))
            RemoveService(name);
        DeleteQuietly(VpnPaths.Conf(programDataDirectory, VpnDefaults.TunnelName));
        DeleteQuietly(VpnPaths.InstalledHash(programDataDirectory));
        return WireGuardNtTunnel.ExitOk;
    }

    private void TryRemoveAll(string name)
    {
        try
        {
            if (services.Exists(name))
                RemoveService(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Default?.Error("VPN: rimozione del servizio dopo un errore non riuscita", ex);
        }
        DeleteQuietly(VpnPaths.Conf(programDataDirectory, VpnDefaults.TunnelName));
        DeleteQuietly(VpnPaths.InstalledHash(programDataDirectory));
    }

    private void RemoveService(string name)
    {
        if (services.Query(name) != TunnelState.Spento)
        {
            services.Stop(name);
            WaitFor(name, TunnelState.Spento);
        }
        services.Delete(name);
    }

    private bool WaitFor(string name, TunnelState wanted)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var waited = TimeSpan.Zero; waited <= Timeout; waited += step)
        {
            var state = services.Query(name);
            if (state == wanted)
                return true;
            if (state == TunnelState.Errore)
                return false;
            Sleep(step);
        }
        return false;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
