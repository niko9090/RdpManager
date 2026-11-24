using RdpManager.Core;
using RdpManager.Platform.Linux;
using Xunit;

namespace RdpManager.Linux.Tests;

/// <summary>k-177: tunnel su ma senza handshake → una sola ri-registrazione della chiave pubblica attuale.</summary>
public class VpnKeyRecoveryTests
{
    private sealed class Harness
    {
        public VpnProfile Profile { get; } = MakeProfile();
        public bool Up { get; set; } = true;
        public string HandshakeOutput { get; set; } = "";
        public bool HandshakeReadable { get; set; } = true;
        public DateTime Now { get; set; } = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        public bool SignedIn { get; set; } = true;
        public List<(string Name, string Key)> Registered { get; } = [];
        public LinuxVpnKeyRecovery Recovery { get; }

        public Harness(bool registered = true)
        {
            if (!registered)
                Profile.Address = "";
            var runner = new RecordingRunner((file, args) =>
            {
                if (file == "ip") return new ProcessResult(Up ? 0 : 1, "", "");
                if (file == "wg") return HandshakeReadable ? new ProcessResult(0, HandshakeOutput, "") : new ProcessResult(1, "", "denied");
                return new ProcessResult(0, "", "");
            });
            var vpn = new LinuxWireGuard(runner, Path.Combine(Path.GetTempPath(), "rdpm-rec-" + Guid.NewGuid().ToString("N") + ".conf"), LinuxVpnBackend.WgQuick);
            Recovery = new LinuxVpnKeyRecovery(vpn, () => Profile, () => SignedIn, (name, key, _) =>
            {
                Registered.Add((name, key));
                return Task.FromResult(new VpnGrant("pc", Profile.Address, Profile.ServerPublicKey, Profile.Endpoint, VpnDefaults.Network, "", 25));
            }, () => Now);
        }

        public async Task<string> TickAfter(TimeSpan wait)
        {
            await Recovery.TickAsync();
            Now += wait;
            return await Recovery.TickAsync();
        }
    }

    private static VpnProfile MakeProfile()
    {
        var p = new VpnProfile { Address = "10.66.0.7/32", Endpoint = "vpn.example.org:51820", PcName = "pc-test" };
        p.GenerateKeys();
        p.ServerPublicKey = WireGuardKeys.Generate().Item2;
        return p;
    }

    private static readonly TimeSpan Past = LinuxVpnKeyRecovery.Grace + TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Ri_registra_una_volta_la_chiave_attuale_dopo_la_pazienza()
    {
        var h = new Harness { HandshakeOutput = "AAAA=\t0\n" };
        var before = h.Profile.PrivateKeyProtected;
        Assert.Equal("", await h.Recovery.TickAsync()); // appena acceso: si aspetta
        Assert.Empty(h.Registered);
        h.Now += Past;
        var line = await h.Recovery.TickAsync();
        Assert.Contains("registrata di nuovo", line);
        Assert.Single(h.Registered);
        Assert.Equal(h.Profile.PublicKey, h.Registered[0].Key);
        Assert.Equal(before, h.Profile.PrivateKeyProtected); // nessuna chiave nuova
        Assert.DoesNotContain(h.Profile.PublicKey!, line);
    }

    [Fact]
    public async Task Non_ripete_il_tentativo()
    {
        var h = new Harness { HandshakeOutput = "AAAA=\t0\n" };
        await h.TickAfter(Past);
        for (var i = 0; i < 5; i++)
        {
            h.Now += TimeSpan.FromMinutes(1);
            Assert.Equal("", await h.Recovery.TickAsync());
        }
        Assert.Single(h.Registered);
        Assert.Equal(1, h.Recovery.Attempts);
    }

    [Fact]
    public async Task Niente_senza_account()
    {
        var h = new Harness { HandshakeOutput = "AAAA=\t0\n", SignedIn = false };
        Assert.Equal("", await h.TickAfter(Past));
        Assert.Empty(h.Registered);
        Assert.Equal(0, h.Recovery.Attempts);
    }

    [Fact]
    public async Task Niente_senza_registrazione()
    {
        var h = new Harness(registered: false) { HandshakeOutput = "AAAA=\t0\n" };
        Assert.Equal("", await h.TickAfter(Past));
        Assert.Empty(h.Registered);
    }

    [Fact]
    public async Task Niente_se_il_tunnel_e_giu_o_c_e_un_handshake_recente_o_non_si_legge()
    {
        var down = new Harness { HandshakeOutput = "AAAA=\t0\n", Up = false };
        await down.TickAfter(Past);
        Assert.Empty(down.Registered);

        var ok = new Harness();
        ok.HandshakeOutput = "AAAA=\t" + new DateTimeOffset(ok.Now).ToUnixTimeSeconds() + "\n";
        await ok.TickAfter(Past);
        Assert.Empty(ok.Registered);

        var unreadable = new Harness { HandshakeReadable = false };
        await unreadable.TickAfter(Past);
        Assert.Empty(unreadable.Registered);
    }

    [Fact]
    public async Task Con_l_handshake_sempre_illeggibile_oltre_la_pazienza_ri_registra_una_volta()
    {
        var h = new Harness { HandshakeReadable = false };
        await h.Recovery.TickAsync();
        h.Now += LinuxVpnKeyRecovery.UnreadablePatience + TimeSpan.FromSeconds(1);
        var line = await h.Recovery.TickAsync();
        Assert.Contains("registrata di nuovo", line);
        Assert.Single(h.Registered);
        h.Now += TimeSpan.FromMinutes(10);
        Assert.Equal("", await h.Recovery.TickAsync());
        Assert.Single(h.Registered);
    }

    [Fact]
    public void Legge_l_ultimo_handshake_dall_output_di_wg()
    {
        Assert.Equal(0, LinuxWireGuardCommands.ParseLatestHandshake("AAAA=\t0\n"));
        Assert.Equal(1700000000, LinuxWireGuardCommands.ParseLatestHandshake("AAAA=\t5\nBBBB=\t1700000000\n"));
        Assert.Null(LinuxWireGuardCommands.ParseLatestHandshake(""));
        Assert.Null(LinuxWireGuardCommands.ParseLatestHandshake("errore qualunque"));
    }
}
