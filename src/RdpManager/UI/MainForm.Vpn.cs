using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.UI;

/// <summary>Voce «VPN (WireGuard)…» del menu Strumenti.</summary>
public sealed partial class MainForm
{
    private VpnForm? _vpn;

    internal void ShowVpn()
    {
        if (_vpn is { IsDisposed: false } open)
        {
            open.Activate();
            return;
        }
        _vpn = new VpnForm(CreateTunnel());
        _vpn.FormClosed += (_, _) => _vpn = null;
        _vpn.Show(this);
    }

    private static WireGuardNtTunnel CreateTunnel()
    {
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "vpn");
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RdpManager");
        return new WireGuardNtTunnel(new ProcessElevator(exe), new ScControl(), staging,
            VpnPaths.InstalledHash(programData), VpnPaths.Status(programData),
            VpnPaths.InboxConf(programData, VpnDefaults.TunnelName));
    }

    /// <summary>
    /// VPN automatica: se questo PC non ha ancora un profilo e si è nell'account, lo ottiene dal server (chiavi create qui,
    /// al server va solo la pubblica) e accende il tunnel. Restituisce una riga di stato per chi l'ha chiamata ("" = niente da fare).
    /// </summary>
    internal async Task<string> EnsureVpnAsync()
    {
        try
        {
            if (IsDisposed || !_sync.SignedIn || _shutdownCts.IsCancellationRequested || !VpnProvisioning.NeedsProvisioning(VpnProfile.Load()))
                return "";
            var profile = await VpnProvisioning.EnsureAsync(_sync, ct: _shutdownCts.Token);
            if (profile is null)
                return "";
            AppLog.Default?.Info("VPN: profilo ottenuto dal server, indirizzo " + profile.Address);
            if (!Vpn.WireGuardNative.DllPresent)
                return "VPN configurata. Manca wireguard.dll accanto al programma: la VPN non si può accendere.";
            var config = profile.ToConfig();
            await Task.Run(() => CreateTunnel().Accendi(config));
            return "VPN configurata e accesa (indirizzo " + profile.Address + ").";
        }
        catch (SyncServerException ex) when (ex.Code == "vpn_disabled")
        {
            return ""; // il server non offre la VPN automatica: niente avviso a ogni avvio
        }
        catch (OperationCanceledException) when (IsDisposed || _shutdownCts.IsCancellationRequested)
        {
            return ""; // il programma si sta chiudendo: non è un errore della VPN
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or Vpn.VpnException
                                       or ArgumentException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            AppLog.Default?.Info("VPN automatica non riuscita: " + ex.Message);
            return "VPN non configurata: " + ex.Message;
        }
    }
}
