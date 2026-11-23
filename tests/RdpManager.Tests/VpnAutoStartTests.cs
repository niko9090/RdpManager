using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.Tests;

public class VpnAutoStartTests
{
    private sealed class FakeTunnel(bool declineElevation) : IWireGuardTunnel
    {
        public int Attempts;
        public TunnelState Stato { get; private set; } = TunnelState.Spento;

        public void Accendi(WireGuardConfig config)
        {
            Attempts++;
            if (declineElevation)
                throw new VpnElevationDeclinedException("annullato");
            Stato = TunnelState.Acceso;
        }

        public void Spegni() => Stato = TunnelState.Spento;
    }

    private sealed class FakeControl(bool installed) : IVpnServiceControl
    {
        public TunnelState Query(string serviceName) => TunnelState.Spento;
        public bool Exists(string serviceName) => installed;
        public void Start(string serviceName) { }
        public void Stop(string serviceName) { }
    }

    private static VpnProfile Profile()
    {
        var p = new VpnProfile { Address = "10.66.0.2/32", ServerPublicKey = Convert.ToBase64String(new byte[32]), PcName = "pc" };
        p.GenerateKeys();
        return p;
    }

    [Fact]
    public void Declined_IsRememberedAndNextStartDoesNotAskAgain()
    {
        var profile = Profile();
        var saves = 0;
        var tunnel = new FakeTunnel(declineElevation: true);

        var first = VpnAutoStart.Run(profile, tunnel, new FakeControl(false), _ => saves++);
        Assert.Equal(VpnAutoStart.NeedsAdminMessage, first);
        Assert.True(profile.AutoConnectSkipsInstall);
        Assert.Equal(1, saves);
        Assert.Equal(1, tunnel.Attempts);

        var second = VpnAutoStart.Run(profile, tunnel, new FakeControl(false), _ => saves++);
        Assert.Equal(VpnAutoStart.NeedsAdminMessage, second);
        Assert.Equal(1, tunnel.Attempts); // nessuna nuova richiesta
    }

    [Fact]
    public void ManualSuccess_ClearsTheMarkAndAutoConnectIsNormalAgain()
    {
        var profile = Profile();
        profile.AutoConnectSkipsInstall = true;
        var saves = 0;

        VpnAutoStart.ManualConnectSucceeded(profile, _ => saves++);
        Assert.False(profile.AutoConnectSkipsInstall);
        Assert.Equal(1, saves);

        var tunnel = new FakeTunnel(declineElevation: false);
        Assert.Equal("VPN collegata.", VpnAutoStart.Run(profile, tunnel, new FakeControl(false), _ => { }));
        Assert.Equal(1, tunnel.Attempts);
    }

    [Fact]
    public void ServiceInstalled_NothingChanges()
    {
        var profile = Profile();
        var saves = 0;
        var tunnel = new FakeTunnel(declineElevation: false);

        Assert.Equal("VPN collegata.", VpnAutoStart.Run(profile, tunnel, new FakeControl(true), _ => saves++));
        Assert.False(profile.AutoConnectSkipsInstall);
        Assert.Equal(0, saves);

        // anche con il segno rimasto da prima, col servizio installato si accende normalmente
        profile.AutoConnectSkipsInstall = true;
        var tunnel2 = new FakeTunnel(declineElevation: false);
        Assert.Equal("VPN collegata.", VpnAutoStart.Run(profile, tunnel2, new FakeControl(true), _ => saves++));
        Assert.Equal(1, tunnel2.Attempts);

        // un rifiuto con servizio installato non mette il segno
        var declined = Profile();
        Assert.Throws<VpnElevationDeclinedException>(() => VpnAutoStart.Run(declined, new FakeTunnel(true), new FakeControl(true), _ => saves++));
        Assert.False(declined.AutoConnectSkipsInstall);
    }
}
