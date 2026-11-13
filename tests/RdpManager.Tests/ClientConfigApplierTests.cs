using System.Security.AccessControl;
using Microsoft.Extensions.Hosting;
using RdpManager.Core;
using RdpManagerVpn;

namespace RdpManager.Tests;

/// <summary>
/// RdpManager VPN (client senza RdpManager): applica il file .rdpmconfig con il codice di RdpManager. Server vero in-process con
/// WireGuard finto e installer del profilo finto: nessun servizio vero, nessuna scrittura in ProgramData.
/// </summary>
public class ClientConfigApplierTests : IAsyncLifetime
{
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

    private sealed class FakeInstaller : IClientProfileInstaller
    {
        public List<WireGuardConfig> Installed { get; } = [];
        public string? Failure { get; set; }

        public string? Install(WireGuardConfig config)
        {
            Installed.Add(config);
            return Failure;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-vpnclient-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWg _wg = new();
    private readonly FakeInstaller _installer = new();
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private string _url = "";

    private string Work => Path.Combine(_dir, "lavoro");

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
        try { Directory.Delete(_dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string WriteFile(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".rdpmconfig");
        File.WriteAllText(path, json);
        return path;
    }

    private string ValidFile(bool account) => WriteFile($$"""
        {
          "server": { "url": "{{_url}}", "codiceRegistrazione": "codice-di-prova" },
          {{(account ? """ "account": { "utente": "mario", "password": "frase segreta lunga" }, """ : "")}}
          "vpn": { "endpoint": "vpn.example.com:51820", "chiavePubblicaServer": "{{ServerKey}}", "richiediChiaveAlServer": true }
        }
        """);

    [Fact]
    public async Task Con_il_file_registra_il_PC_e_consegna_il_profilo_all_installer()
    {
        var report = await ClientConfigApplier.ApplyFileAsync(ValidFile(account: true), Work, _installer, "Ufficio");

        Assert.True(report.Complete, report.Text);
        Assert.Equal("Fatto", report.Title);
        Assert.True(report.VpnConnected);
        var config = Assert.Single(_installer.Installed);
        Assert.True(config.IsValid);
        Assert.Equal("10.66.0.2/32", config.Address);
        Assert.Equal(ServerKey, config.PeerPublicKey);

        // al server è andata solo la chiave pubblica; la privata resta sul PC (cifrata, nella cartella privata)
        var peer = Assert.Single(_wg.Peers);
        Assert.Equal(WireGuardKeys.PublicKeyFromPrivate(config.PrivateKey), peer.PublicKey);
        Assert.DoesNotContain(config.PrivateKey, File.ReadAllText(Path.Combine(Work, ClientConfigApplier.ProfileFileName)));
        Assert.Contains("riservati", report.Text); // il file ha segreti: avviso a cancellarlo
    }

    [Fact]
    public async Task Con_il_solo_codice_il_PC_si_crea_un_account_suo_e_si_registra()
    {
        var report = await ClientConfigApplier.ApplyFileAsync(ValidFile(account: false), Work, _installer, "Magazzino 2");

        Assert.True(report.Complete, report.Text);
        Assert.Single(_installer.Installed);
        Assert.Single(_wg.Peers);
        using var sync = new SyncService(Path.Combine(Work, ClientConfigApplier.AccountFileName), 1000);
        Assert.True(sync.SignedIn);
        Assert.Equal("pc-magazzino-2", sync.State.Username);

        // seconda volta: stesso account, stesso indirizzo, nessun nuovo PC
        var again = await ClientConfigApplier.ApplyFileAsync(ValidFile(account: false), Work, _installer, "Magazzino 2");
        Assert.True(again.Complete, again.Text);
        Assert.Equal(_installer.Installed[0].Address, _installer.Installed[1].Address);
        Assert.Single(_wg.Peers);
    }

    [Fact]
    public async Task Un_file_con_errori_elenca_tutto_e_non_modifica_niente()
    {
        var path = WriteFile("""
            { "server": { "url": "non è un indirizzo" },
              "vpn": { "endpoint": "senza-porta", "chiavePubblicaServer": "corta" } }
            """);

        var ex = await Assert.ThrowsAsync<ConfigFileException>(() => ClientConfigApplier.ApplyFileAsync(path, Work, _installer));

        Assert.True(ex.Errors.Count >= 3, ex.Message);
        Assert.Empty(_installer.Installed);
        Assert.False(Directory.Exists(Work));
        Assert.Empty(_wg.Peers);
    }

    [Fact]
    public async Task Un_file_senza_la_parte_VPN_viene_rifiutato_senza_modifiche()
    {
        var path = WriteFile($$"""{ "server": { "url": "{{_url}}" } }""");

        var ex = await Assert.ThrowsAsync<ConfigFileException>(() => ClientConfigApplier.ApplyFileAsync(path, Work, _installer));

        Assert.Contains("«vpn»", ex.Message);
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public async Task File_inesistente_dice_che_non_c_e()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => ClientConfigApplier.ApplyFileAsync(Path.Combine(_dir, "no.rdpmconfig"), Work, _installer));

    [Fact]
    public async Task Se_il_profilo_non_si_installa_il_riepilogo_dice_da_completare_e_perche()
    {
        _installer.Failure = "serve l'autorizzazione di amministratore";

        var report = await ClientConfigApplier.ApplyFileAsync(ValidFile(account: true), Work, _installer);

        Assert.False(report.Complete);
        Assert.Equal("Da completare", report.Title);
        Assert.Contains("serve l'autorizzazione di amministratore", report.Text);
    }

    [Fact]
    public async Task Server_irraggiungibile_da_da_completare_senza_accendere_il_tunnel()
    {
        var path = WriteFile("""
            { "server": { "url": "http://127.0.0.1:9", "codiceRegistrazione": "x" },
              "account": { "utente": "mario", "password": "frase segreta lunga" },
              "vpn": { "endpoint": "vpn.example.com:51820", "chiavePubblicaServer": "%KEY%", "richiediChiaveAlServer": true } }
            """.Replace("%KEY%", ServerKey));

        var report = await ClientConfigApplier.ApplyFileAsync(path, Work, _installer);

        Assert.False(report.Complete);
        Assert.Empty(_installer.Installed);
        Assert.Contains("non", report.Text);
    }

    [Fact]
    public void Il_profilo_scritto_ha_il_contenuto_giusto_e_permessi_solo_per_SYSTEM_e_amministratori()
    {
        var path = Path.Combine(_dir, "vpn.conf");
        var config = new WireGuardConfig
        {
            PrivateKey = WireGuardKeys.Generate().PrivateKey, Address = "10.66.0.7/32", PeerPublicKey = ServerKey, Endpoint = "vpn.example.com:51820",
        };

        ClientProfileInstaller.WriteProtected(path, config.ToConf(), protect: true);

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights));
        Assert.Empty(Directory.GetFiles(_dir, "vpn.conf.*.tmp"));
    }

    [Fact]
    public void Senza_amministratore_il_profilo_passa_da_un_file_temporaneo_e_UAC_rifiutato_si_spiega()
    {
        string? seen = null;
        var installer = new ClientProfileInstaller(_dir, "x.exe", Work, () => false, temp => { seen = File.ReadAllText(temp); return ClientProfileInstaller.UacDeclined; },
            _ => throw new InvalidOperationException("il servizio non va avviato"));
        var config = new WireGuardConfig { PrivateKey = WireGuardKeys.Generate().PrivateKey, Address = "10.66.0.7/32", PeerPublicKey = ServerKey, Endpoint = "vpn.example.com:51820" };

        var failure = installer.Install(config);

        Assert.Contains("amministratore", failure);
        Assert.True(ClientProfile.Check(seen!).Ok);
        Assert.False(File.Exists(Path.Combine(Work, ClientProfileInstaller.TempProfileName))); // il temporaneo non resta
    }

    [Fact]
    public void Il_comando_con_UAC_rifiuta_file_con_altro_nome_o_profili_non_validi()
    {
        var other = Path.Combine(_dir, "altro.conf");
        File.WriteAllText(other, "x");
        Assert.Equal(3, ClientProfileInstaller.RunApplyCommand(other, "x.exe", _dir));

        Directory.CreateDirectory(Work);
        var bad = Path.Combine(Work, ClientProfileInstaller.TempProfileName);
        File.WriteAllText(bad, "[Interface]\nPrivateKey = no");
        Assert.Equal(4, ClientProfileInstaller.RunApplyCommand(bad, "x.exe", _dir));
        Assert.False(Directory.Exists(ClientPaths.DataDirectory(_dir))); // niente installato
    }
}
