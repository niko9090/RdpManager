using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Tunnel basato su wireguard-nt (wireguard.dll ufficiale) eseguito come servizio Windows «RdpManagerVpn», cioè
/// <c>RdpManager.exe --vpn-service</c>. Il servizio si installa con UAC una volta sola (<c>--vpn-helper su</c>, vedi
/// <see cref="VpnHelper"/>) e dà agli utenti il permesso di avviarlo e fermarlo: le volte dopo, con la stessa configurazione,
/// accendere e spegnere non chiedono più nulla. Con una configurazione diversa o senza servizio si passa di nuovo dall'assistente.
/// </summary>
/// <param name="installedHashFile">File con l'SHA-256 della configurazione installata (<see cref="VpnPaths.InstalledHash"/>); null = passa sempre dall'assistente.</param>
/// <param name="statusFile">File di stato scritto dal servizio (<see cref="VpnPaths.Status"/>); null = nessun dettaglio.</param>
public sealed class WireGuardNtTunnel(IVpnElevator elevator, IVpnServiceControl control, string stagingDirectory,
    string? installedHashFile = null, string? statusFile = null) : IWireGuardTunnel
{
    public const int ExitOk = 0;
    public const int ExitFailed = 2;
    public const int ExitBadArguments = 3;
    public const int ExitCancelled = 1223; // ERROR_CANCELLED: l'utente ha rifiutato la finestra UAC

    public const string ServiceName = "RdpManagerVpn";

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>File temporaneo con la configurazione in chiaro, passato all'assistente.</summary>
    public static string StagingFile(string directory) => Path.Combine(directory, VpnDefaults.TunnelName + ".conf");

    public TunnelState Stato => control.Query(ServiceName);

    public VpnStatus? Dettagli
    {
        get
        {
            if (statusFile is null || Stato != TunnelState.Acceso)
                return null;
            try
            {
                return VpnStatus.Parse(File.ReadAllText(statusFile));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Accendi(WireGuardConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(config));
        if (Stato is TunnelState.Acceso or TunnelState.InAvvio)
            return;

        var text = config.ToConf();
        if (IsInstalledWith(text) && TryStartDirectly())
            return;

        Directory.CreateDirectory(stagingDirectory);
        var staging = StagingFile(stagingDirectory);
        File.WriteAllText(staging, text);
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
        try
        {
            control.Stop(ServiceName); // permesso dato agli utenti all'installazione
            if (WaitFor(TunnelState.Spento))
                return;
        }
        catch (InvalidOperationException)
        {
            // niente permesso (servizio di una versione precedente): si passa dall'assistente
        }
        Check(elevator.RunHelper([VpnHelper.Argument, "giu"]), "spegnere");
    }

    /// <summary>Toglie il servizio e la configurazione (con UAC).</summary>
    public void Rimuovi() => Check(elevator.RunHelper([VpnHelper.Argument, "rimuovi"]), "rimuovere");

    private bool IsInstalledWith(string confText)
    {
        if (installedHashFile is null || !control.Exists(ServiceName))
            return false;
        try
        {
            return File.ReadAllText(installedHashFile).Trim().Equals(VpnHelper.HashConfig(confText), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryStartDirectly()
    {
        try
        {
            control.Start(ServiceName);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return WaitFor(TunnelState.Acceso);
    }

    private bool WaitFor(TunnelState wanted)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var waited = TimeSpan.Zero; waited <= StartTimeout; waited += step)
        {
            var state = Stato;
            if (state == wanted)
                return true;
            if (state == TunnelState.Errore)
                return false;
            Sleep(step);
        }
        return false;
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
                throw new VpnException($"Impossibile {action} la VPN (codice {code}). Controlla che wireguard.dll sia nella cartella del programma e che il server sia raggiungibile.");
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
