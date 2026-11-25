using System.Buffers.Binary;
using System.Net;
using RdpManager.Core;
using RdpManager.Vpn;
using RdpManagerVpn;

namespace RdpManager.Tests;

/// <summary>
/// Servizio VPN di RdpManager: un tunnel che ha già risposto e poi smette (router riavviato, hairpin NAT inceppato) va
/// ricreato, con nome risolto di nuovo e porta nuova. Prima restava morto per sempre (caso reale: PC-FISSO, 25/11/2026).
/// </summary>
public class VpnServiceLoopTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress[] OnLan = [IPAddress.Parse("192.168.1.50")];
    private static readonly IPAddress[] Elsewhere = [IPAddress.Parse("10.0.5.8")];

    private sealed class Adapter(Func<byte[]?> config) : INtAdapter
    {
        public void SetConfiguration(byte[] block) { }
        public void AddAddress(IPAddress ip, int prefix) { }
        public void AddRoute(IPAddress network, int prefix) { }
        public void Up() { }
        public byte[]? GetConfiguration() => config();
        public void Dispose() { }
    }

    private sealed class Driver(Func<INtAdapter> make) : INtDriver
    {
        public INtAdapter Create(string name, string tunnelType) => make();
    }

    private sealed class Rig
    {
        public readonly List<string> Hosts = [];
        public DateTime Now = T0;
        public DateTime? Handshake;      // ultimo handshake che il driver finto riporta (null = mai)
        public bool ResolveFails;
        public readonly VpnServiceLoop Loop;
        private readonly byte[] _block;

        public Rig(string conf, IReadOnlyList<IPAddress> local)
        {
            var cfg = ClientProfile.Check(conf).Config!;
            _block = WireGuardNtStructs.BuildConfiguration(cfg, IPAddress.Loopback);
            var runner = new VpnTunnelRunner(new Driver(() => { Handshake = null; return new Adapter(Block); }), h =>
            {
                if (ResolveFails)
                    throw new InvalidOperationException("Impossibile risolvere " + h);
                Hosts.Add(h);
                return IPAddress.Loopback;
            }, () => Now);
            Loop = new VpnServiceLoop(runner, () => cfg, "RdpManager", () => Now, () => local);
        }

        private byte[] Block()
        {
            var copy = (byte[])_block.Clone();
            long ft = Handshake?.ToFileTimeUtc() ?? 0;
            BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(WireGuardNtStructs.InterfaceSize + WireGuardNtStructs.PeerHandshake), ft);
            return copy;
        }

        public void Run(TimeSpan d)
        {
            var end = Now + d;
            while (Now < end)
            {
                Now += TimeSpan.FromSeconds(2);
                Loop.Tick();
            }
        }
    }

    private static string ConfWithLan() => VpnClientProfileTests.Conf("LanEndpoint = 192.168.1.117:51820\nAllowedIPs = 10.66.0.0/24\n");

    [Fact]
    public void TunnelCheRispondeva_ESmette_VieneRicreato_ConNomeRisoltoDiNuovo()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), Elsewhere);
        rig.Loop.Tick();
        Assert.Single(rig.Hosts);

        rig.Handshake = rig.Now;           // il server risponde
        rig.Run(TimeSpan.FromMinutes(3));  // poi più niente: entro 200 s dall'ultimo handshake si aspetta
        Assert.Equal(0, rig.Loop.Restarts);

        rig.Run(TimeSpan.FromSeconds(30)); // oltre 200 s: tunnel morto, si ricrea
        Assert.Equal(1, rig.Loop.Restarts);
        Assert.Equal(2, rig.Hosts.Count);
    }

    [Fact]
    public void TunnelVivo_NonSiTocca()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), Elsewhere);
        rig.Loop.Tick();
        for (int i = 0; i < 10; i++)
        {
            rig.Handshake = rig.Now;       // handshake rinnovato ogni 2 minuti
            rig.Run(TimeSpan.FromMinutes(2));
        }
        Assert.Equal(0, rig.Loop.Restarts);
        Assert.Single(rig.Hosts);
    }

    [Fact]
    public void SenzaEndpointLan_MaiRisposto_SiRicreaDopoDueMinuti()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), OnLan);
        rig.Loop.Tick();
        rig.Run(TimeSpan.FromSeconds(100));
        Assert.Equal(0, rig.Loop.Restarts);
        rig.Run(TimeSpan.FromSeconds(30));
        Assert.Equal(1, rig.Loop.Restarts);
    }

    [Fact]
    public void ConEndpointLan_NellaLanParteDallaLan_ESeCadeSceglieDiNuovo()
    {
        var rig = new Rig(ConfWithLan(), OnLan);
        rig.Loop.Tick();
        Assert.Equal(["192.168.1.117"], rig.Hosts);
        rig.Handshake = rig.Now;
        rig.Run(TimeSpan.FromSeconds(210)); // cade
        Assert.Equal(1, rig.Loop.Restarts);
        Assert.Equal("192.168.1.117", rig.Hosts[^1]); // sempre nella LAN del server: di nuovo l'endpoint LAN
    }

    [Fact]
    public void ConEndpointLan_SenzaRisposta_ProvaLAltroEndpoint()
    {
        var rig = new Rig(ConfWithLan(), Elsewhere);
        rig.Loop.Tick();
        rig.Run(VpnEndpointChoice.SwitchTimeout + TimeSpan.FromSeconds(4));
        Assert.Equal(["203.0.113.5", "192.168.1.117"], rig.Hosts);
    }

    [Fact]
    public void AccensioneFallita_NonFermaIlServizio_ERiprova()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), Elsewhere) { ResolveFails = true };
        rig.Loop.Tick();                   // rete non pronta: nome non risolto
        Assert.Empty(rig.Hosts);
        rig.ResolveFails = false;
        rig.Run(VpnServiceLoop.RetryDelay + TimeSpan.FromSeconds(2));
        Assert.Single(rig.Hosts);
    }

    [Fact]
    public void Stato_TieneLaPrimaAccensione_AncheDopoIlRiavvioDelTunnel()
    {
        var rig = new Rig(VpnClientProfileTests.Conf(), Elsewhere);
        rig.Loop.Tick();
        var started = rig.Now;
        rig.Handshake = rig.Now;
        rig.Run(TimeSpan.FromMinutes(4));
        rig.Loop.Tick();
        Assert.Equal(1, rig.Loop.Restarts);
        Assert.Equal(started, rig.Loop.LastStatus?.StartedUtc);
    }
}
