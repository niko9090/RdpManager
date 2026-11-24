using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.Tests;

/// <summary>Cartella «marshmelloVPN»: motivo dei fallimenti, ritentativi, riaccensione periodica e client marshmelloVPN.</summary>
public class VpnFolderRecoveryTests
{
    private sealed class FakeAccess : ISyncDocumentAccess
    {
        public ConnectionsDocument Doc { get; } = new();
        public ConnectionsDocument Snapshot() => SyncMerge.Snapshot(Doc);
        public bool Apply(Func<ConnectionsDocument, bool> change) => change(Doc);
    }

    private sealed class FakeTunnel(bool declineElevation = false, TunnelState state = TunnelState.Spento) : IWireGuardTunnel
    {
        public int Attempts;
        public TunnelState Stato { get; private set; } = state;

        public void Accendi(WireGuardConfig config)
        {
            Attempts++;
            if (declineElevation)
                throw new VpnElevationDeclinedException("annullato");
            Stato = TunnelState.Acceso;
        }

        public void Spegni() => Stato = TunnelState.Spento;
    }

    private sealed class FakeControl(bool integratedInstalled, bool clientInstalled = false, bool throws = false) : IVpnServiceControl
    {
        public TunnelState Query(string serviceName) => TunnelState.Spento;

        public bool Exists(string serviceName)
        {
            if (throws)
                throw new InvalidOperationException("scm");
            return serviceName == VpnExitHandler.ClientServiceName ? clientInstalled : integratedInstalled;
        }

        public void Start(string serviceName) { }
        public void Stop(string serviceName) { }
    }

    private static VpnProfile Profile()
    {
        var p = new VpnProfile { Address = "10.66.0.2/32", ServerPublicKey = Convert.ToBase64String(new byte[32]), PcName = "pc", Endpoint = "vpn.example.com:51820", AutoConnect = true };
        p.GenerateKeys();
        return p;
    }

    [Fact]
    public void Ritentativi_30_secondi_1_minuto_2_minuti_poi_i_5_minuti_e_il_successo_riparte()
    {
        var retry = new VpnFolderRetry();
        Assert.Equal(TimeSpan.FromSeconds(30), retry.OnFailure());
        Assert.Equal(TimeSpan.FromMinutes(1), retry.OnFailure());
        Assert.Equal(TimeSpan.FromMinutes(2), retry.OnFailure());
        Assert.Equal(TimeSpan.FromMinutes(5), retry.OnFailure());
        Assert.Equal(TimeSpan.FromMinutes(5), retry.OnFailure());
        Assert.Equal(TimeSpan.FromMinutes(5), retry.OnSuccess());
        Assert.Equal(TimeSpan.FromSeconds(30), retry.OnFailure());
    }

    [Fact]
    public async Task Server_irraggiungibile_dice_il_motivo_e_non_cancella_connessioni()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-vfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sync.json");
            new SyncState
            {
                ServerUrl = "http://127.0.0.1:1/",
                Username = "mario",
                ProtectedPassword = PasswordProtector.Protect("password-lunga-abbastanza"),
            }.Save(path);
            using var sync = new SyncService(path, 1000);
            var access = new FakeAccess();
            access.Doc.Connections.Add(new RdpConnection { Name = "PC", Host = "10.66.0.5", AutoVpnDevice = "pc", Group = VpnFolder.FolderName });

            var result = await sync.TryRefreshVpnFolderAsync(access);

            Assert.True(result.Failed);
            Assert.False(result.Changed);
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
            Assert.Single(access.Doc.Connections);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Senza_account_non_e_un_fallimento()
    {
        using var sync = new SyncService(Path.Combine(Path.GetTempPath(), "rdpm-vfr-" + Guid.NewGuid().ToString("N") + ".sync"), 1000);
        var result = await sync.TryRefreshVpnFolderAsync(new FakeAccess());
        Assert.False(result.Failed);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Riaccende_il_tunnel_giu_con_autoconnect()
    {
        var tunnel = new FakeTunnel();
        var reconnect = new VpnAutoReconnect(tunnel, new FakeControl(true));
        Assert.Equal("VPN collegata.", reconnect.Check(Profile(), _ => { }));
        Assert.Equal(1, tunnel.Attempts);
        // tunnel su: niente da fare
        Assert.Equal("", reconnect.Check(Profile(), _ => { }));
        Assert.Equal(1, tunnel.Attempts);
    }

    [Fact]
    public void Senza_autoconnect_non_fa_niente()
    {
        var tunnel = new FakeTunnel();
        var profile = Profile();
        profile.AutoConnect = false;
        Assert.Equal("", new VpnAutoReconnect(tunnel, new FakeControl(true)).Check(profile, _ => { }));
        Assert.Equal(0, tunnel.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Elevazione_rifiutata_non_si_richiede_piu_fino_al_reset(bool integratedInstalled)
    {
        var tunnel = new FakeTunnel(declineElevation: true);
        var reconnect = new VpnAutoReconnect(tunnel, new FakeControl(integratedInstalled));
        reconnect.Check(Profile(), _ => { });
        Assert.True(reconnect.Declined);
        Assert.Equal(1, tunnel.Attempts);

        for (var i = 0; i < 3; i++)
            Assert.Equal("", reconnect.Check(Profile(), _ => { }));
        Assert.Equal(1, tunnel.Attempts);

        reconnect.Reset();
        reconnect.Check(Profile(), _ => { });
        Assert.Equal(2, tunnel.Attempts);
    }

    [Fact]
    public void Col_client_marshmellovpn_non_accende_niente()
    {
        var tunnel = new FakeTunnel();
        Assert.Equal("", new VpnAutoReconnect(tunnel, new FakeControl(false, clientInstalled: true)).Check(Profile(), _ => { }));
        Assert.Equal(0, tunnel.Attempts);
    }

    [Fact]
    public void Guardia_client_vede_il_servizio_e_nel_dubbio_dice_si()
    {
        Assert.True(VpnClientGuard.IsManagedByClient(new FakeControl(false, clientInstalled: true)));
        Assert.False(VpnClientGuard.IsManagedByClient(new FakeControl(true, clientInstalled: false)));
        Assert.True(VpnClientGuard.IsManagedByClient(new FakeControl(false, throws: true)));
    }
}
