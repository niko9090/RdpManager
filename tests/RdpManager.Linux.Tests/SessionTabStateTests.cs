using RdpManager.Core;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

public class SessionTabStateTests
{
    private const string Green = "#2EA043";
    private const string Red = "#C83C32";

    private static FreeRdpSession NewSession() =>
        Assert.IsType<FreeRdpSession>(
            new FreeRdpSessionHost(isInstalled: n => n == "xfreerdp3").Open(new RdpConnection { Name = "PC", Host = "pc.example.com" }));

    // Il processo vero non si avvia nelle prove: si porta la sessione nello stato voluto come farebbe l'avvio.
    private static void SetRunning(FreeRdpSession s, bool value) =>
        typeof(FreeRdpSession).GetProperty(nameof(FreeRdpSession.IsRunning))!.SetValue(s, value);

    [Fact]
    public void StateColor_TurnsGreenWhenSessionConnects_WithoutTitleChange()
    {
        var session = NewSession();
        var tab = new SessionTabViewModel(session, _ => { });
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(Red, tab.StateColor);

        SetRunning(session, true);

        Assert.Equal(Green, tab.StateColor);
        Assert.True(tab.IsConnected);
        Assert.Contains(nameof(SessionTabViewModel.StateColor), changed);
    }

    [Fact]
    public void StateColor_ReturnsRedOnDisconnect_AndGreenAgainOnReconnect()
    {
        var session = NewSession();
        var tab = new SessionTabViewModel(session, _ => { });
        SetRunning(session, true);
        Assert.Equal(Green, tab.StateColor);

        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        SetRunning(session, false);
        Assert.Equal(Red, tab.StateColor);
        Assert.Contains(nameof(SessionTabViewModel.StateColor), changed);

        SetRunning(session, true);
        Assert.Equal(Green, tab.StateColor);
    }
}
