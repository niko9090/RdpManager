using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Parte che gira elevata (<c>RdpManager.exe --vpn-helper su|giu [file.conf]</c>): sposta la configurazione in
/// %ProgramData% (leggibile solo da SYSTEM e amministratori), registra il servizio del tunnel, lo avvia e aspetta che sia attivo;
/// per «giu» lo ferma e lo rimuove. Il servizio stesso è RdpManager.exe <c>--wg-service</c> che chiama tunnel.dll.
/// </summary>
public sealed class VpnHelper(IServiceManager services, string programDataDirectory, string exePath)
{
    public const string Argument = "--vpn-helper";
    public const string ServiceArgument = "--wg-service";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
    /// <summary>Restringe i permessi del file ai soli amministratori/SYSTEM (sostituibile nelle prove).</summary>
    public Action<string> Restrict { get; init; } = VpnFiles.RestrictToAdministrators;

    public static bool IsHelperMode(string[] args) => args.Length > 0 && args[0].Equals(Argument, StringComparison.OrdinalIgnoreCase);

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
        var config = WireGuardConfig.Parse(File.ReadAllText(stagingFile));
        if (!config.IsValid)
            return WireGuardNtTunnel.ExitFailed;

        var dir = Path.Combine(programDataDirectory, "vpn");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, VpnDefaults.TunnelName + ".conf");
        if (File.Exists(target))
            File.Delete(target);
        File.Move(stagingFile, target); // la chiave in chiaro lascia la cartella dell'utente
        Restrict(target);

        var name = WireGuardNtTunnel.ServiceName;
        if (services.Exists(name))
            RemoveService(name);
        try
        {
            services.Create(name, "RdpManager VPN (WireGuard)", $"\"{exePath}\" {ServiceArgument} \"{target}\"");
            services.Start(name);
            if (!WaitFor(name, TunnelState.Acceso))
            {
                RemoveService(name);
                return WireGuardNtTunnel.ExitFailed;
            }
            return WireGuardNtTunnel.ExitOk;
        }
        finally
        {
            DeleteQuietly(target); // il tunnel ha già letto la configurazione
        }
    }

    private int Down()
    {
        var name = WireGuardNtTunnel.ServiceName;
        if (services.Exists(name))
            RemoveService(name);
        return WireGuardNtTunnel.ExitOk;
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
