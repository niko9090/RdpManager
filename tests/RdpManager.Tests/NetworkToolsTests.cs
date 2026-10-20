using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RdpManager.Core;

namespace RdpManager.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("00:11:22:aa:bb:cc", "00:11:22:AA:BB:CC")]
    [InlineData("00-11-22-AA-BB-CC", "00:11:22:AA:BB:CC")]
    [InlineData("0011.22aa.bbcc", "00:11:22:AA:BB:CC")]
    [InlineData("001122aabbcc", "00:11:22:AA:BB:CC")]
    [InlineData("  00:11:22:AA:BB:CC ", "00:11:22:AA:BB:CC")]
    public void TryNormalizeMac_AcceptsCommonForms(string input, string expected)
    {
        Assert.True(WakeOnLan.TryNormalizeMac(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.True(WakeOnLan.IsValidMac(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:11:22:33:44")]
    [InlineData("00:11:22:33:44:55:66")]
    [InlineData("00:11:22:33:44:GG")]
    [InlineData("00:11-22:33:44:55")]
    [InlineData("0:11:22:33:44:555")]
    [InlineData("00112233445")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    public void TryNormalizeMac_RejectsInvalid(string? input)
    {
        Assert.False(WakeOnLan.TryNormalizeMac(input, out var normalized));
        Assert.Equal("", normalized);
        Assert.Throws<FormatException>(() => WakeOnLan.ParseMac(input));
    }

    [Fact]
    public void MagicPacket_Is6xFFThenMac16Times()
    {
        var packet = WakeOnLan.BuildMagicPacket("01:23:45:67:89:AB");
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        byte[] mac = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];
        for (int i = 0; i < 16; i++)
            Assert.Equal(mac, packet.Skip(6 + i * 6).Take(6));
    }

    [Fact]
    public void MagicPacket_RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => WakeOnLan.BuildMagicPacket(new byte[5]));

    [Fact]
    public async Task SendAsync_DeliversMagicPacketOverUdp()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var receive = receiver.ReceiveAsync();

        await WakeOnLan.SendAsync("01-23-45-67-89-ab", IPAddress.Loopback, port);

        var done = await Task.WhenAny(receive, Task.Delay(5000));
        Assert.Same(receive, done);
        Assert.Equal(WakeOnLan.BuildMagicPacket("01:23:45:67:89:AB"), (await receive).Buffer);
    }

    [Fact]
    public async Task SendAsync_InvalidMac_Throws() =>
        await Assert.ThrowsAsync<FormatException>(() => WakeOnLan.SendAsync("non-un-mac", IPAddress.Loopback, 9));
}

public class HostReachabilityTests
{
    [Fact]
    public async Task OpenPort_IsReachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await HostReachability.CheckAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
            Assert.True(result.IsReachable);
            Assert.Equal(ReachabilityStatus.Reachable, result.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ClosedPort_IsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await HostReachability.CheckAsync(new RdpConnection { Host = "127.0.0.1", Port = port }, TimeSpan.FromSeconds(5));
        Assert.Equal(ReachabilityStatus.Unreachable, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SilentHost_TimesOutQuickly()
    {
        // 192.0.2.0/24 (TEST-NET-1) non è instradato: la connessione resta appesa fino al timeout.
        var sw = Stopwatch.StartNew();
        var result = await HostReachability.CheckAsync("192.0.2.1", 3389, TimeSpan.FromMilliseconds(300));
        sw.Stop();
        Assert.False(result.IsReachable);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Durata {sw.Elapsed}");
    }

    [Theory]
    [InlineData("", 3389)]
    [InlineData("pc", 0)]
    [InlineData("pc", 70000)]
    public async Task InvalidInput_IsUnreachable(string host, int port) =>
        Assert.False((await HostReachability.CheckAsync(host, port)).IsReachable);

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HostReachability.CheckAsync("192.0.2.1", 3389, TimeSpan.FromSeconds(10), cts.Token));
    }
}
