using RdpManager.Core;
using RdpManager.Platform.Linux;

namespace RdpManager.Tests;

public class FreeRdpCommandTests
{
    private static readonly FreeRdpClient X3 = new("xfreerdp3", true, false);
    private static readonly FreeRdpClient X2 = new("xfreerdp", false, false);
    private static readonly FreeRdpClient Sdl = new("sdl-freerdp3", true, true);

    private static RdpConnection Conn() => new()
    {
        Name = "Server",
        Host = "pc.example.com",
        Username = "mario",
        Domain = "DOM",
        Password = "segreto!",
    };

    [Fact]
    public void Base_HasHostUserDomainAndCertificatePolicy()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), X3);
        Assert.Equal("xfreerdp3", cmd.FileName);
        Assert.Contains("/v:pc.example.com", cmd.Arguments);
        Assert.Contains("/u:mario", cmd.Arguments);
        Assert.Contains("/d:DOM", cmd.Arguments);
        Assert.Contains("/title:Server", cmd.Arguments);
        Assert.Contains("/cert:tofu", cmd.Arguments);
        Assert.DoesNotContain(cmd.Arguments, a => a.StartsWith("/port:"));
    }

    [Fact]
    public void Port_OnlyWhenNotDefault()
    {
        var c = Conn();
        c.Port = 3390;
        Assert.Contains("/port:3390", FreeRdpCommandBuilder.Build(c, X3).Arguments);
    }

    [Fact]
    public void Password_NeverOnCommandLine_V3UsesArgsFromStdin()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), X3);
        Assert.DoesNotContain(cmd.Arguments, a => a.Contains("segreto") || a.StartsWith("/p:"));
        Assert.Contains("/args-from:stdin", cmd.Arguments);
        Assert.Equal(["/p:segreto!"], cmd.StdinLines);
    }

    [Fact]
    public void Password_V2UsesFromStdin()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), X2);
        Assert.DoesNotContain(cmd.Arguments, a => a.Contains("segreto"));
        Assert.Contains("/from-stdin", cmd.Arguments);
        Assert.Contains("/cert-tofu", cmd.Arguments);
        Assert.Equal(["segreto!"], cmd.StdinLines);
    }

    [Fact]
    public void NoPassword_NoStdin()
    {
        var c = Conn();
        c.Password = null;
        var cmd = FreeRdpCommandBuilder.Build(c, X3);
        Assert.Empty(cmd.StdinLines);
        Assert.DoesNotContain("/args-from:stdin", cmd.Arguments);
    }

    [Fact]
    public void Embedded_UsesParentWindowAndTabSize()
    {
        var ctx = new FreeRdpLaunchContext { ParentWindow = 4194311, AvailableWidth = 1000, AvailableHeight = 600 };
        var cmd = FreeRdpCommandBuilder.Build(Conn(), X3, ctx);
        Assert.Contains("/parent-window:4194311", cmd.Arguments);
        Assert.Contains("/size:1000x600", cmd.Arguments);
        Assert.Contains("/dynamic-resolution", cmd.Arguments);
        Assert.DoesNotContain("/f", cmd.Arguments);
    }

    [Fact]
    public void Embedded_FixedResolution_UsesSmartSizing()
    {
        var c = Conn();
        c.ResolutionMode = ResolutionMode.Fixed;
        c.DesktopWidth = 1600;
        c.DesktopHeight = 900;
        var cmd = FreeRdpCommandBuilder.Build(c, X3, new FreeRdpLaunchContext { ParentWindow = 1 });
        Assert.Contains("/size:1600x900", cmd.Arguments);
        Assert.Contains("/smart-sizing", cmd.Arguments);
        Assert.DoesNotContain("/dynamic-resolution", cmd.Arguments);
    }

    [Fact]
    public void SdlClient_CannotEmbed_IgnoresParentWindow()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), Sdl, new FreeRdpLaunchContext { ParentWindow = 99 });
        Assert.DoesNotContain(cmd.Arguments, a => a.StartsWith("/parent-window"));
    }

    [Fact]
    public void MultiMonitor_IsFullscreenMultimon_AndNeverEmbedded()
    {
        var c = Conn();
        c.UseAllMonitors = true;
        var cmd = FreeRdpCommandBuilder.Build(c, X3, new FreeRdpLaunchContext { ParentWindow = 5 });
        Assert.Contains("/f", cmd.Arguments);
        Assert.Contains("/multimon", cmd.Arguments);
        Assert.DoesNotContain(cmd.Arguments, a => a.StartsWith("/parent-window"));
    }

    [Fact]
    public void Detached_Fullscreen()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), Sdl, new FreeRdpLaunchContext { Fullscreen = true });
        Assert.Contains("/f", cmd.Arguments);
        Assert.DoesNotContain(cmd.Arguments, a => a.StartsWith("/size"));
    }

    [Fact]
    public void Size_IsClamped()
    {
        var c = Conn();
        c.ResolutionMode = ResolutionMode.Fixed;
        c.DesktopWidth = 10;
        c.DesktopHeight = 99999;
        Assert.Contains("/size:200x8192", FreeRdpCommandBuilder.Build(c, Sdl).Arguments);
    }

    [Fact]
    public void Devices_Clipboard_Drives_Audio_Printer_Mic()
    {
        var c = Conn();
        c.RedirectClipboard = false;
        c.RedirectDrives = true;
        c.RedirectPrinters = true;
        c.RedirectMicrophone = true;
        c.AudioMode = AudioMode.None;
        var cmd = FreeRdpCommandBuilder.Build(c, X3, new FreeRdpLaunchContext { DrivePath = "/home/mario" });
        Assert.Contains("-clipboard", cmd.Arguments);
        Assert.Contains("/drive:home,/home/mario", cmd.Arguments);
        Assert.Contains("/printer", cmd.Arguments);
        Assert.Contains("/microphone", cmd.Arguments);
        Assert.Contains("/audio-mode:2", cmd.Arguments);
    }

    [Fact]
    public void Defaults_ClipboardAndLocalSound_NoDrives()
    {
        var cmd = FreeRdpCommandBuilder.Build(Conn(), X3, new FreeRdpLaunchContext { DrivePath = "/home/mario" });
        Assert.Contains("+clipboard", cmd.Arguments);
        Assert.Contains("/sound", cmd.Arguments);
        Assert.DoesNotContain(cmd.Arguments, a => a.StartsWith("/drive"));
    }

    [Fact]
    public void Audio_Remote()
    {
        var c = Conn();
        c.AudioMode = AudioMode.Remote;
        Assert.Contains("/audio-mode:1", FreeRdpCommandBuilder.Build(c, X3).Arguments);
    }

    [Theory]
    [InlineData(QualityProfile.Automatic, "/network:auto")]
    [InlineData(QualityProfile.LocalNetwork, "/network:lan")]
    [InlineData(QualityProfile.SlowConnection, "/network:modem")]
    public void Quality(QualityProfile q, string expected)
    {
        var c = Conn();
        c.Quality = q;
        Assert.Contains(expected, FreeRdpCommandBuilder.Build(c, X3).Arguments);
    }

    [Fact]
    public void Gateway_PasswordGoesToStdin()
    {
        var c = Conn();
        c.UseGateway = true;
        c.GatewayHost = "gw.example.com:444";
        var gw = new ResolvedCredentials("gwuser", "GWD", "gwpass");
        var cmd = FreeRdpCommandBuilder.Build(c, X3, new FreeRdpLaunchContext { Gateway = gw });
        Assert.Contains("/g:gw.example.com:444", cmd.Arguments);
        Assert.Contains("/gu:gwuser", cmd.Arguments);
        Assert.Contains("/gd:GWD", cmd.Arguments);
        Assert.DoesNotContain(cmd.Arguments, a => a.Contains("gwpass"));
        Assert.Equal(["/p:segreto!", "/gp:gwpass"], cmd.StdinLines);
    }

    [Fact]
    public void Gateway_Disabled_NotPassed()
    {
        var c = Conn();
        c.UseGateway = false;
        c.GatewayHost = "gw.example.com";
        Assert.DoesNotContain(FreeRdpCommandBuilder.Build(c, X3).Arguments, a => a.StartsWith("/g:"));
    }

    [Fact]
    public void AutoReconnect_PassesRetries()
    {
        var c = Conn();
        c.AutoReconnect = true;
        c.AutoReconnectMaxAttempts = 5;
        var cmd = FreeRdpCommandBuilder.Build(c, X3);
        Assert.Contains("+auto-reconnect", cmd.Arguments);
        Assert.Contains("/auto-reconnect-max-retries:5", cmd.Arguments);
    }

    [Fact]
    public void ToString_DoesNotLeakPassword()
    {
        var text = FreeRdpCommandBuilder.Build(Conn(), X3).ToString();
        Assert.DoesNotContain("segreto", text);
    }

    // ---- Ricerca del client ----

    [Fact]
    public void Locator_Embedded_PrefersXfreerdp3_OverSdl()
    {
        var c = FreeRdpLocator.Find(true, _ => true);
        Assert.Equal("xfreerdp3", c!.Executable);
    }

    [Fact]
    public void Locator_Detached_PrefersSdl()
    {
        Assert.Equal("sdl-freerdp3", FreeRdpLocator.Find(false, _ => true)!.Executable);
    }

    [Fact]
    public void Locator_EmbeddedWithOnlySdl_FallsBackToSdl()
    {
        var c = FreeRdpLocator.Find(true, n => n == "sdl-freerdp");
        Assert.Equal("sdl-freerdp", c!.Executable);
        Assert.False(c.CanEmbed);
    }

    [Fact]
    public void Locator_OnlyLegacyXfreerdp_IsVersion2()
    {
        var c = FreeRdpLocator.Find(false, n => n == "xfreerdp");
        Assert.False(c!.IsVersion3);
    }

    [Fact]
    public void Locator_NothingInstalled_IsNull()
    {
        Assert.Null(FreeRdpLocator.Find(true, _ => false));
    }

    [Fact]
    public void NotInstalledMessage_NamesAptDnfZypper()
    {
        var m = FreeRdpLocator.NotInstalledMessage();
        Assert.Contains("apt", m);
        Assert.Contains("dnf", m);
        Assert.Contains("zypper", m);
    }

    // ---- VPN ----

    [Theory]
    [InlineData("10.66.0.5", true)]
    [InlineData("10.66.0.254", true)]
    [InlineData("10.66.1.5", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("pc.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HostIsOnVpnNetwork(string? host, bool expected) =>
        Assert.Equal(expected, FreeRdpCommandBuilder.HostIsOnVpnNetwork(host));
}
