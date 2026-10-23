using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Core della 1.6.0: intervalli di indirizzi (maschera, CIDR, da-a), limite /22, indirizzi ammessi, scanner contro
/// un listener locale, lettura di «tailscale status --json», classificazione delle schede VPN, PC trovati aggiunti
/// senza doppioni e compatibilità con i connections.json 1.0-1.5. Nomi e indirizzi sono inventati.
/// </summary>
public sealed class V16CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV16-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---------- intervalli ----------

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.1", "192.168.1.254", 254)]
    [InlineData("192.168.1.77/24", "192.168.1.1", "192.168.1.254", 254)]
    [InlineData("10.0.0.0/22", "10.0.0.1", "10.0.3.254", 1022)]
    [InlineData("10.0.0.5/30", "10.0.0.5", "10.0.0.6", 2)]
    [InlineData("10.0.0.5/31", "10.0.0.4", "10.0.0.5", 2)]
    [InlineData("10.0.0.5/32", "10.0.0.5", "10.0.0.5", 1)]
    [InlineData("172.16.5.9/255.255.255.0", "172.16.5.1", "172.16.5.254", 254)]
    [InlineData("192.168.1.10-192.168.1.50", "192.168.1.10", "192.168.1.50", 41)]
    [InlineData("192.168.1.10-50", "192.168.1.10", "192.168.1.50", 41)]
    [InlineData(" 192.168.1.250 - 192.168.2.5 ", "192.168.1.250", "192.168.2.5", 12)]
    [InlineData("192.168.1.9", "192.168.1.9", "192.168.1.9", 1)]
    public void TryParse_AcceptsCidrMaskAndFromTo(string text, string first, string last, long count)
    {
        Assert.True(IpRange.TryParse(text, out var r, out var error), error);
        Assert.Equal(IPAddress.Parse(first), r.FirstAddress);
        Assert.Equal(IPAddress.Parse(last), r.LastAddress);
        Assert.Equal(count, r.Count);
        Assert.Equal(count, r.Addresses().LongCount());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ciao")]
    [InlineData("192.168.1/24")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/255.0.255.0")]
    [InlineData("192.168.1.50-192.168.1.10")]
    [InlineData("192.168.1.10-300")]
    [InlineData("192.168.1.256")]
    [InlineData("fe80::1/64")]
    public void TryParse_RejectsInvalidWithAMessage(string text)
    {
        Assert.False(IpRange.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.192.0.0", 10)]
    [InlineData("255.255.255.255", 32)]
    [InlineData("0.0.0.0", 0)]
    public void MaskToPrefix(string mask, int prefix)
    {
        Assert.True(IpRange.TryMaskToPrefix(IPAddress.Parse(mask), out var p));
        Assert.Equal(prefix, p);
    }

    [Fact]
    public void FromMask_GivesTheSubnetOfTheAdapter()
    {
        var r = IpRange.FromMask(IPAddress.Parse("192.168.10.37"), IPAddress.Parse("255.255.255.0"));
        Assert.Equal("192.168.10.1 - 192.168.10.254", r.ToString());
        Assert.True(r.Contains(IPAddress.Parse("192.168.10.37")));
        Assert.False(r.Contains(IPAddress.Parse("192.168.11.1")));
        Assert.Equal(24, r.CoveringPrefix);
        Assert.Throws<ArgumentException>(() => IpRange.FromMask(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.0.255.0")));
    }

    // ---------- limite /22 e indirizzi ammessi ----------

    [Theory]
    [InlineData("192.168.0.0/22", ScanCheck.Ok)]
    [InlineData("192.168.0.0/21", ScanCheck.NeedsSizeConfirmation)]
    [InlineData("10.0.0.0/16", ScanCheck.NeedsSizeConfirmation)]
    [InlineData("10.0.0.0/15", ScanCheck.TooLarge)]
    [InlineData("100.64.0.0/24", ScanCheck.Ok)]
    [InlineData("169.254.1.0/24", ScanCheck.Ok)]
    [InlineData("8.8.8.0/24", ScanCheck.NeedsPublicConfirmation)]
    [InlineData("192.168.255.250-192.169.0.5", ScanCheck.NeedsPublicConfirmation)]
    public void Policy_LimitsSizeAndPublicAddresses(string text, ScanCheck expected)
    {
        Assert.True(IpRange.TryParse(text, out var r, out _));
        Assert.Equal(expected, ScanPolicy.Check(r));
        if (expected != ScanCheck.Ok)
            Assert.NotEmpty(ScanPolicy.Describe(expected, r));
    }

    [Fact]
    public void Policy_ConfirmationsUnlockButNeverPastTheHardLimit()
    {
        IpRange.TryParse("10.0.0.0/20", out var big, out _);
        Assert.Equal(ScanCheck.Ok, ScanPolicy.Check(big, sizeConfirmed: true));
        IpRange.TryParse("1.2.0.0/20", out var bigPublic, out _);
        Assert.Equal(ScanCheck.NeedsSizeConfirmation, ScanPolicy.Check(bigPublic));
        Assert.Equal(ScanCheck.NeedsPublicConfirmation, ScanPolicy.Check(bigPublic, sizeConfirmed: true));
        Assert.Equal(ScanCheck.Ok, ScanPolicy.Check(bigPublic, sizeConfirmed: true, publicConfirmed: true));
        IpRange.TryParse("10.0.0.0/8", out var huge, out _);
        Assert.Equal(ScanCheck.TooLarge, ScanPolicy.Check(huge, sizeConfirmed: true, publicConfirmed: true));
    }

    [Theory]
    [InlineData("10.1.2.3", AddressScope.Private)]
    [InlineData("172.16.0.1", AddressScope.Private)]
    [InlineData("172.31.255.254", AddressScope.Private)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("192.168.0.1", AddressScope.Private)]
    [InlineData("192.169.0.1", AddressScope.Public)]
    [InlineData("100.64.0.1", AddressScope.Cgnat)]
    [InlineData("100.127.255.254", AddressScope.Cgnat)]
    [InlineData("100.128.0.1", AddressScope.Public)]
    [InlineData("169.254.10.10", AddressScope.LinkLocal)]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    public void AddressScope_Classifies(string ip, AddressScope scope)
    {
        Assert.Equal(scope, AddressScopes.Of(IPAddress.Parse(ip)));
        Assert.Equal(scope != AddressScope.Public, AddressScopes.IsScannableWithoutConfirmation(IPAddress.Parse(ip)));
    }

    [Fact]
    public void CountOutsidePrivate_CountsOnlyPublicAddresses()
    {
        IpRange.TryParse("192.168.255.250-192.169.0.5", out var r, out _);
        Assert.Equal(6, AddressScopes.CountOutsidePrivate(r)); // 192.169.0.0 - .5
        IpRange.TryParse("172.16.0.0/12", out var p, out _);
        Assert.Equal(0, AddressScopes.CountOutsidePrivate(p));
    }

    // ---------- scanner ----------

    [Fact]
    public async Task Scanner_FindsALocalListenerAndNotTheClosedPorts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var names = new List<IPAddress>();
        var scanner = new NetworkScanner((ip, _) =>
        {
            lock (names)
                names.Add(ip);
            return Task.FromResult<string?>("pc-di-prova.esempio.lan.");
        });
        var progress = new List<ScanProgress>();
        IpRange.TryParse("127.0.0.1-127.0.0.3", out var range, out _);

        var hits = await scanner.ScanAsync(range, new ScanOptions { Port = port, MaxConcurrency = 2, Timeout = TimeSpan.FromSeconds(1) },
            new SyncProgress<ScanProgress>(progress.Add));

        // Su Windows il listener su 127.0.0.1 risponde solo a quell'indirizzo.
        var hit = Assert.Single(hits);
        Assert.Equal(IPAddress.Loopback, hit.Address);
        Assert.Equal(port, hit.Port);
        Assert.Equal("pc-di-prova.esempio.lan", hit.HostName);
        Assert.Equal([IPAddress.Loopback], names);
        Assert.Equal(3, progress.Count);
        Assert.Equal(new ScanProgress(3, 3, 1), progress.OrderBy(p => p.Done).Last());
    }

    [Fact]
    public async Task Scanner_RespectsTheConcurrencyLimit()
    {
        int current = 0, peak = 0;
        var scanner = new NetworkScanner((_, _) => Task.FromResult<string?>(null), async (ip, _, _, ct) =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref current));
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref current);
            return ip.GetAddressBytes()[3] % 10 == 0;
        });
        IpRange.TryParse("10.0.0.1-10.0.0.60", out var range, out _);
        var hits = await scanner.ScanAsync(range, new ScanOptions { MaxConcurrency = 3 });
        Assert.InRange(peak, 1, 3);
        Assert.Equal(["10.0.0.10", "10.0.0.20", "10.0.0.30", "10.0.0.40", "10.0.0.50", "10.0.0.60"], hits.Select(h => h.Address.ToString()));

        // Gli indirizzi ripetuti si controllano una volta sola.
        var once = await scanner.ScanAsync(Enumerable.Repeat(IPAddress.Parse("10.0.0.10"), 5));
        Assert.Single(once);
    }

    [Fact]
    public async Task Scanner_StopsWhenCancelled()
    {
        IpRange.TryParse("192.0.2.1-192.0.2.200", out var range, out _); // TEST-NET-1: nessuno risponde
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var scanner = new NetworkScanner((_, _) => Task.FromResult<string?>(null));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(range,
            new ScanOptions { MaxConcurrency = 4, Timeout = TimeSpan.FromSeconds(3) }, cancellationToken: cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"annullamento lento: {sw.Elapsed}");
    }

    [Fact]
    public async Task Scanner_IgnoresReverseDnsFailures()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = new NetworkScanner((_, _) => throw new SocketException((int)SocketError.HostNotFound));
        var hit = Assert.Single(await scanner.ScanAsync([IPAddress.Loopback], new ScanOptions { Port = port }));
        Assert.Null(hit.HostName);
        var slow = new NetworkScanner(async (_, ct) => { await Task.Delay(5000, ct); return "tardi"; });
        var hit2 = Assert.Single(await slow.ScanAsync([IPAddress.Loopback], new ScanOptions { Port = port, NameTimeout = TimeSpan.FromMilliseconds(100) }));
        Assert.Null(hit2.HostName);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
                report(value);
        }
    }

    // ---------- Tailscale ----------

    private const string TailscaleJson = """
    {
      "Version": "1.70.0",
      "BackendState": "Running",
      "MagicDNSSuffix": "esempio.ts.net",
      "Self": { "HostName": "Questo-PC", "DNSName": "questo-pc.esempio.ts.net.", "OS": "windows",
                "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"], "Online": true },
      "Peer": {
        "nodekey:aaa": { "HostName": "PC-Ufficio", "DNSName": "pc-ufficio.esempio.ts.net.", "OS": "windows",
                         "TailscaleIPs": ["100.64.0.2", "fd7a:115c:a1e0::2"], "Online": true },
        "nodekey:bbb": { "HostName": "Portatile", "DNSName": "portatile.esempio.ts.net.", "OS": "macOS",
                         "TailscaleIPs": ["100.64.0.3"], "Online": false },
        "nodekey:ccc": { "HostName": "senza-dns", "DNSName": "", "OS": "linux",
                         "TailscaleIPs": ["100.64.0.4"], "Online": true },
        "nodekey:ddd": { "HostName": "solo-ipv6", "OS": "windows",
                         "TailscaleIPs": ["fd7a:115c:a1e0::5"], "Online": true }
      }
    }
    """;

    [Fact]
    public void Tailscale_ParsesOnlineOfflineMissingMagicDnsAndIpv6()
    {
        var s = TailscaleStatus.Parse(TailscaleJson);
        Assert.True(s.IsRunning);
        Assert.Equal("", s.StateMessage);
        Assert.Equal("esempio.ts.net", s.MagicDnsSuffix);
        Assert.NotNull(s.Self);
        Assert.True(s.Self!.IsSelf);
        Assert.Equal("questo-pc.esempio.ts.net", s.Self.DnsName);

        Assert.Equal(4, s.Peers.Count);
        Assert.False(s.Peers[^1].Online); // in linea prima
        var office = s.Peers.Single(p => p.HostName == "PC-Ufficio");
        Assert.True(office.Online);
        Assert.Equal("pc-ufficio", office.DisplayName);
        Assert.Equal("pc-ufficio.esempio.ts.net", office.ConnectAddress);
        Assert.Equal(IPAddress.Parse("100.64.0.2"), office.IPv4);
        Assert.Equal(IPAddress.Parse("fd7a:115c:a1e0::2"), office.IPv6);
        Assert.Equal("windows", office.OS);

        var laptop = s.Peers.Single(p => p.HostName == "Portatile");
        Assert.False(laptop.Online);
        Assert.Null(laptop.IPv6);

        var noDns = s.Peers.Single(p => p.HostName == "senza-dns");
        Assert.Equal("", noDns.DnsName);
        Assert.Equal("senza-dns", noDns.DisplayName);
        Assert.Equal("100.64.0.4", noDns.ConnectAddress);

        var v6 = s.Peers.Single(p => p.HostName == "solo-ipv6");
        Assert.Null(v6.IPv4);
        Assert.Equal("fd7a:115c:a1e0::5", v6.ConnectAddress);
    }

    [Theory]
    [InlineData("NeedsLogin", "accesso")]
    [InlineData("Stopped", "spento")]
    [InlineData("Qualcosa", "non è connesso")]
    public void Tailscale_NotRunningGivesAClearMessage(string state, string expected)
    {
        var s = TailscaleStatus.Parse($$"""{ "BackendState": "{{state}}", "Peer": null }""");
        Assert.False(s.IsRunning);
        Assert.Empty(s.Peers);
        Assert.Contains(expected, s.StateMessage);
    }

    [Fact]
    public void Tailscale_InvalidJsonIsAFormatException()
    {
        Assert.Throws<FormatException>(() => TailscaleStatus.Parse("non è json"));
        Assert.Throws<FormatException>(() => TailscaleStatus.Parse("[1,2]"));
        var minimal = TailscaleStatus.Parse("{}");
        Assert.Empty(minimal.Peers);
        Assert.Null(minimal.Self);
    }

    [Fact]
    public void TailscaleCli_InterpretsOutputAndErrors()
    {
        var ok = TailscaleCli.Interpret(0, TailscaleJson, "");
        Assert.Equal(TailscaleLookupOutcome.Ok, ok.Outcome);
        Assert.Equal(4, ok.Status!.Peers.Count);

        var down = TailscaleCli.Interpret(1, "", "failed to connect to local tailscaled; it doesn't appear to be running");
        Assert.Equal(TailscaleLookupOutcome.Failed, down.Outcome);
        Assert.Contains("non è avviato", down.Message);

        var garbage = TailscaleCli.Interpret(0, "???", "");
        Assert.Equal(TailscaleLookupOutcome.Failed, garbage.Outcome);
    }

    [Fact]
    public void TailscaleCli_FindsTheExecutableOrSaysItIsMissing()
    {
        var installed = Path.Combine("C:\\Programmi", "Tailscale", "tailscale.exe");
        Assert.Equal(installed, TailscaleCli.FindExecutable(p => p == installed, path: "", programFiles: "C:\\Programmi"));
        var onPath = Path.Combine("D:\\strumenti", "tailscale.exe");
        Assert.Equal(onPath, TailscaleCli.FindExecutable(p => p == onPath, path: "C:\\altro;D:\\strumenti", programFiles: "C:\\Programmi"));
        Assert.Null(TailscaleCli.FindExecutable(_ => false, path: "C:\\altro", programFiles: "C:\\Programmi"));
        Assert.Contains("non è installato", TailscaleCli.NotInstalledMessage);
    }

    // ---------- classificazione delle schede ----------

    [Theory]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, NetworkKind.Tailscale)]
    [InlineData("Ethernet 3", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet, NetworkKind.ZeroTier)]
    [InlineData("ufficio", "WireGuard Tunnel", NetworkInterfaceType.Unknown, NetworkKind.WireGuard)]
    [InlineData("Ethernet 2", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, NetworkKind.OpenVPN)]
    [InlineData("OpenVPN Data Channel Offload", "OpenVPN Data Channel Offload", NetworkInterfaceType.Unknown, NetworkKind.OpenVPN)]
    [InlineData("VPN sede", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp, NetworkKind.OtherVpn)]
    [InlineData("Ethernet 4", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", NetworkInterfaceType.Ethernet, NetworkKind.OtherVpn)]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet, NetworkKind.Local)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, NetworkKind.Local)]
    public void Vpn_ClassifiesAdapters(string name, string description, NetworkInterfaceType type, NetworkKind kind) =>
        Assert.Equal(kind, VpnProviders.Classify(name, description, type));

    [Fact]
    public void Vpn_ProvidersAreExtensible()
    {
        IVpnProvider[] custom = [new KeywordVpnProvider(NetworkKind.OtherVpn, "Rete di prova", "ReteProva"), .. VpnProviders.All];
        Assert.Equal(NetworkKind.OtherVpn, VpnProviders.Classify("ReteProva 1", "Scheda virtuale", NetworkInterfaceType.Ethernet, providers: custom));
        Assert.Equal(NetworkKind.Local, VpnProviders.Classify("ReteProva 1", "Scheda virtuale", NetworkInterfaceType.Ethernet));
        Assert.Equal("ZeroTier", VpnProviders.DisplayName(NetworkKind.ZeroTier));
        Assert.Equal("Rete locale", VpnProviders.DisplayName(NetworkKind.Local));
    }

    [Fact]
    public void Adapter_SuggestsAtMostA22()
    {
        var lan = new AdapterInfo("Ethernet", "x", NetworkInterfaceType.Ethernet, IPAddress.Parse("192.168.1.20"), 24, NetworkKind.Local);
        Assert.Equal(lan.Subnet, lan.SuggestedRange);
        var zt = new AdapterInfo("ZeroTier", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet, IPAddress.Parse("10.147.17.5"), 16, NetworkKind.ZeroTier);
        Assert.Equal("10.147.17.1 - 10.147.17.254", zt.SuggestedRange.ToString());
        Assert.True(zt.IsVpn);
        Assert.Equal(ScanCheck.NeedsSizeConfirmation, ScanPolicy.Check(zt.Subnet));
        Assert.Equal(ScanCheck.Ok, ScanPolicy.Check(zt.SuggestedRange));
    }

    // ---------- PC trovati e doppioni ----------

    [Fact]
    public void Import_AddsToTheFolderAndSkipsDuplicates()
    {
        var doc = new ConnectionsDocument
        {
            Connections =
            [
                new RdpConnection { Name = "Ufficio", Host = "PC-UFFICIO.esempio.ts.net" },
                new RdpConnection { Name = "Stampa", Host = "192.168.1.30" },
                new RdpConnection { Name = "Altra porta", Host = "192.168.1.40", Port = 3390 },
                new RdpConnection { Name = "Nome breve", Host = "magazzino" },
            ],
        };
        var peers = TailscaleStatus.Parse(TailscaleJson).Peers;
        var found = new List<DiscoveredPc>
        {
            DiscoveredPc.FromTailscale(peers.Single(p => p.HostName == "PC-Ufficio")),    // doppione (MagicDNS)
            DiscoveredPc.FromTailscale(peers.Single(p => p.HostName == "senza-dns")),     // nuovo
            DiscoveredPc.FromScan(new ScanHit(IPAddress.Parse("192.168.1.30"), 3389, null, TimeSpan.Zero)),               // doppione (IP)
            DiscoveredPc.FromScan(new ScanHit(IPAddress.Parse("192.168.1.40"), 3389, null, TimeSpan.Zero)),               // nuovo: porta diversa
            DiscoveredPc.FromScan(new ScanHit(IPAddress.Parse("192.168.1.31"), 3389, "magazzino.esempio.lan", TimeSpan.Zero)), // doppione (nome breve)
            DiscoveredPc.FromScan(new ScanHit(IPAddress.Parse("192.168.1.50"), 3389, "server.esempio.lan.", TimeSpan.Zero)),   // nuovo
            DiscoveredPc.FromScan(new ScanHit(IPAddress.Parse("192.168.1.50"), 3389, "server.esempio.lan.", TimeSpan.Zero)),   // doppione nella selezione
        };
        Assert.True(DiscoveryImport.IsAlreadyPresent(doc, found[0]));
        Assert.False(DiscoveryImport.IsAlreadyPresent(doc, found[1]));

        var result = DiscoveryImport.ImportInto(doc, found, "Rete/Ufficio");

        Assert.Equal(4, result.Duplicates);
        Assert.Equal(["senza-dns", "192.168.1.40", "server"], result.Added.Select(c => c.Name));
        Assert.Equal(["100.64.0.4", "192.168.1.40", "192.168.1.50"], result.Added.Select(c => c.Host));
        Assert.All(result.Added, c => Assert.Equal("Rete/Ufficio", c.Group));
        Assert.All(result.Added, c => Assert.Equal("", c.Username));
        Assert.Contains("Rete/Ufficio", doc.AllGroups());
        Assert.Equal(7, doc.Connections.Count);

        var again = DiscoveryImport.ImportInto(doc, found, "Rete/Ufficio");
        Assert.Empty(again.Added);
        Assert.Equal(7, again.Duplicates);
    }

    [Fact]
    public void Import_DifferentIpsAreNeverTheSamePc()
    {
        var doc = new ConnectionsDocument { Connections = [new RdpConnection { Host = "10.0.0.1" }] };
        var pc = new DiscoveredPc("10.0.0.10", "10.0.0.10", 3389, "Rete locale");
        Assert.False(DiscoveryImport.IsAlreadyPresent(doc, pc));
        var r = DiscoveryImport.ImportInto(doc, [pc], "");
        Assert.Single(r.Added);
        Assert.Equal("", r.Added[0].Group);
    }

    // ---------- compatibilità ----------

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    [InlineData("1.4.0")]
    [InlineData("1.5.0")]
    public void OldFiles_LoadAddFoundPcsAndSaveWithoutLosingAnything(string version)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json"), path);
        var store = new ConnectionStore(path);
        var doc = store.Load();
        var before = doc.Connections.Select(c => (c.Id, c.Name, c.Host, c.Port, c.Group)).ToList();
        Assert.NotEmpty(before);

        // Un PC già presente nel file non si aggiunge di nuovo.
        var existing = doc.Connections[0];
        var dup = new DiscoveredPc(existing.Name, existing.Host, existing.Port, "Rete locale");
        var fresh = new DiscoveredPc("nuovo-pc", "192.168.77.77", 3389, "Rete locale");
        var r = DiscoveryImport.ImportInto(doc, [dup, fresh]);
        Assert.Equal(1, r.Duplicates);
        store.Save(doc);

        var reloaded = new ConnectionStore(path).Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, reloaded.Version);
        Assert.Equal(before, reloaded.Connections.Take(before.Count).Select(c => (c.Id, c.Name, c.Host, c.Port, c.Group)).ToList());
        var added = Assert.Single(reloaded.Connections.Skip(before.Count));
        Assert.Equal(("nuovo-pc", "192.168.77.77", DiscoveryImport.DefaultFolder), (added.Name, added.Host, added.Group));
    }
}
