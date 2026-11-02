using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Tunnel basato su wireguard-nt (tunnel.dll + wireguard.dll) eseguito come servizio Windows «WireGuardTunnel$RdpManager».
/// Accendere e spegnere richiedono l'amministratore: questa classe gira con i diritti normali e delega a una
/// seconda copia di RdpManager.exe avviata con UAC (<c>--vpn-helper</c>, vedi <see cref="VpnHelper"/>). Lo stato si legge senza elevazione.
/// </summary>
public sealed class WireGuardNtTunnel(IVpnElevator elevator, IVpnServiceQuery query, string stagingDirectory) : IWireGuardTunnel
{
    public const int ExitOk = 0;
    public const int ExitFailed = 2;
    public const int ExitBadArguments = 3;
    public const int ExitCancelled = 1223; // ERROR_CANCELLED: l'utente ha rifiutato la finestra UAC

    public static string ServiceName => "WireGuardTunnel$" + VpnDefaults.TunnelName;

    /// <summary>File temporaneo con la configurazione in chiaro, passato all'assistente.</summary>
    public static string StagingFile(string directory) => Path.Combine(directory, VpnDefaults.TunnelName + ".conf");

    public TunnelState Stato => query.Query(ServiceName);

    public void Accendi(WireGuardConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(config));
        if (Stato is TunnelState.Acceso or TunnelState.InAvvio)
            return;

        Directory.CreateDirectory(stagingDirectory);
        var staging = StagingFile(stagingDirectory);
        File.WriteAllText(staging, config.ToConf());
        try
        {
            Check(elevator.RunHelper([VpnHelper.Argument, "su", staging]), "accendere");
        }
        finally
        {
            TryDelete(staging); // l'assistente la sposta; se non l'ha fatto non deve restare la chiave in chiaro
        }
    }

    public void Spegni()
    {
        if (Stato == TunnelState.Spento)
            return;
        Check(elevator.RunHelper([VpnHelper.Argument, "giu"]), "spegnere");
    }

    private static void Check(int code, string action)
    {
        switch (code)
        {
            case ExitOk:
                return;
            case ExitCancelled:
                throw new VpnException($"Per {action} la VPN servono i diritti di amministratore: operazione annullata.");
            default:
                throw new VpnException($"Impossibile {action} la VPN (codice {code}). Controlla che tunnel.dll e wireguard.dll siano nella cartella del programma.");
        }
    }

    private static void TryDelete(string path)
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
