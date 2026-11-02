using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.Tests;

public class WireGuardKeysTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void X25519_Rfc7748_Vector1()
    {
        var r = WireGuardKeys.X25519(
            Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
            Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));
        Assert.Equal("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552", Convert.ToHexString(r).ToLowerInvariant());
    }

    [Fact]
    public void X25519_Rfc7748_Vector2()
    {
        var r = WireGuardKeys.X25519(
            Hex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
            Hex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"));
        Assert.Equal("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957", Convert.ToHexString(r).ToLowerInvariant());
    }

    [Fact]
    public void X25519_Rfc7748_DiffieHellman()
    {
        var alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPriv = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePub = Convert.FromBase64String(WireGuardKeys.PublicKeyFromPrivate(alicePriv));
        var bobPub = Convert.FromBase64String(WireGuardKeys.PublicKeyFromPrivate(bobPriv));
        Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", Convert.ToHexString(alicePub).ToLowerInvariant());
        Assert.Equal("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", Convert.ToHexString(bobPub).ToLowerInvariant());
        var k1 = WireGuardKeys.X25519(alicePriv, bobPub);
        var k2 = WireGuardKeys.X25519(bobPriv, alicePub);
        Assert.Equal(k1, k2);
        Assert.Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", Convert.ToHexString(k1).ToLowerInvariant());
    }

    [Fact]
    public void Generate_GivesValidDistinctKeysConsistentWithPublic()
    {
        var (priv, pub) = WireGuardKeys.Generate();
        var (priv2, _) = WireGuardKeys.Generate();
        Assert.True(WireGuardKeys.IsValidKey(priv));
        Assert.True(WireGuardKeys.IsValidKey(pub));
        Assert.NotEqual(priv, priv2);
        Assert.Equal(pub, WireGuardKeys.PublicKeyFromPrivate(priv));
        Assert.Equal(0, Convert.FromBase64String(priv)[0] & 7); // clamped come wg genkey
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // lunghezza sbagliata
    public void IsValidKey_RejectsBadKeys(string key) => Assert.False(WireGuardKeys.IsValidKey(key));
}

public class WireGuardConfigTests
{
    private static WireGuardConfig Sample()
    {
        var (priv, _) = WireGuardKeys.Generate();
        var (_, serverPub) = WireGuardKeys.Generate();
        return new WireGuardConfig { PrivateKey = priv, Address = "10.66.0.2/32", Dns = "10.66.0.1", PeerPublicKey = serverPub };
    }

    [Fact]
    public void Defaults_AreSynologyStar()
    {
        var c = Sample();
        Assert.Equal("syncnetpro.com:51820", c.Endpoint);
        Assert.Equal("10.66.0.0/24", c.AllowedIPs);
        Assert.Equal(25, c.PersistentKeepalive);
        Assert.True(c.IsValid);
    }

    [Fact]
    public void ConfRoundTrip()
    {
        var c = Sample();
        Assert.Equal(c, WireGuardConfig.Parse(c.ToConf()));
        var noDns = c with { Dns = "" };
        Assert.DoesNotContain("DNS", noDns.ToConf());
        Assert.Equal(noDns, WireGuardConfig.Parse(noDns.ToConf()));
    }

    [Fact]
    public void Parse_AcceptsCommentsCaseAndUnknownKeys()
    {
        var c = Sample();
        var text = $"# commento\n[interface]\nprivatekey={c.PrivateKey}\nADDRESS = 10.66.0.2/32 # il mio\nMTU=1380\n\n[PEER]\nPublicKey = {c.PeerPublicKey}\nEndpoint = syncnetpro.com:51820\nAllowedIPs = 10.66.0.0/24\nPersistentKeepalive = 25\n";
        var p = WireGuardConfig.Parse(text);
        Assert.Equal(c with { Dns = "" }, p);
    }

    [Theory]
    [InlineData("PrivateKey = x")]                            // riga fuori da una sezione
    [InlineData("[Interface]\nPrivateKey")]                   // senza =
    [InlineData("[Interface]\nAddress = 10.0.0.2/32")]        // manca il Peer
    [InlineData("[Strano]\nA = b")]
    [InlineData("[Peer]\n[Peer]")]                            // due peer
    [InlineData("[Peer]\nPersistentKeepalive = tanto")]
    public void Parse_RejectsMalformed(string text) => Assert.Throws<FormatException>(() => WireGuardConfig.Parse(text));

    [Fact]
    public void Validate_ReportsEachProblem()
    {
        var bad = new WireGuardConfig { PrivateKey = "no", Address = "10.66.0.2", Dns = "pippo", PeerPublicKey = "no", Endpoint = "senzaporta", AllowedIPs = "boh", PersistentKeepalive = -1 };
        Assert.Equal(7, bad.Validate().Count);
        Assert.False(bad.IsValid);
        Assert.NotEmpty((Sample() with { Endpoint = "host:70000" }).Validate());
        Assert.Empty((Sample() with { Endpoint = "203.0.113.5:51820" }).Validate());
    }

    [Fact]
    public void ServerPeerFragment_HasPublicKeyAndHost32()
    {
        var c = Sample() with { Address = "10.66.0.7/24" };
        var pub = WireGuardKeys.PublicKeyFromPrivate(c.PrivateKey);
        var f = c.ToServerPeerFragment("Ufficio\nX");
        Assert.Equal($"[Peer]\n# UfficioX\nPublicKey = {pub}\nAllowedIPs = 10.66.0.7/32\n", f);
        Assert.DoesNotContain(c.PrivateKey, f);
        Assert.DoesNotContain("# ", c.ToServerPeerFragment().Replace("[Peer]\n", ""));
    }

    [Fact]
    public void NextFreeAddress_SkipsUsed()
    {
        Assert.Equal("10.66.0.2/32", WireGuardConfig.NextFreeAddress([]));
        Assert.Equal("10.66.0.4/32", WireGuardConfig.NextFreeAddress(["10.66.0.2/32", "10.66.0.3"]));
        Assert.Null(WireGuardConfig.NextFreeAddress(Enumerable.Range(2, 253).Select(n => $"10.66.0.{n}")));
    }
}

public class VpnProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void PrivateKey_IsEncryptedOnDisk_AndRestored()
    {
        var path = Path.Combine(_dir, "vpn.json");
        var p = new VpnProfile { Address = "10.66.0.2/32", PcName = "Mio" };
        var pub = p.GenerateKeys();
        var (_, serverPub) = WireGuardKeys.Generate();
        p.ServerPublicKey = serverPub;
        p.Save(path);

        var plainPriv = p.ToConfig().PrivateKey;
        Assert.DoesNotContain(plainPriv, File.ReadAllText(path));

        var loaded = VpnProfile.Load(path);
        Assert.Equal(pub, loaded.PublicKey);
        Assert.Equal(plainPriv, loaded.ToConfig().PrivateKey);
        Assert.True(loaded.ToConfig().IsValid);
        Assert.Contains(pub, loaded.ServerPeerFragment());
    }

    [Fact]
    public void Load_MissingOrCorrupt_GivesEmptyProfile()
    {
        Assert.False(VpnProfile.Load(Path.Combine(_dir, "no.json")).HasKey);
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ non json");
        Assert.False(VpnProfile.Load(bad).HasKey);
        Assert.Null(new VpnProfile { PrivateKeyProtected = "xxxx" }.PublicKey);
    }
}

public class WireGuardTunnelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpnt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private sealed class FakeServices : IServiceManager
    {
        public TunnelState State = TunnelState.Spento;
        public bool Present;
        public bool StartFails;
        public List<string> Calls = [];
        public string? BinPath;

        public TunnelState Query(string name) => Present ? State : TunnelState.Spento;
        public bool Exists(string name) => Present;
        public void Create(string name, string display, string bin) { Calls.Add("create"); Present = true; BinPath = bin; }
        public void Start(string name) { Calls.Add("start"); State = StartFails ? TunnelState.Errore : TunnelState.Acceso; }
        public void Stop(string name) { Calls.Add("stop"); State = TunnelState.Spento; }
        public void Delete(string name) { Calls.Add("delete"); Present = false; }
    }

    /// <summary>Esegue l'assistente nello stesso processo, come se fosse elevato.</summary>
    private sealed class InProcessElevator(FakeServices svc, string programData, int? forcedCode = null) : IVpnElevator
    {
        public List<string[]> Runs = [];

        public int RunHelper(IReadOnlyList<string> args)
        {
            Runs.Add([.. args]);
            if (forcedCode is { } c)
                return c;
            var helper = new VpnHelper(svc, programData, @"C:\app\RdpManager.exe") { Sleep = _ => { }, Restrict = _ => { } };
            return helper.Run([.. args]);
        }
    }

    private static WireGuardConfig Config()
    {
        var (priv, _) = WireGuardKeys.Generate();
        var (_, serverPub) = WireGuardKeys.Generate();
        return new WireGuardConfig { PrivateKey = priv, Address = "10.66.0.2/32", PeerPublicKey = serverPub };
    }

    private (WireGuardNtTunnel tunnel, FakeServices svc, InProcessElevator el) Make(int? code = null)
    {
        var svc = new FakeServices();
        var el = new InProcessElevator(svc, Path.Combine(_dir, "pd"), code);
        return (new WireGuardNtTunnel(el, svc, Path.Combine(_dir, "stage")), svc, el);
    }

    [Fact]
    public void Accendi_RegistersStartsAndLeavesNoPlainKeyOnDisk()
    {
        var (t, svc, el) = Make();
        Assert.Equal(TunnelState.Spento, t.Stato);
        t.Accendi(Config());
        Assert.Equal(TunnelState.Acceso, t.Stato);
        Assert.Equal(["create", "start"], svc.Calls);
        Assert.Contains("--wg-service", svc.BinPath);
        Assert.Empty(Directory.GetFiles(_dir, "*.conf", SearchOption.AllDirectories));
        Assert.Equal(["--vpn-helper", "su"], el.Runs[0][..2]);
    }

    [Fact]
    public void Accendi_WhenAlreadyOn_DoesNothing()
    {
        var (t, svc, el) = Make();
        t.Accendi(Config());
        t.Accendi(Config());
        Assert.Single(el.Runs);
    }

    [Fact]
    public void Accendi_InvalidConfig_ThrowsWithoutElevating()
    {
        var (t, _, el) = Make();
        Assert.Throws<ArgumentException>(() => t.Accendi(new WireGuardConfig()));
        Assert.Empty(el.Runs);
    }

    [Fact]
    public void Accendi_ServiceFails_RemovesServiceAndThrows()
    {
        var (t, svc, _) = Make();
        svc.StartFails = true;
        var ex = Assert.Throws<VpnException>(() => t.Accendi(Config()));
        Assert.Contains("codice 2", ex.Message);
        Assert.False(svc.Present);
        Assert.Empty(Directory.GetFiles(_dir, "*.conf", SearchOption.AllDirectories));
    }

    [Fact]
    public void Accendi_UacCancelled_GivesClearMessageAndCleansUp()
    {
        var (t, _, _) = Make(WireGuardNtTunnel.ExitCancelled);
        var ex = Assert.Throws<VpnException>(() => t.Accendi(Config()));
        Assert.Contains("amministratore", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.conf", SearchOption.AllDirectories));
    }

    [Fact]
    public void Spegni_StopsAndRemoves_AndIsIdempotent()
    {
        var (t, svc, el) = Make();
        t.Spegni();
        Assert.Empty(el.Runs); // già spento: niente UAC
        t.Accendi(Config());
        t.Spegni();
        Assert.Equal(TunnelState.Spento, t.Stato);
        Assert.False(svc.Present);
        Assert.Equal(["create", "start", "stop", "delete"], svc.Calls);
    }

    [Fact]
    public void Helper_BadArguments_Rejected()
    {
        var svc = new FakeServices();
        var h = new VpnHelper(svc, _dir, "x.exe") { Sleep = _ => { }, Restrict = _ => { } };
        Assert.Equal(WireGuardNtTunnel.ExitBadArguments, h.Run(["--vpn-helper"]));
        Assert.Equal(WireGuardNtTunnel.ExitBadArguments, h.Run(["--vpn-helper", "boh"]));
        Assert.Equal(WireGuardNtTunnel.ExitBadArguments, h.Run(["--vpn-helper", "su"]));
        Assert.Equal(WireGuardNtTunnel.ExitFailed, h.Run(["--vpn-helper", "su", Path.Combine(_dir, "manca.conf")]));
        Assert.Empty(svc.Calls);
    }
}
