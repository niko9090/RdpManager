using System.ComponentModel;

namespace RdpManager.Vpn;

/// <summary>
/// Ferma il servizio della VPN prima di un aggiornamento: il servizio è <c>RdpManager.exe --vpn-service</c> dalla cartella
/// installata, quindi finché gira l'installer non può sostituire RdpManager.exe e le librerie (file in uso), l'installazione
/// si annulla e il programma di aggiornamento rimette la versione di prima. Fermarlo non chiede UAC: il servizio dà agli
/// utenti il permesso di avviarlo e fermarlo.
/// </summary>
public static class VpnServiceStopper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Vero se alla fine il servizio non c'è o è fermo.</summary>
    public static bool StopIfRunning(IVpnServiceControl services, Action<string> log, TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var name = WireGuardNtTunnel.ServiceName;
        try
        {
            if (!services.Exists(name) || services.Query(name) == TunnelState.Spento)
                return true;
            log("Fermo il servizio della VPN (usa i file da aggiornare)");
            if (services.Query(name) != TunnelState.InArresto)
                services.Stop(name);
            var step = TimeSpan.FromMilliseconds(250);
            for (var waited = TimeSpan.Zero; waited <= timeout; waited += step)
            {
                if (services.Query(name) == TunnelState.Spento)
                {
                    log("Servizio della VPN fermato");
                    return true;
                }
                sleep(step);
            }
            log("Il servizio della VPN non si è fermato in " + timeout.TotalSeconds.ToString("0") + " secondi");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            log("Non riesco a fermare il servizio della VPN: " + ex.Message);
            return false;
        }
    }

    /// <summary>Con il gestore dei servizi di Windows (usato dal programma di aggiornamento vero).</summary>
    public static bool StopSystemService(Action<string> log) =>
        OperatingSystem.IsWindows() && StopIfRunning(new ScServiceManager(), log, DefaultTimeout);
}
