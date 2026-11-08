using System.Security.Cryptography;
using System.Text;
using RdpManager.Core;
using RdpManager.Platform.Linux;

namespace RdpManager.Tests;

public sealed class LinuxPlatformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-linux-" + Guid.NewGuid().ToString("N"));

    public LinuxPlatformTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public List<string?> Stdin { get; } = [];
        public Func<string, ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, "", "");
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default)
        {
            var line = fileName + " " + string.Join(' ', arguments);
            Calls.Add(line);
            Stdin.Add(stdin);
            return Task.FromResult(Handler(line));
        }
    }

    // ---- XDG ----

    [Fact]
    public void Xdg_Linux_UsaLeVariabili()
    {
        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/cfg", ["XDG_DATA_HOME"] = "/dat", ["HOME"] = "/home/u" };
        Assert.Equal(Path.Combine("/cfg", "RdpManager"), AppPaths.ResolveConfig(false, k => env.GetValueOrDefault(k), "ignorato"));
        Assert.Equal(Path.Combine("/dat", "RdpManager"), AppPaths.ResolveData(false, k => env.GetValueOrDefault(k), "ignorato"));
    }

    [Fact]
    public void Xdg_Linux_SenzaVariabiliUsaHome()
    {
        var env = new Dictionary<string, string> { ["HOME"] = "/home/u" };
        Assert.Equal(Path.Combine("/home/u", ".config", "RdpManager"), AppPaths.ResolveConfig(false, k => env.GetValueOrDefault(k), ""));
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "RdpManager"), AppPaths.ResolveData(false, k => env.GetValueOrDefault(k), ""));
    }

    [Fact]
    public void Xdg_ValoreVuotoONonAssolutoSiIgnora()
    {
        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "relativo", ["XDG_DATA_HOME"] = "  ", ["HOME"] = "/h" };
        Assert.Equal(Path.Combine("/h", ".config", "RdpManager"), AppPaths.ResolveConfig(false, k => env.GetValueOrDefault(k), ""));
        Assert.Equal(Path.Combine("/h", ".local", "share", "RdpManager"), AppPaths.ResolveData(false, k => env.GetValueOrDefault(k), ""));
    }

    [Fact]
    public void Windows_ResaInvariata()
    {
        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/cfg" };
        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Roaming", "RdpManager"),
            AppPaths.ResolveConfig(true, k => env.GetValueOrDefault(k), @"C:\Users\x\AppData\Roaming"));
        Assert.Equal(Path.Combine(@"C:\L", "RdpManager"), AppPaths.ResolveData(true, _ => null, @"C:\L"));
    }

    [Fact]
    public void Windows_PercorsiPredefinitiComeUnaVolta()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager", "connections.json"), ConnectionStore.DefaultPath);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager", "vpn.json"), VpnProfile.DefaultPath);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "logs"), AppLog.DefaultDirectory);
    }

    // ---- AES-GCM e chiave su file ----

    private AesGcmSecretStore FileStore() => new(new FileMasterKeyProvider(Path.Combine(_dir, "k", "secret.key")));

    [Fact]
    public void AesGcm_Roundtrip_ConEntropia()
    {
        var s = FileStore();
        var ent = Encoding.UTF8.GetBytes("e");
        var data = Encoding.UTF8.GetBytes("password segreta");
        var enc = s.Protect(data, ent);
        Assert.DoesNotContain("segreta", Encoding.UTF8.GetString(enc));
        Assert.Equal(data, s.Unprotect(enc, ent));
        Assert.Equal(data, s.Unprotect(s.Protect(data, null), null));
    }

    [Fact]
    public void AesGcm_EntropiaDiversaOCorrottoLanciaCryptographicException()
    {
        var s = FileStore();
        var enc = s.Protect([1, 2, 3], [9]);
        Assert.ThrowsAny<CryptographicException>(() => s.Unprotect(enc, [8]));
        enc[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => s.Unprotect(enc, [9]));
        Assert.ThrowsAny<CryptographicException>(() => s.Unprotect([1, 2], null));
    }

    [Fact]
    public void AesGcm_CifraturaDiversaOgniVolta_ChiavePersistente()
    {
        var path = Path.Combine(_dir, "persist.key");
        var a = new AesGcmSecretStore(new FileMasterKeyProvider(path));
        var enc = a.Protect([7, 7], null);
        Assert.NotEqual(enc, a.Protect([7, 7], null));
        var b = new AesGcmSecretStore(new FileMasterKeyProvider(path));
        Assert.Equal(new byte[] { 7, 7 }, b.Unprotect(enc, null));
        Assert.Equal(32, File.ReadAllBytes(path).Length);
    }

    [Fact]
    public void AesGcm_ConPasswordProtector_FormatoBase64()
    {
        var old = SecretStore.IsConfigured ? SecretStore.Current : null;
        try
        {
            SecretStore.Current = FileStore();
            var enc = PasswordProtector.Protect("ciao");
            Assert.Equal("ciao", PasswordProtector.Unprotect(enc));
            Assert.True(PasswordProtector.TryUnprotect(enc, out var p) && p == "ciao");
        }
        finally { if (old is not null) SecretStore.Current = old; }
    }

    [Fact]
    public void ChiaveSuFile_CorrottaVieneRifiutata_E_PermessiSuUnix()
    {
        var path = Path.Combine(_dir, "bad.key");
        File.WriteAllBytes(path, [1, 2, 3]);
        var p = new FileMasterKeyProvider(path);
        Assert.Null(p.TryRead());
        var key = p.GetOrCreateKey();
        Assert.Equal(32, key.Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // ---- Secret Service (secret-tool finto) ----

    [Fact]
    public void SecretTool_ChiaveNelPortachiavi_NienteFile()
    {
        string? stored = null;
        var runner = new FakeRunner();
        runner.Handler = line => line.StartsWith("secret-tool lookup")
            ? (stored is null ? new ProcessResult(1, "", "") : new ProcessResult(0, stored, ""))
            : new ProcessResult(0, "", "");
        var keyFile = Path.Combine(_dir, "fb.key");
        var capture = new FakeRunner { Handler = runner.Handler };
        var provider = new FallbackMasterKeyProvider(new SecretToolMasterKeyProvider(new CapturingRunner(capture, v => stored = v)), new FileMasterKeyProvider(keyFile));
        var key = provider.GetOrCreateKey();
        Assert.Equal("keyring", provider.Source);
        Assert.Equal(Convert.ToBase64String(key), stored);
        Assert.False(File.Exists(keyFile));
    }

    private sealed class CapturingRunner(FakeRunner inner, Action<string> onStore) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default)
        {
            if (arguments.Count > 0 && arguments[0] == "store" && stdin is not null)
                onStore(stdin);
            return await inner.RunAsync(fileName, arguments, stdin, ct);
        }
    }

    [Fact]
    public void SecretTool_NonDisponibile_UsaFile_EPoiMigraNelPortachiavi()
    {
        var keyFile = Path.Combine(_dir, "mig.key");
        var down = new FakeRunner { Handler = _ => new ProcessResult(127, "", "secret-tool: not found") };
        var p1 = new FallbackMasterKeyProvider(new SecretToolMasterKeyProvider(down), new FileMasterKeyProvider(keyFile));
        var key = p1.GetOrCreateKey();
        Assert.Equal("file", p1.Source);
        Assert.Equal(key, File.ReadAllBytes(keyFile));

        // Più tardi il portachiavi c'è ma è vuoto: la chiave su file passa lì invariata.
        string? stored = null;
        var up = new FakeRunner();
        up.Handler = line => new ProcessResult(line.StartsWith("secret-tool lookup") ? 1 : 0, "", "");
        var p2 = new FallbackMasterKeyProvider(new SecretToolMasterKeyProvider(new CapturingRunner(up, v => stored = v)), new FileMasterKeyProvider(keyFile));
        Assert.Equal(key, p2.GetOrCreateKey());
        Assert.Equal("keyring", p2.Source);
        Assert.Equal(Convert.ToBase64String(key), stored);
    }

    [Fact]
    public void SecretTool_Bloccato_NonSovrascriveLaVoce()
    {
        var r = new FakeRunner { Handler = line => line.Contains("lookup") ? new ProcessResult(1, "", "Cannot autolaunch D-Bus") : new ProcessResult(0, "", "") };
        var p = new FallbackMasterKeyProvider(new SecretToolMasterKeyProvider(r), new FileMasterKeyProvider(Path.Combine(_dir, "l.key")));
        p.GetOrCreateKey();
        Assert.Equal("file", p.Source);
        Assert.DoesNotContain(r.Calls, c => c.Contains(" store "));
    }

    [Fact]
    public void SecretTool_ChiaveEsistenteLetta()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var r = new FakeRunner { Handler = _ => new ProcessResult(0, Convert.ToBase64String(key) + "\n", "") };
        var p = new SecretToolMasterKeyProvider(r);
        Assert.Equal(key, p.GetOrCreateKey());
        Assert.Equal("secret-tool lookup application RdpManager kind master-key", r.Calls[0]);
    }

    [Fact]
    public void Register_NonSostituisceUnCustodeGiaRegistrato()
    {
        var before = SecretStore.Current;
        LinuxPlatformInit.Register(new FakeRunner());
        Assert.Same(before, SecretStore.Current);
    }

    // ---- WireGuard ----

    private static WireGuardConfig SampleConfig()
    {
        var k1 = WireGuardKeys.Generate().PrivateKey;
        var k2 = WireGuardKeys.Generate().PublicKey;
        return new WireGuardConfig { PrivateKey = k1, Address = "10.66.0.2/32", PeerPublicKey = k2, Endpoint = "vpn.example.com:51820" };
    }

    [Fact]
    public void Comandi_Nmcli()
    {
        Assert.Equal("nmcli connection import type wireguard file /x/RdpManager.conf", LinuxWireGuardCommands.NmImport("/x/RdpManager.conf").ToString());
        Assert.Equal("nmcli connection up RdpManager", LinuxWireGuardCommands.NmUp().ToString());
        Assert.Equal("nmcli connection down RdpManager", LinuxWireGuardCommands.NmDown().ToString());
        Assert.Equal("nmcli connection delete RdpManager", LinuxWireGuardCommands.NmDelete().ToString());
    }

    [Fact]
    public void Comandi_WgQuickConPkexec()
    {
        Assert.Equal("pkexec wg-quick up /x/RdpManager.conf", LinuxWireGuardCommands.WgQuickUp("/x/RdpManager.conf").ToString());
        Assert.Equal("pkexec wg-quick down /x/RdpManager.conf", LinuxWireGuardCommands.WgQuickDown("/x/RdpManager.conf").ToString());
    }

    [Fact]
    public void Stato_DaNmcli()
    {
        Assert.True(LinuxWireGuardCommands.IsActiveInNmcli("Wired:802-3-ethernet:eth0\nRdpManager:wireguard:RdpManager\n"));
        Assert.False(LinuxWireGuardCommands.IsActiveInNmcli("Wired:802-3-ethernet:eth0\n"));
        Assert.False(LinuxWireGuardCommands.IsActiveInNmcli("RdpManager:vpn:\n"));
        Assert.False(LinuxWireGuardCommands.IsActiveInNmcli(""));
    }

    [Fact]
    public async Task Connetti_NetworkManager_SequenzaEFileConf()
    {
        var conf = Path.Combine(_dir, "vpn", "RdpManager.conf");
        var r = new FakeRunner();
        var wg = new LinuxWireGuard(r, conf, LinuxVpnBackend.NetworkManager);
        var cfg = SampleConfig();
        var res = await wg.ConnectAsync(cfg);
        Assert.True(res.Success);
        Assert.Equal(
            ["nmcli connection delete RdpManager", "nmcli connection import type wireguard file " + conf, "nmcli connection up RdpManager"],
            r.Calls);
        var text = File.ReadAllText(conf);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("PrivateKey = " + cfg.PrivateKey, text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(conf));
    }

    [Fact]
    public async Task Connetti_ImportFallito_RiportaErrore_ConfigNonValidaNonLanciaNulla()
    {
        var conf = Path.Combine(_dir, "RdpManager.conf");
        var r = new FakeRunner { Handler = l => l.Contains(" import ") ? new ProcessResult(1, "", "boom") : new ProcessResult(0, "", "") };
        var res = await new LinuxWireGuard(r, conf, LinuxVpnBackend.NetworkManager).ConnectAsync(SampleConfig());
        Assert.False(res.Success);
        Assert.Contains("boom", res.Message);
        Assert.DoesNotContain("nmcli connection up RdpManager", r.Calls);

        var r2 = new FakeRunner();
        var bad = await new LinuxWireGuard(r2, conf, LinuxVpnBackend.WgQuick).ConnectAsync(new WireGuardConfig());
        Assert.False(bad.Success);
        Assert.Empty(r2.Calls);
    }

    [Fact]
    public async Task Connetti_WgQuick_E_Stato()
    {
        var conf = Path.Combine(_dir, "RdpManager.conf");
        var r = new FakeRunner();
        var wg = new LinuxWireGuard(r, conf, LinuxVpnBackend.WgQuick);
        Assert.True((await wg.ConnectAsync(SampleConfig())).Success);
        Assert.Equal(["pkexec wg-quick up " + conf], r.Calls);
        Assert.True(await wg.IsConnectedAsync());
        Assert.Equal("ip -o link show dev RdpManager", r.Calls[^1]);
        Assert.True((await wg.DisconnectAsync()).Success);
        Assert.Equal("pkexec wg-quick down " + conf, r.Calls[^1]);
    }

    [Fact]
    public async Task ScelgoBackend_NmcliPresenteOAssente()
    {
        var present = new LinuxWireGuard(new FakeRunner(), Path.Combine(_dir, "a.conf"));
        Assert.Equal(LinuxVpnBackend.NetworkManager, await present.GetBackendAsync());
        var absent = new LinuxWireGuard(new FakeRunner { Handler = _ => new ProcessResult(127, "", "") }, Path.Combine(_dir, "b.conf"));
        Assert.Equal(LinuxVpnBackend.WgQuick, await absent.GetBackendAsync());
    }

    [Fact]
    public async Task Stato_NetworkManager_LeggeElencoAttive()
    {
        var r = new FakeRunner { Handler = _ => new ProcessResult(0, "RdpManager:wireguard:RdpManager\n", "") };
        Assert.True(await new LinuxWireGuard(r, Path.Combine(_dir, "c.conf"), LinuxVpnBackend.NetworkManager).IsConnectedAsync());
    }
}
