using Microsoft.Extensions.Hosting;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Import del file di configurazione: arriva al profilo VPN, alle impostazioni del server, registra il PC (server vero
/// in-process con WireGuard finto) e accende un tunnel finto. Nessun servizio vero.
/// </summary>
public class ConfigFileImportTests : IAsyncLifetime
{
    private const string Password = "frase segreta lunga";
    private static readonly string ServerKey = RdpmConfigFileTests.ServerKey;

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

    private sealed class FakeTunnel
    {
        public List<WireGuardConfig> Started { get; } = [];
        public string? Failure { get; set; }

        public Task<string?> ConnectAsync(WireGuardConfig config, CancellationToken ct)
        {
            Started.Add(config);
            return Task.FromResult(Failure);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-cfgimp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWg _wg = new();
    private readonly FakeTunnel _tunnel = new();
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private string _url = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var options = new RdpmSync.SyncOptions
        {
            DatabasePath = Path.Combine(_dir, "db.sqlite"),
            ServerHashIterations = 1000,
            RegistrationCode = "codice-di-prova",
            VpnServerPublicKey = ServerKey,
            VpnEndpoint = "vpn.example.com:51820",
            VpnDns = "10.66.0.1",
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

    private SyncService NewSync() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".sync"), 1000);

    private string ProfileFile => Path.Combine(_dir, "vpn.json");

    private ConfigImportOptions Options() => new()
    {
        ProfilePath = ProfileFile,
        DataFolder = Path.Combine(_dir, "dati"),
        PcName = "Ufficio",
    };

    private RdpmConfigFile File_(bool auto = true, bool account = true) => RdpmConfigFile.Parse($$"""
        {
          "server": { "url": "{{_url}}", "codiceRegistrazione": "codice-di-prova" },
          {{(account ? $$"""
          "account": { "utente": "mario", "password": "{{Password}}" },
          """ : "")}}
          "vpn": { "endpoint": "vpn.example.com:51820", "chiavePubblicaServer": "{{ServerKey}}",
                   "dns": "10.66.0.1", "keepalive": 20, "richiediChiaveAlServer": {{(auto ? "true" : "false")}} }
        }
        """);

    [Fact]
    public async Task Il_file_con_account_registra_il_PC_e_accende_la_VPN_senza_compilare_niente()
    {
        using var sync = NewSync();
        var report = await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());

        Assert.True(report.Complete, report.Text);
        Assert.Equal("Fatto", report.Title);
        Assert.True(report.VpnConnected);
        Assert.True(sync.SignedIn);
        Assert.Equal("mario", sync.State.Username);

        // profilo VPN: dati del file + indirizzo assegnato dal server, chiave privata cifrata
        var profile = VpnProfile.Load(ProfileFile);
        Assert.Equal("10.66.0.2/32", profile.Address);
        Assert.Equal(ServerKey, profile.ServerPublicKey);
        Assert.Equal("vpn.example.com:51820", profile.Endpoint);
        Assert.Equal(25, profile.PersistentKeepalive); // dopo la registrazione vale il keepalive deciso dal server
        Assert.True(profile.HasKey);
        Assert.DoesNotContain("PrivateKey", File.ReadAllText(ProfileFile).Replace("PrivateKeyProtected", ""));
        var peer = Assert.Single(_wg.Peers);
        Assert.Equal(profile.PublicKey, peer.PublicKey); // al server è andata solo la pubblica

        // il tunnel è partito con la configurazione del profilo
        var started = Assert.Single(_tunnel.Started);
        Assert.True(started.IsValid);
        Assert.Equal(profile.PersistentKeepalive, started.PersistentKeepalive);

        // impostazioni di sincronizzazione: ritrovate dal dialogo dell'account
        var saved = ClientConfig.Load(Path.Combine(Options().DataFolder!, ClientConfig.FileName));
        Assert.Equal(SyncHttp.NormalizeUrl(_url), saved.ServerUrl);
        Assert.Equal("codice-di-prova", saved.RegistrationCode);
        Assert.Equal("vpn.example.com:51820", saved.VpnEndpoint);
    }

    [Fact]
    public async Task Rifare_l_import_tiene_lo_stesso_account_e_lo_stesso_indirizzo()
    {
        using var sync = NewSync();
        await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());
        var report = await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());
        Assert.True(report.Complete, report.Text);
        Assert.Contains(report.Lines, l => l.Contains("già collegato"));
        Assert.Equal("10.66.0.2/32", VpnProfile.Load(ProfileFile).Address);
        Assert.Single(_wg.Peers);
    }

    [Fact]
    public async Task Senza_account_nel_file_il_profilo_e_salvato_e_si_spiega_cosa_manca()
    {
        using var sync = NewSync();
        var report = await ConfigFileImporter.ImportAsync(File_(account: false), sync, _tunnel.ConnectAsync, Options());

        Assert.False(report.Complete);
        Assert.Equal("Da completare", report.Title);
        Assert.Empty(_tunnel.Started); // senza indirizzo assegnato il tunnel non parte
        Assert.Contains(report.Lines, l => l.Contains("accedi all'account"));
        var profile = VpnProfile.Load(ProfileFile);
        Assert.Equal(ServerKey, profile.ServerPublicKey);
        Assert.Equal("", profile.Address);
        Assert.True(File.Exists(Path.Combine(Options().DataFolder!, ClientConfig.FileName)));
    }

    [Fact]
    public async Task Password_sbagliata_o_codice_sbagliato_sono_spiegati_senza_eccezioni()
    {
        using var sync = NewSync();
        // account già creato con un'altra password
        await sync.RegisterAsync(_url, "mario", "un'altra frase lunga", "codice-di-prova");
        var wrongPassword = await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());
        Assert.False(wrongPassword.Complete);
        Assert.Contains(wrongPassword.Lines, l => l.Contains("utente o password sbagliati"));
        Assert.Empty(_tunnel.Started);

        var wrongCode = RdpmConfigFile.Parse($$"""
            { "server": { "url": "{{_url}}", "codiceRegistrazione": "sbagliato" }, "account": { "utente": "anna", "password": "{{Password}}" } }
            """);
        var report = await ConfigFileImporter.ImportAsync(wrongCode, NewSync(), _tunnel.ConnectAsync, Options());
        Assert.False(report.Complete);
        Assert.Contains(report.Lines, l => l.Contains("codice di registrazione"));
    }

    [Fact]
    public async Task Se_il_tunnel_non_parte_il_riepilogo_lo_dice()
    {
        _tunnel.Failure = "servizio non avviabile";
        using var sync = NewSync();
        var report = await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());
        Assert.False(report.Complete);
        Assert.False(report.VpnConnected);
        Assert.Contains(report.Lines, l => l.Contains("non accesa") && l.Contains("servizio non avviabile"));
    }

    [Fact]
    public async Task Un_conf_WireGuard_nella_cartella_temporanea_si_importa_e_si_cancella()
    {
        var priv = WireGuardKeys.Generate().PrivateKey;
        var conf = new WireGuardConfig
        {
            PrivateKey = priv, Address = "10.66.0.9/32", PeerPublicKey = ServerKey, Endpoint = "vpn.example.com:51820",
        }.ToConf();
        var path = Path.Combine(Path.GetTempPath(), "rdpm-import-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(path, conf);
        try
        {
            using var sync = NewSync();
            var report = await ConfigFileImporter.ImportFileAsync(path, sync, _tunnel.ConnectAsync, Options());
            Assert.True(report.Complete, report.Text);
            Assert.True(report.SourceDeleted);
            Assert.False(File.Exists(path)); // la chiave privata non resta su disco
            var profile = VpnProfile.Load(ProfileFile);
            Assert.Equal("10.66.0.9/32", profile.Address);
            Assert.True(profile.ToConfig().IsValid);
            Assert.Equal(priv, Assert.Single(_tunnel.Started).PrivateKey);
            Assert.DoesNotContain(priv, File.ReadAllText(ProfileFile)); // nel profilo è cifrata
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Un_file_non_si_cancella_se_non_lo_si_chiede_o_se_non_e_nella_cartella_temporanea()
    {
        var path = Path.Combine(_dir, "mio.rdpmconfig");
        File.WriteAllText(path, File_().ToJson());
        using var sync = NewSync();
        var report = await ConfigFileImporter.ImportFileAsync(path, sync, _tunnel.ConnectAsync, Options() with { DeleteSource = false });
        Assert.True(report.Complete, report.Text);
        Assert.False(report.SourceDeleted);
        Assert.True(File.Exists(path));
        Assert.Contains("dati riservati", report.SourceWarning);

        var deleted = await ConfigFileImporter.ImportFileAsync(path, sync, _tunnel.ConnectAsync, Options() with { DeleteSource = true });
        Assert.True(deleted.SourceDeleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Un_file_non_valido_non_tocca_il_profilo()
    {
        var path = Path.Combine(_dir, "rotto.rdpmconfig");
        File.WriteAllText(path, """{ "vpn": { "endpoint": "senza-porta" } }""");
        using var sync = NewSync();
        await Assert.ThrowsAsync<ConfigFileException>(() => ConfigFileImporter.ImportFileAsync(path, sync, _tunnel.ConnectAsync, Options()));
        Assert.False(File.Exists(ProfileFile));
        Assert.Empty(_tunnel.Started);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Cambiando_server_l_indirizzo_vecchio_si_rifa_ma_la_chiave_resta()
    {
        using var sync = NewSync();
        await ConfigFileImporter.ImportAsync(File_(), sync, _tunnel.ConnectAsync, Options());
        var before = VpnProfile.Load(ProfileFile);
        var other = RdpmConfigFile.Parse($$"""
            { "server": { "url": "{{_url}}" }, "vpn": { "endpoint": "altro.example.com:51820", "chiavePubblicaServer": "{{WireGuardKeys.Generate().PublicKey}}", "richiediChiaveAlServer": true } }
            """);
        var report = await ConfigFileImporter.ImportAsync(other, NewSync(), null, Options());
        var after = VpnProfile.Load(ProfileFile);
        Assert.Equal("altro.example.com:51820", after.Endpoint);
        Assert.Equal("", after.Address);
        Assert.Equal(before.PublicKey, after.PublicKey); // la chiave privata di questo PC non cambia
        Assert.False(report.Complete);
    }
}
