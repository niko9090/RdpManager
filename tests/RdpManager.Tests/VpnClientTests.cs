using System.Net;
using RdpManager.Core;
using RdpManager.Vpn;
using RdpManagerVpn;

namespace RdpManager.Tests;

/// <summary>Client VPN per i PC dei clienti (src\RdpManagerVpn): profilo, impostazioni, avvio automatico, servizio.</summary>
public class VpnClientProfileTests
{
    internal static readonly string Priv = WireGuardKeys.Generate().PrivateKey;
    internal static readonly string Pub = WireGuardKeys.Generate().PublicKey;

    internal static string Conf(string extraPeer = "AllowedIPs = 10.66.0.0/24\nPersistentKeepalive = 25\n") =>
        $"[Interface]\nPrivateKey = {Priv}\nAddress = 10.66.0.7/32\n\n[Peer]\nPublicKey = {Pub}\nEndpoint = 203.0.113.5:51820\n{extraPeer}";

    [Fact]
    public void VpnClient_ProfiloCompleto_EValido()
    {
        var r = ClientProfile.Check(Conf());
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal("10.66.0.7", r.IpAddress);
        Assert.Equal("10.66.0.0/24", r.Config!.AllowedIPs);
    }

    [Fact]
    public void VpnClient_ProfiloCreatoDalloScriptDelServer_EValido()
    {
        // come crea-profilo-client.sh: commenti, DNS assente, righe con CRLF
        var r = ClientProfile.Check("# profilo di prova\r\n" + Conf().Replace("\n", "\r\n"));
        Assert.True(r.Ok, r.ErrorText);
    }

    [Fact]
    public void VpnClient_SenzaAllowedIpsEKeepalive_UsaIValoriDellaVpn()
    {
        var r = ClientProfile.Check(Conf(extraPeer: ""));
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal(VpnDefaults.Network, r.Config!.AllowedIPs);
        Assert.Equal(VpnDefaults.Keepalive, r.Config.PersistentKeepalive);
    }

    [Theory]
    [InlineData("PrivateKey")]
    [InlineData("Address")]
    [InlineData("PublicKey")]
    [InlineData("Endpoint")]
    public void VpnClient_CampoMancante_DiceQualeManca(string campo)
    {
        var righe = Conf().Split('\n').Where(l => !l.StartsWith(campo + " ")).ToArray();
        var r = ClientProfile.Check(string.Join('\n', righe));
        Assert.False(r.Ok);
        Assert.Contains(campo, r.ErrorText);
        Assert.Contains("mancano", r.ErrorText);
    }

    [Fact]
    public void VpnClient_SenzaSezionePeer_Errore()
    {
        var r = ClientProfile.Check($"[Interface]\nPrivateKey = {Priv}\nAddress = 10.66.0.7/32\n");
        Assert.False(r.Ok);
        Assert.Contains("[Peer]", r.ErrorText);
    }

    [Fact]
    public void VpnClient_ChiaveNonValida_ErroreDiValidazione()
    {
        var r = ClientProfile.Check(Conf().Replace(Priv, "non-una-chiave"));
        Assert.False(r.Ok);
        Assert.Contains("chiave privata", r.ErrorText);
    }

    [Fact]
    public void VpnClient_TestoSenzaSenso_NonLanciaEccezioni()
    {
        var r = ClientProfile.Check("questo non è un file conf");
        Assert.False(r.Ok);
        Assert.Contains(".conf", r.ErrorText);
    }

    [Fact]
    public void VpnClient_Load_PrendeIlPrimoFileCheEsiste()
    {
        var files = new Dictionary<string, string> { ["B/vpn.conf"] = Conf() };
        var r = ClientProfile.Load(["A/vpn.conf", "B/vpn.conf"], p => files.GetValueOrDefault(p));
        Assert.True(r.Ok);
        Assert.Equal("B/vpn.conf", r.Path);
    }

    [Fact]
    public void VpnClient_Load_AccantoAllExeHaLaPrecedenza()
    {
        var files = new Dictionary<string, string> { ["A/vpn.conf"] = Conf(), ["B/vpn.conf"] = "rotto" };
        var r = ClientProfile.Load(["A/vpn.conf", "B/vpn.conf"], p => files.GetValueOrDefault(p));
        Assert.True(r.Ok);
        Assert.Equal("A/vpn.conf", r.Path);
    }

    [Fact]
    public void VpnClient_Load_NessunFile_DiceDoveHaCercato()
    {
        var r = ClientProfile.Load(["A/vpn.conf", "B/vpn.conf"], _ => null);
        Assert.False(r.Ok);
        Assert.Contains("non trovato", r.ErrorText);
        Assert.Contains("B/vpn.conf", r.ErrorText);
    }

    [Fact]
    public void VpnClient_Load_FileNonLeggibile_Errore()
    {
        var r = ClientProfile.Load(["A/vpn.conf"], _ => throw new UnauthorizedAccessException("negato"));
        Assert.False(r.Ok);
        Assert.Contains("non si riesce a leggere", r.ErrorText);
    }

    [Fact]
    public void VpnClient_PercorsiDelProfilo_ExeEPoiProgramData()
    {
        var c = ClientPaths.ProfileCandidates(@"C:\App", @"C:\PD");
        Assert.Equal(Path.Combine(@"C:\App", "vpn.conf"), c[0]);
        Assert.Equal(Path.Combine(@"C:\PD", "RdpManagerVPN", "vpn.conf"), c[1]);
    }
}

public class VpnClientSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpnclient-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void VpnClient_Impostazioni_PredefinitePerFileAssente()
    {
        var s = ClientSettings.Load(Path.Combine(_dir, "manca.json"));
        Assert.True(s.AvviaConWindows);
        Assert.True(s.RiconnettiDaSolo);
    }

    [Fact]
    public void VpnClient_Impostazioni_SalvaERilegge()
    {
        var path = Path.Combine(_dir, "sotto", "impostazioni.json");
        new ClientSettings { AvviaConWindows = false, RiconnettiDaSolo = false }.Save(path);
        var s = ClientSettings.Load(path);
        Assert.False(s.AvviaConWindows);
        Assert.False(s.RiconnettiDaSolo);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void VpnClient_Impostazioni_JsonSemplice()
    {
        var path = Path.Combine(_dir, "impostazioni.json");
        new ClientSettings { RiconnettiDaSolo = false }.Save(path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"AvviaConWindows\": true", text);
        Assert.Contains("\"RiconnettiDaSolo\": false", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ non json")]
    [InlineData("null")]
    public void VpnClient_Impostazioni_FileRovinato_TornaAiValoriPredefiniti(string contenuto)
    {
        var s = ClientSettings.Load("x.json", _ => contenuto);
        Assert.Equal(new ClientSettings(), s);
    }

    [Fact]
    public void VpnClient_Impostazioni_CampoMancante_HaIlValorePredefinito()
    {
        var s = ClientSettings.Load("x.json", _ => "{ \"RiconnettiDaSolo\": false }");
        Assert.True(s.AvviaConWindows);
        Assert.False(s.RiconnettiDaSolo);
    }
}

public class VpnClientAutoStartTests
{
    private sealed class FakeRunKey : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string command) => Values[name] = command;
        public void Remove(string name) => Values.Remove(name);
    }

    [Fact]
    public void VpnClient_AvvioAutomatico_AttivaScriveIlComandoConIPercorsiTraVirgolette()
    {
        var store = new FakeRunKey();
        var m = new AutoStartManager(store, @"C:\Program Files\RdpManager VPN\RdpManagerVpn.exe");
        Assert.False(m.IsEnabled);
        m.Apply(true);
        Assert.True(m.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\RdpManager VPN\\RdpManagerVpn.exe\"", store.Values[AutoStartManager.ValueName]);
    }

    [Fact]
    public void VpnClient_AvvioAutomatico_DisattivaRimuoveLaVoce()
    {
        var store = new FakeRunKey();
        var m = new AutoStartManager(store, @"C:\App\RdpManagerVpn.exe");
        m.Apply(true);
        m.Apply(false);
        Assert.False(m.IsEnabled);
        Assert.Empty(store.Values);
        m.Apply(false); // già tolta: nessun errore
    }

    [Fact]
    public void VpnClient_AvvioAutomatico_ProgrammaSpostato_LaVoceSiCorregge()
    {
        var store = new FakeRunKey();
        new AutoStartManager(store, @"C:\Vecchio\RdpManagerVpn.exe").Apply(true);
        var nuovo = new AutoStartManager(store, @"C:\Nuovo\RdpManagerVpn.exe");
        Assert.True(nuovo.IsStale);
        nuovo.Apply(true);
        Assert.False(nuovo.IsStale);
    }
}

public class VpnClientServiceTests
{
    private sealed class RecordingRunner(Func<string[], int>? respond = null) : ICommandRunner
    {
        public List<string> Calls { get; } = [];
        public int Run(string file, params string[] args)
        {
            Calls.Add(string.Join(' ', args));
            return respond?.Invoke(args) ?? 0;
        }
    }

    [Fact]
    public void VpnClient_Installazione_CreaServizioAutomaticoESystem()
    {
        var runner = new RecordingRunner(a => a[0] == "query" ? 1060 : 0);
        new ClientServiceInstaller(runner).Install(@"C:\App\RdpManagerVpn.exe");
        Assert.StartsWith("create RdpManagerVpnClient binPath= \"C:\\App\\RdpManagerVpn.exe\" --servizio start= auto", runner.Calls[1]);
        Assert.Contains(runner.Calls, c => c.StartsWith("failure RdpManagerVpnClient"));
        Assert.Contains(runner.Calls, c => c.StartsWith("sdset RdpManagerVpnClient"));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("obj="));
    }

    [Fact]
    public void VpnClient_Installazione_GiaPresente_AggiornaConConfig()
    {
        var runner = new RecordingRunner();
        new ClientServiceInstaller(runner).Install(@"C:\App\RdpManagerVpn.exe");
        Assert.StartsWith("config RdpManagerVpnClient", runner.Calls[1]);
    }

    [Fact]
    public void VpnClient_Installazione_ScFallisce_Eccezione()
    {
        var runner = new RecordingRunner(a => a[0] == "query" ? 1060 : 5);
        Assert.Throws<InvalidOperationException>(() => new ClientServiceInstaller(runner).Install(@"C:\App\x.exe"));
    }

    [Fact]
    public void VpnClient_Rimozione_FermaECancella()
    {
        var runner = new RecordingRunner();
        new ClientServiceInstaller(runner).Uninstall();
        Assert.Equal(["query RdpManagerVpnClient", "stop RdpManagerVpnClient", "delete RdpManagerVpnClient"], runner.Calls);
    }

    [Fact]
    public void VpnClient_Rimozione_ServizioAssente_NonFaNulla()
    {
        var runner = new RecordingRunner(a => 1060);
        new ClientServiceInstaller(runner).Uninstall();
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void VpnClient_Policy_AvvioDiWindows_RispettaLImpostazione()
    {
        Assert.True(ServicePolicy.ShouldConnectAtStart(new ClientSettings { AvviaConWindows = true }, manualStart: false));
        Assert.False(ServicePolicy.ShouldConnectAtStart(new ClientSettings { AvviaConWindows = false }, manualStart: false));
        Assert.True(ServicePolicy.ShouldConnectAtStart(new ClientSettings { AvviaConWindows = false }, manualStart: true));
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static VpnStatus Status(DateTime? hs) => new(hs, 0, 0, T0);

    [Fact]
    public void VpnClient_Policy_Riconnessione()
    {
        var on = new ClientSettings { RiconnettiDaSolo = true };
        var off = new ClientSettings { RiconnettiDaSolo = false };
        Assert.True(ServicePolicy.NeedsReconnect(on, null, T0, T0));                                  // adattatore sparito
        Assert.False(ServicePolicy.NeedsReconnect(on, Status(T0), T0, T0.AddMinutes(4)));              // handshake recente
        Assert.True(ServicePolicy.NeedsReconnect(on, Status(T0), T0, T0.AddMinutes(6)));               // handshake vecchio
        Assert.False(ServicePolicy.NeedsReconnect(on, Status(null), T0, T0.AddSeconds(60)));           // ancora in attesa del primo
        Assert.True(ServicePolicy.NeedsReconnect(on, Status(null), T0, T0.AddSeconds(150)));           // il server non risponde
        Assert.False(ServicePolicy.NeedsReconnect(off, null, T0, T0.AddHours(1)));                     // disattivata
    }

    [Fact]
    public void VpnClient_Testi_StatoInItaliano()
    {
        Assert.Equal("Non connesso", ClientStatusText.Describe(TunnelState.Spento, null, T0));
        Assert.Equal("Connesso", ClientStatusText.Describe(TunnelState.Acceso, Status(T0), T0.AddSeconds(30)));
        Assert.Equal("Server non raggiungibile", ClientStatusText.Describe(TunnelState.Acceso, Status(T0), T0.AddMinutes(10)));
        Assert.Equal("Connessione in corso…", ClientStatusText.Describe(TunnelState.Acceso, null, T0));
        Assert.Equal("Servizio non installato", ClientStatusText.Describe(TunnelState.Spento, null, T0, serviceInstalled: false));
        Assert.Equal("—", ClientStatusText.Address(""));
    }
}

public class VpnClientSupervisorTests
{
    private sealed class FakeAdapter(byte[]? config) : INtAdapter
    {
        public bool Disposed;
        public void SetConfiguration(byte[] block) { }
        public void AddAddress(IPAddress ip, int prefix) { }
        public void AddRoute(IPAddress network, int prefix) { }
        public void Up() { }
        public byte[]? GetConfiguration() => config;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDriver(Func<FakeAdapter> make) : INtDriver
    {
        public int Created;
        public INtAdapter Create(string name, string tunnelType)
        {
            Created++;
            return make();
        }
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ClientProfileResult Good() => ClientProfile.Check(VpnClientProfileTests.Conf());

    [Fact]
    public void VpnClient_Supervisore_AccendeConIlProfilo()
    {
        var cfg = Good().Config!;
        var block = WireGuardNtStructs.BuildConfiguration(cfg, IPAddress.Parse("203.0.113.5"));
        var driver = new FakeDriver(() => new FakeAdapter(block));
        var t = T0;
        var sup = new ClientSupervisor(new VpnTunnelRunner(driver, _ => IPAddress.Loopback), Good, () => new ClientSettings(), () => t, _ => { });
        Assert.True(sup.Tick());
        Assert.True(sup.Connected);
        t = t.AddSeconds(2);
        Assert.True(sup.Tick());
        Assert.Equal(1, driver.Created);
    }

    [Fact]
    public void VpnClient_Supervisore_AdattatoreCaduto_RiconnetteSeAttivo()
    {
        var driver = new FakeDriver(() => new FakeAdapter(null)); // GetConfiguration null = adattatore morto
        var t = T0;
        var sup = new ClientSupervisor(new VpnTunnelRunner(driver, _ => IPAddress.Loopback), Good, () => new ClientSettings(), () => t, _ => { });
        sup.Tick();                // accende
        t = t.AddSeconds(2);
        sup.Tick();                // lo trova morto e lo spegne
        Assert.False(sup.Connected);
        t = t.AddSeconds(2);
        sup.Tick();                // lo riaccende
        Assert.True(sup.Connected);
        Assert.Equal(2, driver.Created);
    }

    [Fact]
    public void VpnClient_Supervisore_AdattatoreCaduto_SenzaRiconnessioneRestaAcceso()
    {
        var driver = new FakeDriver(() => new FakeAdapter(null));
        var t = T0;
        var sup = new ClientSupervisor(new VpnTunnelRunner(driver, _ => IPAddress.Loopback), Good,
            () => new ClientSettings { RiconnettiDaSolo = false }, () => t, _ => { });
        sup.Tick();
        t = t.AddSeconds(2);
        sup.Tick();
        Assert.True(sup.Connected);
        Assert.Equal(1, driver.Created);
    }

    [Fact]
    public void VpnClient_Supervisore_ProfiloAssente_RiprovaSeAttivoAltrimentiSiArrende()
    {
        var driver = new FakeDriver(() => new FakeAdapter(null));
        var bad = ClientProfile.Load(["x"], _ => null);
        var log = new List<string>();
        var t = T0;
        var retry = new ClientSupervisor(new VpnTunnelRunner(driver), () => bad, () => new ClientSettings(), () => t, log.Add);
        Assert.True(retry.Tick());
        Assert.Contains(log, l => l.Contains("non trovato"));
        var giveUp = new ClientSupervisor(new VpnTunnelRunner(driver), () => bad, () => new ClientSettings { RiconnettiDaSolo = false }, () => t, _ => { });
        Assert.False(giveUp.Tick());
        Assert.Equal(0, driver.Created);
    }

    [Fact]
    public void VpnClient_Supervisore_AccensioneFallita_AspettaPrimaDiRiprovare()
    {
        int attempts = 0;
        var driver = new FakeDriver(() => { attempts++; throw new InvalidOperationException("rete non pronta"); });
        var t = T0;
        var sup = new ClientSupervisor(new VpnTunnelRunner(driver, _ => IPAddress.Loopback), Good, () => new ClientSettings(), () => t, _ => { });
        Assert.True(sup.Tick());
        t = t.AddSeconds(2);
        Assert.True(sup.Tick());            // ancora in attesa: nessun nuovo tentativo
        Assert.Equal(1, attempts);
        t = t.Add(ServicePolicy.RetryDelay);
        sup.Tick();
        Assert.Equal(2, attempts);
    }
}

/// <summary>Il profilo finto usato per provare build-vpn-client.ps1 deve essere accettato dal client.</summary>
public class VpnClientFixtureTests
{
    [Fact]
    public void VpnClient_ProfiloDiEsempioDelleFixture_EValido()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "vpn-esempio.conf")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var r = ClientProfile.Load([Path.Combine(dir!.FullName, "tests", "fixtures", "vpn-esempio.conf")]);
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal("10.66.0.50", r.IpAddress);
    }
}
