using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Voce «Plancia VPN…» del menu Strumenti (codice a parte per non toccare MainForm.Vpn.cs).</summary>
public sealed partial class MainForm
{
    private VpnDashboardForm? _vpnDashboard;
    private readonly Dictionary<string, RdpConnection> _vpnDashboardConnections = new(StringComparer.OrdinalIgnoreCase);

    internal void ShowVpnDashboard()
    {
        if (_vpnDashboard is { IsDisposed: false } open)
        {
            open.Activate();
            return;
        }
        _vpnDashboard = new VpnDashboardForm(() => _sync.SignedIn, ct => _sync.GetVpnDevicesAsync(ct), OpenVpnDashboardPc);
        _vpnDashboard.FormClosed += (_, _) => _vpnDashboard = null;
        _vpnDashboard.Show(this);
    }

    /// <summary>Apre il desktop remoto verso l'IP VPN: usa la connessione salvata con quell'indirizzo, se c'è; altrimenti ne tiene una temporanea.</summary>
    private void OpenVpnDashboardPc(string name, string address)
    {
        var saved = _doc.Connections.FirstOrDefault(c => string.Equals(c.Host, address, StringComparison.OrdinalIgnoreCase));
        if (saved is null && !_vpnDashboardConnections.TryGetValue(address, out saved))
        {
            saved = new RdpConnection { Name = name, Host = address };
            _vpnDashboardConnections[address] = saved;
        }
        OpenSession(saved);
    }
}
