using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>VPN automatica lato client: dalla risposta del server al profilo, e il giro completo con il server vero (WireGuard finto).</summary>
public class VpnProvisioningTests
{
    private static readonly string ServerKey = WireGuardKeys.Generate().PublicKey;

    private static string GrantJson(string address = "10.66.0.5/32", string dns = "", string? serverKey = null) =>
        JsonSerializer.Serialize(new
        {
            name = "Ufficio", address, serverPublicKey = serverKey ?? ServerKey, endpoint = "syncnetpro.com:51820",
            allowedIPs = "10.66.0.0/24", dns, persistentKeepalive = 25,
        });

    private static VpnGrant Grant(string json) => VpnGrant.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void La_risposta_del_server_diventa_un_profilo_con_cui_si_costruisce_la_configurazione()
    {
        var profile = new VpnProfile();
        var pub = profile.GenerateKeys();
        VpnProvisioning.Apply(profile, Grant(GrantJson(dns: "10.66.0.1")));

        Assert.Equal("10.66.0.5/32", profile.Address);
        Assert.Equal(ServerKey, profile.ServerPublicKey);
        Assert.Equal("syncnetpro.com:51820", profile.Endpoint);
        Assert.Equal("Ufficio", profile.PcName);
        Assert.False(VpnProvisioning.NeedsProvisioning(profile));
        Assert.Equal(pub, profile.PublicKey); // la chiave del PC non cambia

        var config = profile.ToConfig();
        Assert.True(config.IsValid, string.Join("; ", config.Validate()));
        var conf = config.ToConf();
        Assert.Contains("Address = 10.66.0.5/32", conf);
        Assert.Contains("DNS = 10.66.0.1", conf);
        Assert.Contains("PublicKey = " + ServerKey, conf);
        Assert.Contains("Endpoint = syncnetpro.com:51820", conf);
        Assert.Contains("AllowedIPs = 10.66.0.0/24", conf);
        Assert.Contains("PersistentKeepalive = 25", conf);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"address\":\"10.66.0.5/32\",\"endpoint\":\"x:1\",\"serverPublicKey\":\"corta\"}")]
    [InlineData("{\"address\":\"non-un-indirizzo\",\"endpoint\":\"x:1\"}")]
    public void Una_risposta_incompleta_e_rifiutata(string json)
    {
        Assert.Throws<FormatException>(() => Grant(json));
    }

    [Fact]
    public void Serve_il_profilo_solo_se_manca_qualcosa()
    {
        Assert.True(VpnProvisioning.NeedsProvisioning(new VpnProfile()));

        var p = new VpnProfile();
        p.GenerateKeys();
        Assert.True(VpnProvisioning.NeedsProvisioning(p)); // chiavi senza indirizzo/server

        p.Address = "10.66.0.9/32";
        p.ServerPublicKey = ServerKey;
        Assert.False(VpnProvisioning.NeedsProvisioning(p)); // anche un profilo fatto a mano resta com'e'
    }

    [Theory]
    [InlineData("DESKTOP-ABC", "DESKTOP-ABC")]
    [InlineData("PC di Anna", "PC di Anna")]
    [InlineData("pc/strano:è", "pc-strano--")]
    [InlineData("", "PC")]
    public void Il_nome_del_pc_e_adatto_al_server(string input, string expected)
    {
        Assert.Equal(expected, VpnProvisioning.SafeName(input));
    }

    [Fact]
    public async Task Senza_account_non_si_chiede_niente()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-vpn-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var sync = new SyncService(Path.Combine(dir, "sync.json"), 1000);
            Assert.Null(await VpnProvisioning.EnsureAsync(sync, Path.Combine(dir, "vpn.json")));
            Assert.False(File.Exists(Path.Combine(dir, "vpn.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Il giro completo: registrazione dall'app, accesso, richiesta della VPN, profilo salvato (server vero in-process).</summary>
public class VpnProvisioningEndToEndTests : IAsyncLifetime
{
    private const int Iterations = 1000;
    private static readonly string ServerKey = WireGuardKeys.Generate().PublicKey;

    private sealed class FakeWg : RdpmSync.IWireGuardPeers
    {
        public List<RdpmSync.WgPeer> Peers { get; private set; } = [];
        public IReadOnlyCollection<string> ReservedAddresses() => [];
        public Task ApplyAsync(IReadOnlyCollection<RdpmSync.WgPeer> desired, CancellationToken ct = default)
        {
            Peers = desired.ToList();
            return Task.CompletedTask;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-vpn-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWg _wg = new();
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private string _url = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var options = new RdpmSync.SyncOptions
        {
            DatabasePath = Path.Combine(_dir, "db.sqlite"),
            ServerHashIterations = 1000,
            OpenRegistration = true,
            VpnServerPublicKey = ServerKey,
        };
        _app = RdpmSync.SyncApp.Build(options, null, "http://127.0.0.1:0", _wg);
        _app.Start();
        _url = _app.Urls.First();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Registrazione_accesso_e_vpn_senza_nessun_passo_manuale()
    {
        using var sync = new SyncService(Path.Combine(_dir, "sync.json"), Iterations);
        await sync.RegisterAsync(_url, "mario", "frase segreta lunga", null);
        await sync.SignInAsync(_url, "mario", "frase segreta lunga");

        var path = Path.Combine(_dir, "vpn.json");
        var profile = await VpnProvisioning.EnsureAsync(sync, path, "Ufficio");
        Assert.NotNull(profile);
        Assert.Equal("10.66.0.2/32", profile!.Address);
        Assert.Equal(ServerKey, profile.ServerPublicKey);
        Assert.True(profile.ToConfig().IsValid);

        // il server ha ricevuto la sola chiave pubblica di questo PC
        var peer = Assert.Single(_wg.Peers);
        Assert.Equal(profile.PublicKey, peer.PublicKey);

        // salvato su disco, e rifarlo non cambia niente (stessa chiave, stesso indirizzo, nessuna chiamata)
        var reloaded = VpnProfile.Load(path);
        Assert.Equal(profile.Address, reloaded.Address);
        var again = await VpnProvisioning.EnsureAsync(sync, path, "Ufficio");
        Assert.Equal(profile.PublicKey, again!.PublicKey);
        Assert.Single(_wg.Peers);
    }

    [Fact]
    public async Task Se_il_profilo_salvato_e_perso_ma_le_chiavi_no_il_server_da_lo_stesso_indirizzo()
    {
        using var sync = new SyncService(Path.Combine(_dir, "sync.json"), Iterations);
        await sync.RegisterAsync(_url, "mario", "frase segreta lunga", null);
        await sync.SignInAsync(_url, "mario", "frase segreta lunga");
        var path = Path.Combine(_dir, "vpn.json");
        var first = await VpnProvisioning.EnsureAsync(sync, path, "Ufficio");

        // simula una chiamata interrotta: restano le chiavi ma non indirizzo e chiave del server
        var partial = VpnProfile.Load(path);
        partial.Address = "";
        partial.ServerPublicKey = "";
        partial.Save(path);

        var second = await VpnProvisioning.EnsureAsync(sync, path, "Ufficio");
        Assert.Equal(first!.Address, second!.Address);
        Assert.Single(_wg.Peers);
    }

    [Fact]
    public async Task Registrazione_con_nome_gia_preso_e_account_inesistente()
    {
        using var sync = new SyncService(Path.Combine(_dir, "sync.json"), Iterations);
        await sync.RegisterAsync(_url, "mario", "frase segreta lunga", null);
        var dup = await Assert.ThrowsAsync<SyncServerException>(() => sync.RegisterAsync(_url, "mario", "altra", null));
        Assert.Equal("account_exists", dup.Code);
        await Assert.ThrowsAsync<SyncServerException>(() => sync.SignInAsync(_url, "nessuno", "boh"));
    }
}
