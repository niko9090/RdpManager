using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>
/// File di configurazione (.rdpmconfig / .conf) su Linux: pulsante e comandi, import fino al profilo VPN e alle impostazioni
/// del server, accensione con wg-quick finto, file temporaneo cancellato. Nessun servizio vero (processi e rete finti).
/// </summary>
public class ConfigFileTests : IDisposable
{
    private static readonly string ServerKey = WireGuardKeys.Generate().PublicKey;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-cfgfile-" + Guid.NewGuid().ToString("N"));

    public ConfigFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Profile => Path.Combine(_dir, "vpn.json");
    private string Data => Path.Combine(_dir, "dati");

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string RdpmConfig(bool auto = true) => $$"""
        {
          "formato": "rdpmconfig", "versione": 1,
          "server": { "url": "https://vpn.example.com:8443", "urlInterno": "http://10.66.0.1:8787", "codiceRegistrazione": "codice-di-prova" },
          "vpn": { "endpoint": "vpn.example.com:51820", "chiavePubblicaServer": "{{ServerKey}}", "richiediChiaveAlServer": {{(auto ? "true" : "false")}} }
        }
        """;

    private static string Conf(string privateKey) => new WireGuardConfig
    {
        PrivateKey = privateKey, Address = "10.66.0.9/32", PeerPublicKey = ServerKey, Endpoint = "vpn.example.com:51820",
    }.ToConf();

    private (VpnViewModel Vpn, RecordingRunner Runner, SyncService Sync) NewVpn(NullShellUi? ui = null)
    {
        var up = false;
        var runner = new RecordingRunner((file, args) => (file, args[0]) switch
        {
            ("pkexec", _) when args.Contains("up") => Set(true),
            ("pkexec", _) when args.Contains("down") => Set(false),
            ("ip", _) => new ProcessResult(up ? 0 : 1, "", ""),
            _ => new ProcessResult(0, "", ""),
        });
        ProcessResult Set(bool value) { up = value; return new ProcessResult(0, "", ""); }
        var sync = new SyncService(Path.Combine(_dir, "conn.json.sync"), 1000);
        var vpn = new VpnViewModel(new LinuxWireGuard(runner, Path.Combine(_dir, "RdpManager.conf"), LinuxVpnBackend.WgQuick), sync,
            ui ?? new NullShellUi(), new FakeDialogs(), Profile, Data);
        return (vpn, runner, sync);
    }

    [Fact]
    public async Task Il_file_rdpmconfig_senza_account_salva_server_e_profilo_e_dice_cosa_manca()
    {
        var (vpn, runner, sync) = NewVpn();
        using var _ = sync;
        var path = WriteFile("a.rdpmconfig", RdpmConfig());

        await vpn.ImportConfigFileAsync(path);

        Assert.Contains("Da completare", vpn.Message);
        Assert.Contains("accedi", vpn.Message);
        Assert.Equal("vpn.example.com:51820", vpn.Endpoint);
        Assert.Equal(ServerKey, vpn.ServerPublicKey);
        var profile = VpnProfile.Load(Profile);
        Assert.Equal(ServerKey, profile.ServerPublicKey);
        Assert.Equal("", profile.Address); // lo assegna il server alla registrazione
        var saved = ClientConfig.Load(Path.Combine(Data, ClientConfig.FileName));
        Assert.Equal("https://vpn.example.com:8443/", saved.ServerUrl);
        Assert.Equal("codice-di-prova", saved.RegistrationCode);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("wg-quick up")); // niente tunnel senza indirizzo
        Assert.False(File.Exists(path)); // il file con segreti nella cartella temporanea si cancella dopo l'import
    }

    [Fact]
    public async Task Un_conf_WireGuard_temporaneo_accende_la_VPN_con_wg_quick_e_si_cancella()
    {
        var (vpn, runner, sync) = NewVpn();
        using var _ = sync;
        var priv = WireGuardKeys.Generate().PrivateKey;
        var path = Path.Combine(Path.GetTempPath(), "rdpm-conf-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(path, Conf(priv));
        try
        {
            await vpn.ImportConfigFileAsync(path);

            Assert.StartsWith("Fatto", vpn.Message);
            Assert.Contains(runner.Calls, c => c.StartsWith("pkexec wg-quick up"));
            Assert.True(vpn.IsConnected);
            Assert.False(File.Exists(path)); // la chiave privata non resta nel file di partenza
            var profile = VpnProfile.Load(Profile);
            Assert.Equal("10.66.0.9/32", profile.Address);
            Assert.DoesNotContain(priv, File.ReadAllText(Profile)); // nel profilo è cifrata
            Assert.Equal(vpn.PublicKey, profile.PublicKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Un_file_non_valido_mostra_il_motivo_e_non_cambia_niente()
    {
        var (vpn, runner, sync) = NewVpn();
        using var _ = sync;
        var path = WriteFile("rotto.rdpmconfig", """{ "vpn": { "endpoint": "senza-porta", "chiavePubblicaServer": "x" } }""");

        await vpn.ImportConfigFileAsync(path);

        Assert.Contains("non è valido", vpn.Message);
        Assert.Contains("vpn.endpoint", vpn.Message);
        Assert.Equal("", VpnProfile.Load(Profile).ServerPublicKey);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("wg-quick"));
        Assert.False(File.Exists(Path.Combine(Data, ClientConfig.FileName)));
        Assert.True(File.Exists(path));

        await vpn.ImportConfigFileAsync(Path.Combine(_dir, "manca.rdpmconfig"));
        Assert.Contains("non trovato", vpn.Message);
    }

    [Fact]
    public async Task Il_pulsante_Carica_file_di_configurazione_usa_il_selettore()
    {
        var path = WriteFile("b.rdpmconfig", RdpmConfig(auto: false));
        var ui = new NullShellUi { FileAnswer = path };
        var (vpn, _, sync) = NewVpn(ui);
        using var _ = sync;

        Assert.True(vpn.ImportConfigCommand.CanExecute(null));
        vpn.ImportConfigCommand.Execute(null);
        for (int i = 0; i < 100 && !File.Exists(Profile); i++)
            await Task.Delay(50);
        for (int i = 0; i < 100 && vpn.Message.Length == 0; i++)
            await Task.Delay(50);

        Assert.Contains("Da completare", vpn.Message); // senza richiesta al server servono ancora le chiavi sul server
        Assert.Equal(ServerKey, VpnProfile.Load(Profile).ServerPublicKey);
    }

    [Fact]
    public async Task L_account_importa_un_rdpmconfig_con_l_import_completo_e_un_json_con_quello_vecchio()
    {
        using var data = new TempData();
        using var sync = new SyncService(SyncState.PathFor(data.Store), 1000);
        var imported = new List<string>();
        var rdpm = WriteFile("c.rdpmconfig", RdpmConfig());
        var ui = new NullShellUi { FileAnswer = rdpm };
        var account = new AccountViewModel(sync, () => Task.FromResult(new SyncOutcome(SyncStatus.UpToDate, "")), () => null, ui, new FakeDialogs(),
            defaultConfig: new ClientConfig("", "", ""),
            importFile: path => { imported.Add(path); return Task.FromResult("Fatto (prova)"); });

        await account.ImportConfigAsync();
        Assert.Equal([rdpm], imported);
        Assert.Equal("Fatto (prova)", account.Status);

        var json = WriteFile("d.json", """{"server":"https://vecchio.example.com:8443","codiceRegistrazione":"abc"}""");
        ui.FileAnswer = json;
        await account.ImportConfigAsync();
        Assert.Single(imported); // il .json passa dal vecchio percorso
        Assert.Equal("https://vecchio.example.com:8443/", account.ServerUrl);
    }

    [Fact]
    public async Task La_finestra_principale_importa_il_file_passato_da_riga_di_comando_e_mostra_il_riepilogo()
    {
        using var data = new TempData();
        var ui = new NullShellUi();
        var vm = Seed.Main(data);
        vm.Ui = ui;
        vm.VpnProfilePath = Profile;
        vm.ConfigDataFolder = Data;
        vm.StartupConfigFile = WriteFile("e.rdpmconfig", RdpmConfig());

        vm.StartBlockB(ui, background: false);
        for (int i = 0; i < 200 && ui.ShownTexts.Count == 0; i++)
            await Task.Delay(50);

        var text = Assert.Single(ui.ShownTexts);
        Assert.Contains("Da completare", text);
        Assert.Null(vm.StartupConfigFile);
        Assert.Equal(ServerKey, VpnProfile.Load(Profile).ServerPublicKey);
        vm.ShutdownBlockB();
    }

    [Fact]
    public void La_riga_di_comando_trova_il_file_di_configurazione()
    {
        var path = WriteFile("f.rdpmconfig", RdpmConfig());
        Assert.Equal(path, RdpmConfigFile.FindArgument(["--altro", path]));
        Assert.Null(RdpmConfigFile.FindArgument([Path.Combine(_dir, "manca.rdpmconfig")]));
    }
}
