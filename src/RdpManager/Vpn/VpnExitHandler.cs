using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Cosa fare della VPN quando RdpManager esce davvero. La VPN integrata (servizio «RdpManagerVpn») si spegne; se è
/// installata marshmelloVPN col suo programma (servizio «RdpManagerVpnClient», dal suo installer) non si tocca: la gestisce lei.
/// </summary>
public sealed class VpnExitHandler(IWireGuardTunnel tunnel, IVpnServiceControl control, Action<string>? log = null)
{
    /// <summary>Nome del servizio che registra l'installer di marshmelloVPN (src\RdpManagerVpn, ClientPaths.ServiceName).</summary>
    public const string ClientServiceName = "RdpManagerVpnClient";

    /// <summary>Vero se marshmelloVPN è installata col suo programma.</summary>
    public bool MarshmelloVpnInstalled()
    {
        try
        {
            return control.Exists(ClientServiceName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Nel dubbio la VPN non si spegne: meglio lasciarla accesa che toglierla a chi la usa.
            log?.Invoke("Non riesco a capire se marshmelloVPN è installata: " + ex.Message);
            return true;
        }
    }

    /// <summary>Alla chiusura vera: spegne la VPN integrata se serve. Restituisce vero se l'ha spenta.</summary>
    public bool OnExit(bool windowsEnding)
    {
        if (!ClosePolicy.ShouldStopVpnOnExit(MarshmelloVpnInstalled(), windowsEnding))
            return false;
        try
        {
            if (tunnel.Stato is TunnelState.Spento or TunnelState.InArresto)
                return false;
            tunnel.Spegni();
            log?.Invoke("VPN integrata spenta all'uscita");
            return true;
        }
        catch (Exception ex) when (ex is VpnException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke("Non riesco a spegnere la VPN all'uscita: " + ex.Message);
            return false;
        }
    }
}
