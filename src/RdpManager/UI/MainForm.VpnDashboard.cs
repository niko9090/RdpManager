using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Menu «Plancia» (prima di Aiuto) (codice a parte per non toccare MainForm.Vpn.cs).</summary>
public sealed partial class MainForm
{
    private VpnDashboardForm? _vpnDashboard;
    private readonly Dictionary<string, RdpConnection> _vpnDashboardConnections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ruolo «Gestore VPN» (impostato da Program): appena la finestra è visibile si apre la plancia, in primo piano.</summary>
    internal bool OpenVpnDashboardOnStart { get; set; }

    private void RunRoleStartup()
    {
        if (!OpenVpnDashboardOnStart)
            return;
        OpenVpnDashboardOnStart = false;
        ShowVpnDashboard();
    }

    internal void ShowVpnDashboard()
    {
        if (_vpnDashboard is { IsDisposed: false } open)
        {
            open.Activate();
            return;
        }
        _vpnDashboard = new VpnDashboardForm(() => _sync.SignedIn, ct => _sync.GetVpnDevicesAsync(ct), OpenVpnDashboardPc,
            sendFile: (to, path, ct) => _sync.SendVpnFileAsync(to, path, ct),
            names: _doc.Settings.VpnNames, saveNames: SaveConnections);
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
