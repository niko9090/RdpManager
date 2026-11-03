using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.Tests;

/// <summary>
/// Prova con il driver wireguard-nt vero: crea un adattatore, quindi installa il driver e richiede l'amministratore.
/// Saltata se il processo non è elevato o se RDPM_VPN_DRIVER_TEST non vale 1.
/// </summary>
public sealed class VpnDriverFactAttribute : FactAttribute
{
    public const string Variable = "RDPM_VPN_DRIVER_TEST";

    public VpnDriverFactAttribute()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Prova con il driver wireguard-nt vero: serve un processo elevato (amministratore).";
        else if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Prova con il driver wireguard-nt vero (installa il driver): imposta {Variable}=1 per eseguirla.";
    }
}

public class WireGuardNtStructsTests
{
    private static WireGuardConfig Config(string allowed = "10.66.0.0/24", int keepalive = 25, byte priv = 7, byte pub = 9)
        => new()
        {
            PrivateKey = Convert.ToBase64String(Enumerable.Repeat(priv, 32).ToArray()),
            Address = "10.66.0.2/32",
            PeerPublicKey = Convert.ToBase64String(Enumerable.Repeat(pub, 32).ToArray()),
            Endpoint = "vpn.example.org:51820",
            AllowedIPs = allowed,
            PersistentKeepalive = keepalive,
        };

    [Fact]
    public void Sizes_AreMultiplesOfEightAsInWireguardH()
    {
        Assert.Equal(80, WireGuardNtStructs.InterfaceSize);
        Assert.Equal(136, WireGuardNtStructs.PeerSize);
        Assert.Equal(24, WireGuardNtStructs.AllowedIpSize);
        Assert.All([WireGuardNtStructs.InterfaceSize, WireGuardNtStructs.PeerSize, WireGuardNtStructs.AllowedIpSize], n => Assert.Equal(0, n % 8));
    }

    [Fact]
    public void BuildConfiguration_Layout()
    {
        var buf = WireGuardNtStructs.BuildConfiguration(Config(), IPAddress.Parse("203.0.113.5"));
        Assert.Equal(80 + 136 + 24, buf.Length);
        var s = buf.AsSpan();

        // WIREGUARD_INTERFACE
        Assert.Equal(WireGuardNtStructs.InterfaceHasPrivateKey | WireGuardNtStructs.InterfaceReplacePeers, BinaryPrimitives.ReadUInt32LittleEndian(s));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(s[4..])); // ListenPort: a caso
        Assert.Equal(Enumerable.Repeat((byte)7, 32), s.Slice(6, 32).ToArray());
        Assert.Equal(new byte[32], s.Slice(38, 32).ToArray()); // PublicKey non impostata: la calcola il driver
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(s[72..]));

        // WIREGUARD_PEER (subito dopo, a offset 80)
        var p = s[80..];
        Assert.Equal(WireGuardNtStructs.PeerHasPublicKey | WireGuardNtStructs.PeerHasEndpoint | WireGuardNtStructs.PeerHasKeepalive
            | WireGuardNtStructs.PeerReplaceAllowedIps, BinaryPrimitives.ReadUInt32LittleEndian(p));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(p[4..])); // Reserved
        Assert.Equal(Enumerable.Repeat((byte)9, 32), p.Slice(8, 32).ToArray());
        Assert.Equal(new byte[32], p.Slice(40, 32).ToArray()); // PresharedKey
        Assert.Equal(25, BinaryPrimitives.ReadUInt16LittleEndian(p[72..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(p[76..])); // AF_INET
        Assert.Equal(51820, BinaryPrimitives.ReadUInt16BigEndian(p[78..])); // porta in network order
        Assert.Equal(new byte[] { 203, 0, 113, 5 }, p.Slice(80, 4).ToArray());
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(p[104..])); // Tx
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(p[128..])); // AllowedIPsCount

        // WIREGUARD_ALLOWED_IP (dopo il peer)
        var a = p[136..];
        Assert.Equal(new byte[] { 10, 66, 0, 0 }, a[..4].ToArray());
        Assert.Equal(WireGuardNtStructs.AfInet, BinaryPrimitives.ReadUInt16LittleEndian(a[16..]));
        Assert.Equal(24, a[18]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(a[20..])); // Flags
    }

    [Fact]
    public void BuildConfiguration_MultipleAllowedIps_AndNoKeepalive()
    {
        var buf = WireGuardNtStructs.BuildConfiguration(Config("10.66.0.0/24, 192.168.1.0/24", keepalive: 0), IPAddress.Loopback);
        Assert.Equal(80 + 136 + 2 * 24, buf.Length);
        var p = buf.AsSpan(80);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(p) & WireGuardNtStructs.PeerHasKeepalive);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(p[128..]));
        var second = p[(136 + 24)..];
        Assert.Equal(new byte[] { 192, 168, 1, 0 }, second[..4].ToArray());
        Assert.Equal(24, second[18]);
    }

    [Fact]
    public void BuildConfiguration_KeysAreDecodedFromBase64()
    {
        var (priv, _) = WireGuardKeys.Generate();
        var (_, pub) = WireGuardKeys.Generate();
        var cfg = Config() with { PrivateKey = priv, PeerPublicKey = pub };
        var buf = WireGuardNtStructs.BuildConfiguration(cfg, IPAddress.Loopback);
        Assert.Equal(Convert.FromBase64String(priv), buf.AsSpan(6, 32).ToArray());
        Assert.Equal(Convert.FromBase64String(pub), buf.AsSpan(88, 32).ToArray());
    }

    [Fact]
    public void BuildConfiguration_BadKeysOrEndpoint_Throw()
    {
        Assert.Throws<ArgumentException>(() => WireGuardNtStructs.BuildConfiguration(Config() with { PrivateKey = "corta" }, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => WireGuardNtStructs.BuildConfiguration(Config() with { PeerPublicKey = "" }, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => WireGuardNtStructs.BuildConfiguration(Config() with { Endpoint = "senza-porta" }, IPAddress.Loopback));
        Assert.Throws<ArgumentException>(() => WireGuardNtStructs.BuildConfiguration(Config("boh"), IPAddress.Loopback));
    }

    private static byte[] StatusBlock(params (ulong tx, ulong rx, long hs, uint allowed)[] peers)
    {
        var size = 80 + peers.Sum(p => 136 + (int)p.allowed * 24);
        var buf = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), (uint)peers.Length);
        int off = 80;
        foreach (var p in peers)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 104), p.tx);
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 112), p.rx);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 120), p.hs);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 128), p.allowed);
            off += 136 + (int)p.allowed * 24;
        }
        return buf;
    }

    [Fact]
    public void ParseStatus_ReadsHandshakeAndBytes()
    {
        var hs = new DateTime(2026, 11, 3, 8, 30, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 3, 8, 31, 0, DateTimeKind.Utc);
        var st = WireGuardNtStructs.ParseStatus(StatusBlock((1000, 2000, hs.ToFileTimeUtc(), 2)), now);
        Assert.NotNull(st);
        Assert.Equal(hs, st.LastHandshakeUtc);
        Assert.Equal(1000ul, st.TxBytes);
        Assert.Equal(2000ul, st.RxBytes);
        Assert.Equal(now, st.UpdatedUtc);
    }

    [Fact]
    public void ParseStatus_NoHandshakeYet_AndSumsPeers()
    {
        var st = WireGuardNtStructs.ParseStatus(StatusBlock((1, 2, 0, 1), (10, 20, 0, 0)), DateTime.UtcNow);
        Assert.NotNull(st);
        Assert.Null(st.LastHandshakeUtc);
        Assert.Equal(11ul, st.TxBytes);
        Assert.Equal(22ul, st.RxBytes);
    }

    [Fact]
    public void ParseStatus_TruncatedBuffer_ReturnsNull()
    {
        var good = StatusBlock((1, 2, 0, 2));
        Assert.Null(WireGuardNtStructs.ParseStatus(good.AsSpan(0, 60), DateTime.UtcNow));
        Assert.Null(WireGuardNtStructs.ParseStatus(good.AsSpan(0, good.Length - 1), DateTime.UtcNow));
        Assert.Null(WireGuardNtStructs.ParseStatus([], DateTime.UtcNow));
    }

    [Fact]
    public void VpnStatus_FileFormatRoundTrips_AndRejectsGarbage()
    {
        var st = new VpnStatus(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 5, 6, new DateTime(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc));
        Assert.Equal(st, VpnStatus.Parse(st.Format()));
        var none = st with { LastHandshakeUtc = null };
        Assert.Equal(none, VpnStatus.Parse(none.Format()));
        Assert.Null(VpnStatus.Parse(""));
        Assert.Null(VpnStatus.Parse("handshake=abc\n"));
        Assert.Null(VpnStatus.Parse("handshake=0\ntx=1\n"));
    }

    [Fact]
    public void NetworkRows_HaveWindowsLayout()
    {
        var luid = 0x0006_0000_0000_0000ul;
        var u = WireGuardNtStructs.BuildUnicastRow(luid, IPAddress.Parse("10.66.0.2"), 32);
        Assert.Equal(80, u.Length);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(u));
        Assert.Equal(new byte[] { 10, 66, 0, 2 }, u[4..8]);
        Assert.Equal(luid, BinaryPrimitives.ReadUInt64LittleEndian(u.AsSpan(32)));
        Assert.Equal(32, u[60]);

        var f = WireGuardNtStructs.BuildForwardRow(luid, IPAddress.Parse("10.66.0.0"), 24);
        Assert.Equal(104, f.Length);
        Assert.Equal(luid, BinaryPrimitives.ReadUInt64LittleEndian(f));
        Assert.Equal(new byte[] { 10, 66, 0, 0 }, f[16..20]);
        Assert.Equal(24, f[40]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(44)));
    }
}

public class VpnTunnelRunnerTests
{
    private sealed class FakeAdapter(List<string> log, string? failAt = null) : INtAdapter
    {
        public byte[]? Block;
        private void Step(string name)
        {
            log.Add(name);
            if (name == failAt)
                throw new InvalidOperationException("guasto " + name);
        }
        public void SetConfiguration(byte[] block) { Block = block; Step("config"); }
        public void AddAddress(IPAddress ip, int prefix) => Step($"addr {ip}/{prefix}");
        public void AddRoute(IPAddress network, int prefix) => Step($"route {network}/{prefix}");
        public void Up() => Step("up");
        public byte[]? GetConfiguration() => Status;
        public byte[]? Status;
        public void Dispose() => log.Add("close");
    }

    private sealed class FakeDriver(List<string> log, string? failAt = null) : INtDriver
    {
        public FakeAdapter? Adapter;
        public INtAdapter Create(string name, string tunnelType)
        {
            log.Add($"create {name}/{tunnelType}");
            return Adapter = new FakeAdapter(log, failAt);
        }
    }

    private static WireGuardConfig Config() => new()
    {
        PrivateKey = Convert.ToBase64String(new byte[32].Select(_ => (byte)1).ToArray()),
        Address = "10.66.0.2/32",
        PeerPublicKey = Convert.ToBase64String(new byte[32].Select(_ => (byte)2).ToArray()),
        AllowedIPs = "10.66.0.0/24,192.168.1.0/24",
    };

    [Fact]
    public void Start_CreatesConfiguresAddressesRoutesThenBringsUp()
    {
        var log = new List<string>();
        var driver = new FakeDriver(log);
        string? resolved = null;
        using var runner = new VpnTunnelRunner(driver, h => { resolved = h; return IPAddress.Parse("198.51.100.7"); });
        runner.Start(Config(), "RdpManager");
        Assert.Equal("syncnetpro.com", resolved);
        Assert.Equal(["create RdpManager/RdpManager", "config", "addr 10.66.0.2/32", "route 10.66.0.0/24", "route 192.168.1.0/24", "up"], log);
        Assert.True(runner.IsRunning);
        Assert.Equal(198, driver.Adapter!.Block![80 + 76 + 4]);
    }

    [Fact]
    public void Start_FailureClosesAdapterAndRethrows()
    {
        var log = new List<string>();
        var runner = new VpnTunnelRunner(new FakeDriver(log, failAt: "up"), _ => IPAddress.Loopback);
        Assert.Throws<InvalidOperationException>(() => runner.Start(Config(), "RdpManager"));
        Assert.Equal("close", log[^1]);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public void Start_InvalidConfig_DoesNotTouchTheDriver()
    {
        var log = new List<string>();
        var runner = new VpnTunnelRunner(new FakeDriver(log), _ => IPAddress.Loopback);
        Assert.Throws<ArgumentException>(() => runner.Start(new WireGuardConfig(), "RdpManager"));
        Assert.Empty(log);
    }

    [Fact]
    public void Stop_ClosesAdapter_AndStatusIsReadFromDriver()
    {
        var log = new List<string>();
        var driver = new FakeDriver(log);
        var now = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var runner = new VpnTunnelRunner(driver, _ => IPAddress.Loopback, () => now);
        Assert.Null(runner.ReadStatus());
        runner.Start(Config(), "RdpManager");
        var block = new byte[80 + 136];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(72), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(80 + 112), 77);
        driver.Adapter!.Status = block;
        Assert.Equal(77ul, runner.ReadStatus()!.RxBytes);
        runner.Stop();
        Assert.False(runner.IsRunning);
        Assert.Equal("close", log[^1]);
        Assert.Null(runner.ReadStatus());
    }
}

public class VpnServiceCommandTests
{
    [Theory]
    [InlineData("RdpManager", true)]
    [InlineData("vpn-1_a", true)]
    [InlineData("", false)]
    [InlineData("..\\evil", false)]
    [InlineData("a b", false)]
    [InlineData("nome-molto-molto-molto-molto-lungo-oltre-il-limite", false)]
    public void ServiceArgs_AcceptOnlySafeNames(string name, bool ok)
    {
        Assert.Equal(ok, VpnServiceArgs.TryParse(["--vpn-service", name], out var parsed));
        Assert.Equal(ok ? name : "", parsed);
    }

    [Fact]
    public void ServiceArgs_RequireExactFlagAndOneName()
    {
        Assert.False(VpnServiceArgs.TryParse([], out _));
        Assert.False(VpnServiceArgs.TryParse(["--vpn-service"], out _));
        Assert.False(VpnServiceArgs.TryParse(["--wg-service", "RdpManager"], out _));
        Assert.False(VpnServiceArgs.TryParse(["--vpn-service", "a", "b"], out _));
        Assert.True(VpnServiceArgs.TryParse(["--VPN-SERVICE", "a"], out _));
    }

    [Fact]
    public void ServiceName_IsNotTheOfficialWireGuardOne()
    {
        Assert.Equal("RdpManagerVpn", WireGuardNtTunnel.ServiceName);
        Assert.DoesNotContain("WireGuardTunnel", WireGuardNtTunnel.ServiceName);
    }

    [Fact]
    public void Paths_AreUnderProgramDataVpn()
    {
        Assert.Equal(Path.Combine("pd", "vpn", "X.conf"), VpnPaths.Conf("pd", "X"));
        Assert.Equal(Path.Combine("pd", "vpn", "stato.txt"), VpnPaths.Status("pd"));
        Assert.Equal(Path.Combine("pd", "vpn", "installato.sha256"), VpnPaths.InstalledHash("pd"));
    }

    [Fact]
    public void HashConfig_IsStableAndSensitive()
    {
        Assert.Equal(VpnHelper.HashConfig("a"), VpnHelper.HashConfig("a"));
        Assert.NotEqual(VpnHelper.HashConfig("a"), VpnHelper.HashConfig("b"));
        Assert.Equal(64, VpnHelper.HashConfig("a").Length);
    }
}

public class WireGuardDllTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void Build_ContainsWireguardDll_SignedByWireGuardLlc()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "wireguard.dll");
        Assert.True(File.Exists(dll), "wireguard.dll non è accanto ai file compilati");
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(dll));
        Assert.Contains("WireGuard LLC", cert.Subject);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid; // firma valida a suo tempo
        Assert.True(chain.Build(cert), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void Build_DoesNotShipTunnelDll()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "tunnel.dll")));
        var root = RepoRoot();
        Assert.NotNull(root);
        Assert.False(File.Exists(Path.Combine(root, "src", "RdpManager", "Vpn", "dll", "amd64", "tunnel.dll")));
    }

    [Fact]
    public void Sources_DoNotDependOnTunnelDll()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "RdpManager", "Vpn"), "*.cs"))
            Assert.DoesNotContain("tunnel.dll", File.ReadAllText(file));
        Assert.DoesNotContain("tunnel.dll", File.ReadAllText(Path.Combine(root, "src", "RdpManager", "RdpManager.csproj")).Replace("tunnel.dll non serve", ""));
    }

    [VpnDriverFact]
    public void RealDriver_CreatesAdapterAppliesConfigAndReadsStatus()
    {
        var (priv, _) = WireGuardKeys.Generate();
        var (_, pub) = WireGuardKeys.Generate();
        var cfg = new WireGuardConfig
        {
            PrivateKey = priv,
            Address = "10.199.0.2/32",
            PeerPublicKey = pub,
            Endpoint = "127.0.0.1:51899",
            AllowedIPs = "10.199.0.0/24",
        };
        using var runner = new VpnTunnelRunner(new WireGuardNtDriver());
        runner.Start(cfg, "RdpManagerTest");
        Assert.True(runner.IsRunning);
        var status = runner.ReadStatus();
        Assert.NotNull(status);
        Assert.Null(status.LastHandshakeUtc);
    }
}
