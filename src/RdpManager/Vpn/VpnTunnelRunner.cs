using System.Net;
using System.Net.Sockets;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// La logica del tunnel dentro il servizio: crea l'adattatore, gli dà la configurazione, indirizzo e rotte, lo accende,
/// e ne rilegge lo stato. Separata dall'API nativa (<see cref="INtDriver"/>) per poterla provare con una finta.
/// </summary>
public sealed class VpnTunnelRunner(INtDriver driver, Func<string, IPAddress>? resolve = null, Func<DateTime>? now = null) : IDisposable
{
    private readonly Func<string, IPAddress> _resolve = resolve ?? ResolveHost;
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private INtAdapter? _adapter;

    public bool IsRunning => _adapter is not null;

    public void Start(WireGuardConfig config, string adapterName)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(config));
        if (_adapter is not null)
            return;

        WireGuardConfig.TryParseEndpoint(config.Endpoint, out var host, out _);
        var endpoint = _resolve(host);
        var block = WireGuardNtStructs.BuildConfiguration(config, endpoint);

        var adapter = driver.Create(adapterName, VpnDefaults.TunnelName);
        try
        {
            adapter.SetConfiguration(block);
            WireGuardConfig.TryParseCidr(config.Address, out var ip, out var prefix);
            adapter.AddAddress(IPAddress.Parse(ip), prefix);
            foreach (var a in config.AllowedIPs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                WireGuardConfig.TryParseCidr(a, out var net, out var len);
                adapter.AddRoute(IPAddress.Parse(net), len);
            }
            adapter.Up();
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
        _adapter = adapter;
    }

    /// <summary>Stato attuale riletto dal driver (null se spento o non leggibile).</summary>
    public VpnStatus? ReadStatus()
    {
        var block = _adapter?.GetConfiguration();
        return block is null ? null : WireGuardNtStructs.ParseStatus(block, _now());
    }

    public void Stop()
    {
        _adapter?.Dispose();
        _adapter = null;
    }

    public void Dispose() => Stop();

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var found = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
                if (found.Length > 0)
                    return found[0];
            }
            catch (SocketException ex)
            {
                last = ex;
            }
            Thread.Sleep(1000);
        }
        throw new InvalidOperationException($"Impossibile risolvere {host}.", last);
    }
}
