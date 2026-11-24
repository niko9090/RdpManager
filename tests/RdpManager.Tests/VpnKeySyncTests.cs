using System.Net;
using RdpManager.Core;
using RdpManager.UI;
using RdpManager.Vpn;

namespace RdpManager.Tests;

/// <summary>k-176: «Crea chiavi» su un PC già registrato manda la chiave nuova al server; se il server rifiuta resta la vecchia.</summary>
public class VpnKeySyncTests : IDisposable
{
    private static readonly string ServerKey = WireGuardKeys.Generate().PublicKey;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-keysync-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "vpn.json");

    public VpnKeySyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private VpnProfile Registered()
    {
        var p = new VpnProfile { Address = "10.66.0.7/32", Endpoint = "vpn.example.com:51820", ServerPublicKey = ServerKey, PcName = "Ufficio" };
        p.GenerateKeys();
        p.Save(Path_);
        return p;
    }

    private sealed class FakeServer
    {
        public List<(string Name, string Key)> Calls { get; } = [];
        public Exception? Fail { get; set; }

        public Task<VpnGrant> Register(string name, string key, CancellationToken ct)
        {
            Calls.Add((name, key));
            if (Fail is not null)
                return Task.FromException<VpnGrant>(Fail);
            return Task.FromResult(new VpnGrant(name, "10.66.0.7/32", ServerKey, "vpn.example.com:51820", VpnDefaults.Network, "", 25));
        }
    }

    [Fact]
    public async Task Crea_chiavi_su_un_pc_registrato_manda_la_chiave_nuova_al_server_con_lo_stesso_indirizzo()
    {
        var p = Registered();
        var old = p.PublicKey;
        var server = new FakeServer();

        var r = await VpnKeySync.RotateAsync(p, Path_, server.Register);

        Assert.True(r.Ok);
        Assert.True(r.RegisteredOnServer);
        var call = Assert.Single(server.Calls);
        Assert.Equal("Ufficio", call.Name);
        Assert.NotEqual(old, call.Key);
        Assert.Equal(p.PublicKey, call.Key);
        Assert.Equal("10.66.0.7/32", p.Address);
        Assert.Equal(call.Key, VpnProfile.Load(Path_).PublicKey); // salvata
    }

    [Fact]
    public async Task Se_il_server_rifiuta_le_chiavi_locali_restano_quelle_di_prima()
    {
        var p = Registered();
        var oldPub = p.PublicKey;
        var oldProtected = p.PrivateKeyProtected;
        var server = new FakeServer { Fail = new SyncUnreachableException("Il server non risponde.") };

        var r = await VpnKeySync.RotateAsync(p, Path_, server.Register);

        Assert.False(r.Ok);
        Assert.Contains("non ha accettato", r.Message);
        Assert.Contains("non sono state cambiate", r.Message);
        Assert.Equal(oldProtected, p.PrivateKeyProtected);
        Assert.Equal(oldPub, p.PublicKey);
        Assert.Equal(oldPub, VpnProfile.Load(Path_).PublicKey); // il disco non è stato toccato
    }

    [Fact]
    public async Task Senza_account_le_chiavi_si_creano_solo_in_locale()
    {
        var p = Registered();
        var old = p.PublicKey;

        var r = await VpnKeySync.RotateAsync(p, Path_, null);

        Assert.True(r.Ok);
        Assert.False(r.RegisteredOnServer);
        Assert.NotEqual(old, p.PublicKey);
    }

    [Fact]
    public async Task Nessun_messaggio_contiene_chiavi_private()
    {
        var p = Registered();
        var server = new FakeServer { Fail = new SyncServerException(HttpStatusCode.Conflict, "key_in_use", "Questa chiave pubblica è già registrata.") };
        PasswordProtector.TryUnprotect(p.PrivateKeyProtected, out var priv);

        var r = await VpnKeySync.RotateAsync(p, Path_, server.Register);

        Assert.DoesNotContain(priv!, r.Message);
        Assert.Contains("già registrata", r.Message);
    }

    [Fact]
    public async Task La_ri_registrazione_rimanda_la_chiave_attuale_senza_crearne_di_nuove()
    {
        var p = Registered();
        var pub = p.PublicKey;
        var server = new FakeServer();

        var r = await VpnKeySync.ReRegisterAsync(p, server.Register);

        Assert.True(r.Ok);
        Assert.Equal(pub, Assert.Single(server.Calls).Key);
        Assert.Equal(pub, p.PublicKey);
    }

    [Fact]
    public void Il_controllo_automatico_tenta_una_sola_volta_e_solo_senza_handshake()
    {
        var w = new VpnKeyWatchdog();
        Assert.False(w.TryBegin(false));       // handshake presente: niente da fare
        Assert.True(w.TryBegin(true));         // primo tentativo
        Assert.False(w.TryBegin(true));        // mai due volte
        Assert.False(w.TryBegin(false));
        Assert.Equal(1, w.Attempts);
    }

    [Fact]
    public void Dal_pannello_VPN_Crea_chiavi_registra_sul_server_e_se_fallisce_lascia_la_chiave()
    {
        var p = Registered();
        var oldPub = p.PublicKey;
        var server = new FakeServer();
        string? shown = null;
        StaTestRunner.Run(() =>
        {
            using var form = new VpnForm(new NullTunnel(), Path_)
            {
                Registrar = () => server.Register,
                ConfirmOverride = _ => true,
                NotifyOverride = m => shown = m,
            };
            form.GenerateKeysAsync().GetAwaiter().GetResult();
            Assert.Single(server.Calls);
            Assert.NotEqual(oldPub, VpnProfile.Load(Path_).PublicKey);

            var afterOk = VpnProfile.Load(Path_).PublicKey;
            server.Fail = new SyncUnreachableException("Il server non risponde.");
            form.GenerateKeysAsync().GetAwaiter().GetResult();
            Assert.Equal(2, server.Calls.Count);
            Assert.Equal(afterOk, VpnProfile.Load(Path_).PublicKey);
            Assert.Contains("non ha accettato", shown);
        });
    }

    private sealed class NullTunnel : IWireGuardTunnel
    {
        public TunnelState Stato => TunnelState.Spento;
        public void Accendi(WireGuardConfig config) { }
        public void Spegni() { }
    }
}
