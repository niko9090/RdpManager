using System.Net;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Vpn;
using RdpManagerVpn;

namespace RdpManager.Tests;

/// <summary>
/// Hairpin NAT: un PC nella LAN del server, col solo nome pubblico, non fa mai l'handshake (il router non rimanda in dentro
/// l'UDP verso il proprio IP pubblico). Il profilo porta anche l'endpoint LAN e il client sceglie quello giusto.
/// </summary>
public class VpnLanEndpointTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress[] OnLan = [IPAddress.Parse("192.168.1.50"), IPAddress.Parse("fe80::1")];
    private static readonly IPAddress[] Elsewhere = [IPAddress.Parse("10.0.5.8")];

    private static string ConfWithLan() => VpnClientProfileTests.Conf("LanEndpoint = 192.168.1.117:51820\nAllowedIPs = 10.66.0.0/24\n");

    [Fact]
    public void LanEndpoint_ConfSenzaLan_ResteCompatibile()
    {
        var r = ClientProfile.Check(VpnClientProfileTests.Conf());
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal("", r.Config!.LanEndpoint);
        Assert.DoesNotContain("LanEndpoint", r.Config.ToConf());
    }

    [Fact]
    public void LanEndpoint_ConfConLan_SiLeggeESiRiscrive()
    {
        var r = ClientProfile.Check(ConfWithLan());
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal("192.168.1.117:51820", r.Config!.LanEndpoint);
        Assert.Equal("203.0.113.5:51820", r.Config.Endpoint);
        Assert.Equal("192.168.1.117:51820", WireGuardConfig.Parse(r.Config.ToConf()).LanEndpoint);
        Assert.False(ClientProfile.Check(VpnClientProfileTests.Conf("LanEndpoint = senza-porta\n")).Ok);
    }

    [Fact]
    public void LanEndpoint_NellaLanDelServer_SiRiconosceDallIndirizzoLocale()
    {
        Assert.True(VpnEndpointChoice.OnServerLan("192.168.1.117:51820", OnLan));
        Assert.False(VpnEndpointChoice.OnServerLan("192.168.1.117:51820", Elsewhere));
        Assert.False(VpnEndpointChoice.OnServerLan("", OnLan));
        Assert.False(VpnEndpointChoice.OnServerLan("nas.local:51820", OnLan));
        Assert.Equal("192.168.1.117:51820", VpnEndpointChoice.Apply(ClientProfile.Check(ConfWithLan()).Config!, true).Endpoint);
        Assert.Equal("203.0.113.5:51820", VpnEndpointChoice.Apply(ClientProfile.Check(ConfWithLan()).Config!, false).Endpoint);
        Assert.Equal("203.0.113.5:51820", VpnEndpointChoice.Apply(ClientProfile.Check(VpnClientProfileTests.Conf()).Config!, true).Endpoint);
    }

    private sealed class Adapter(byte[]? config) : INtAdapter
    {
        public void SetConfiguration(byte[] block) { }
        public void AddAddress(IPAddress ip, int prefix) { }
        public void AddRoute(IPAddress network, int prefix) { }
        public void Up() { }
        public byte[]? GetConfiguration() => config;
        public void Dispose() { }
    }

    private sealed class Driver(Func<Adapter> make) : INtDriver
    {
        public INtAdapter Create(string name, string tunnelType) => make();
    }

    private sealed class Rig
    {
        public readonly List<string> Hosts = [];
        public DateTime Now = T0;
        public ClientSupervisor Sup;

        public Rig(string conf, IReadOnlyList<IPAddress> local)
        {
            var cfg = ClientProfile.Check(conf).Config!;
            var block = WireGuardNtStructs.BuildConfiguration(cfg, IPAddress.Loopback); // handshake 0: il server non risponde
            var runner = new VpnTunnelRunner(new Driver(() => new Adapter(block)), h => { Hosts.Add(h); return IPAddress.Loopback; });
            Sup = new ClientSupervisor(runner, () => ClientProfile.Check(conf), () => new ClientSettings(), () => Now, _ => { }, () => local);
        }

        public void Advance(TimeSpan d) => Now += d;
    }

    [Fact]
    public void LanEndpoint_Supervisore_NellaLanParteDaEndpointLan()
    {
        var rig = new Rig(ConfWithLan(), OnLan);
        rig.Sup.Tick();
        Assert.Equal(["192.168.1.117"], rig.Hosts);
    }

    [Fact]
    public void LanEndpoint_Supervisore_FuoriDallaLanParteDalNomePubblico_EDopoIlTempoProvaLan()
    {
        var rig = new Rig(ConfWithLan(), Elsewhere);
        rig.Sup.Tick();
        Assert.Equal(["203.0.113.5"], rig.Hosts);
        rig.Advance(TimeSpan.FromSeconds(10));
        rig.Sup.Tick();
        Assert.Single(rig.Hosts); // troppo presto per cambiare
        rig.Advance(VpnEndpointChoice.SwitchTimeout);
        rig.Sup.Tick();           // nessun handshake: si ferma per provare l'altro
        rig.Advance(TimeSpan.FromSeconds(2));
        rig.Sup.Tick();
        Assert.Equal(["203.0.113.5", "192.168.1.117"], rig.Hosts);
    }

    [Fact]
    public void LanEndpoint_Supervisore_SenzaEndpointLanNonCambiaEndpoint()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), OnLan);
        rig.Sup.Tick();
        rig.Advance(TimeSpan.FromSeconds(60));
        rig.Sup.Tick();
        Assert.Single(rig.Hosts);
    }

    [Fact]
    public void Grant_ConEndpointLan_SiSalvaNelProfilo_ESenzaResteVuoto()
    {
        var key = WireGuardKeys.Generate().PublicKey;
        var withLan = VpnGrant.Parse(JsonDocument.Parse(
            "{\"name\":\"PC\",\"address\":\"10.66.0.5/32\",\"serverPublicKey\":\"" + key + "\",\"endpoint\":\"vpn.example.com:51820\",\"lanEndpoint\":\"192.168.1.117:51820\"}").RootElement);
        Assert.Equal("192.168.1.117:51820", withLan.LanEndpoint);
        var profile = new VpnProfile();
        profile.GenerateKeys();
        VpnProvisioning.Apply(profile, withLan);
        Assert.Equal("192.168.1.117:51820", profile.ToConfig().LanEndpoint);

        var old = VpnGrant.Parse(JsonDocument.Parse(
            "{\"name\":\"PC\",\"address\":\"10.66.0.5/32\",\"serverPublicKey\":\"" + key + "\",\"endpoint\":\"vpn.example.com:51820\"}").RootElement);
        Assert.Equal("", old.LanEndpoint);
    }

    [Fact]
    public void Stato_SenzaHandshake_DiceNonCollegato_EVecchiFileDiStatoRestanoLeggibili()
    {
        var running = new VpnStatus(null, 100, 0, T0.AddSeconds(60)) { StartedUtc = T0 };
        Assert.Equal("Connessione in corso…", ClientStatusText.Describe(TunnelState.Acceso, running, T0.AddSeconds(10)));
        Assert.Equal("Non collegato: il server non risponde", ClientStatusText.Describe(TunnelState.Acceso, running, T0.AddSeconds(60)));
        Assert.True(running.ServerUnreachable(T0.AddSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
        Assert.False(new VpnStatus(T0, 1, 1, T0).ServerUnreachable(T0.AddMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));

        var roundTrip = VpnStatus.Parse(running.Format());
        Assert.Equal(T0, roundTrip!.StartedUtc);
        var old = VpnStatus.Parse("handshake=0\ntx=1\nrx=2\naggiornato=" + T0.ToFileTimeUtc() + "\n");
        Assert.NotNull(old);
        Assert.Null(old!.StartedUtc);
        Assert.False(old.ServerUnreachable(T0.AddHours(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5))); // senza avvio non si sa
    }

    [Fact]
    public void Panoramica_SchedaAccesaMaServerSenzaRisposta_NonCollegata()
    {
        var wg = new AdapterInfo("RdpManager", "RdpManager Tunnel", System.Net.NetworkInformation.NetworkInterfaceType.Unknown, IPAddress.Parse("10.66.0.2"), 32, NetworkKind.WireGuard);
        Assert.True(NetworkOverview.Build([wg]).VpnConnected);
        var bad = NetworkOverview.Build([wg], null, serverUnreachable: true);
        Assert.False(bad.VpnConnected);
        Assert.Contains("NON COLLEGATA", bad.Headline);
        Assert.Contains("non risponde", bad.Headline);
    }
}
