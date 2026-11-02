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
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "vpn");
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        _vpn = new VpnForm(new WireGuardNtTunnel(new ProcessElevator(exe), new ScQuery(), staging));
        _vpn.FormClosed += (_, _) => _vpn = null;
        _vpn.Show(this);
    }
}
