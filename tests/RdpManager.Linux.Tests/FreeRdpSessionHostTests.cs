using RdpManager.Core;
using RdpManager.Linux.Sessions;

namespace RdpManager.Linux.Tests;

public class FreeRdpSessionHostTests
{
    private static RdpConnection Conn() => new() { Name = "PC", Host = "pc.example.com" };

    [Fact]
    public void WithoutFreeRdp_NotAvailable_AndMessageExplainsInstall()
    {
        var host = new FreeRdpSessionHost(isInstalled: _ => false);
        Assert.False(host.IsAvailable);
        Assert.Contains("apt", host.UnavailableReason);
        Assert.Contains("dnf", host.UnavailableReason);
        Assert.Contains("zypper", host.UnavailableReason);
        var session = host.Open(Conn());
        Assert.IsType<MissingFreeRdpSession>(session);
    }

    [Fact]
    public void MissingSession_CloseRaisesClosed()
    {
        var session = new FreeRdpSessionHost(isInstalled: _ => false).Open(Conn());
        var closed = false;
        session.Closed += (_, _) => closed = true;
        session.Close();
        Assert.True(closed);
    }

    [Fact]
    public void WithFreeRdp_IsAvailable_AndOpensSessionWithoutStartingProcess()
    {
        var host = new FreeRdpSessionHost(isInstalled: n => n == "xfreerdp3");
        Assert.True(host.IsAvailable);
        var session = Assert.IsType<FreeRdpSession>(host.Open(Conn()));
        Assert.False(session.IsRunning); // il processo parte solo quando la vista decide come incorporarlo
        Assert.False(session.HasProcess);
        var closed = false;
        session.Closed += (_, _) => closed = true;
        session.Close();
        Assert.True(closed);
    }
}
