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
        _vpnDashboard = new VpnDashboardForm(() => _sync.SignedIn, async ct =>
            {
                var devices = await _sync.GetVpnDevicesAsync(ct);
                if (VpnFolder.MigrateLegacyNames(_doc, devices))
                    SaveConnections();
                return devices;
            }, OpenVpnDashboardPc,
            sendFile: (to, path, ct) => _sync.SendVpnFileAsync(to, path, ct),
            names: () => VpnFolder.CustomNames(_doc), rename: RenameVpnPc);
        _vpnDashboard.FormClosed += (_, _) => _vpnDashboard = null;
        _vpnDashboard.Show(this);
    }

    /// <summary>
    /// «Rinomina…» della Plancia: il nome va sulla connessione del PC nella cartella marshmelloVPN (sincronizzata), così si
    /// vede su tutti i PC, nell'elenco e nella barra della sessione.
    /// </summary>
    private bool RenameVpnPc(VpnDashboardRow row, string name)
    {
        if (!VpnFolder.Rename(_doc, row.Device, name, row.Address))
            return false;
        SaveConnections();
        RefreshTree();
        UpdateSessionTitles(); // le schede già aperte di quel PC prendono subito il nome nuovo
        return true;
    }

    /// <summary>Apre il desktop remoto verso l'IP VPN: usa la connessione salvata con quell'indirizzo, se c'è; altrimenti ne tiene una temporanea.</summary>
    private void OpenVpnDashboardPc(string name, string address)
    {
        // Prima la connessione della cartella marshmelloVPN (ha il nome scelto nella Plancia), poi un'altra verso lo stesso IP.
        var saved = _doc.Connections.FirstOrDefault(c => c.AutoVpnDevice is not null && string.Equals(c.Host, address, StringComparison.OrdinalIgnoreCase))
                    ?? _doc.Connections.FirstOrDefault(c => string.Equals(c.Host, address, StringComparison.OrdinalIgnoreCase));
        if (saved is null && !_vpnDashboardConnections.TryGetValue(address, out saved))
        {
            saved = new RdpConnection { Name = name, Host = address };
            _vpnDashboardConnections[address] = saved;
        }
        OpenSession(saved);
    }
}
