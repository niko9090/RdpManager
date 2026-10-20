using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

public class RdpFileTests
{
    private const string SampleRdp =
        "screen mode id:i:1\r\n" +
        "use multimon:i:0\r\n" +
        "desktopwidth:i:1280\r\n" +
        "desktopheight:i:720\r\n" +
        "session bpp:i:32\r\n" +
        "full address:s:server.contoso.com:3391\r\n" +
        "username:s:CONTOSO\\anna\r\n" +
        "redirectclipboard:i:0\r\n" +
        "drivestoredirect:s:*\r\n" +
        "prompt for credentials:i:0\r\n";

    [Fact]
    public void Parse_ReadsMainSettings()
    {
        var c = RdpFile.Parse(SampleRdp, "Contoso");

        Assert.Equal("Contoso", c.Name);
        Assert.Equal("server.contoso.com", c.Host);
        Assert.Equal(3391, c.Port);
        Assert.Equal("CONTOSO", c.Domain);
        Assert.Equal("anna", c.Username);
        Assert.Equal(ResolutionMode.Fixed, c.ResolutionMode);
        Assert.Equal(1280, c.DesktopWidth);
        Assert.Equal(720, c.DesktopHeight);
        Assert.False(c.RedirectClipboard);
        Assert.True(c.RedirectDrives);
        Assert.Null(c.Password);
    }

    [Fact]
    public void Parse_DefaultsAndFullScreenMeanFitToWindow()
    {
        var c = RdpFile.Parse("full address:s:10.0.0.5\nscreen mode id:i:2\ndesktopwidth:i:1920\ndesktopheight:i:1080\nusername:s:admin\ndomain:s:LAB\n");
        Assert.Equal("10.0.0.5", c.Host);
        Assert.Equal("10.0.0.5", c.Name);
        Assert.Equal(RdpConnection.DefaultPort, c.Port);
        Assert.Equal("admin", c.Username);
        Assert.Equal("LAB", c.Domain);
        Assert.Equal(ResolutionMode.FitToWindow, c.ResolutionMode);
        Assert.True(c.RedirectClipboard);
        Assert.False(c.RedirectDrives);
    }

    [Fact]
    public void Parse_ServerPortLineIsUsedWhenAddressHasNoPort()
    {
        var c = RdpFile.Parse("full address:s:pc\nserver port:i:4000\n");
        Assert.Equal(4000, c.Port);
    }

    [Fact]
    public void Parse_IgnoresGarbageAndKeyCase()
    {
        var c = RdpFile.Parse("﻿riga senza separatori\n\nFull Address:s:pc-01\nxyz\n");
        Assert.Equal("pc-01", c.Host);
    }

    [Fact]
    public void Parse_DecryptsPassword51()
    {
        var hex = PasswordProtector.ProtectForRdpFile("dalfile");
        var c = RdpFile.Parse($"full address:s:pc\npassword 51:b:{hex}\n");
        Assert.Equal("dalfile", c.Password);
    }

    [Fact]
    public void Generate_WritesExpectedLines()
    {
        var c = new RdpConnection
        {
            Host = "srv", Port = 3390, Username = "luca", Domain = "DOM",
            ResolutionMode = ResolutionMode.Fixed, DesktopWidth = 1440, DesktopHeight = 900,
            RedirectClipboard = true, RedirectDrives = true, Password = "segreta",
        };
        var text = RdpFile.Generate(c);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("full address:s:srv:3390", lines);
        Assert.Contains("username:s:DOM\\luca", lines);
        Assert.Contains("desktopwidth:i:1440", lines);
        Assert.Contains("desktopheight:i:900", lines);
        Assert.Contains("redirectclipboard:i:1", lines);
        Assert.Contains("drivestoredirect:s:*", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("password 51", StringComparison.Ordinal));
        Assert.DoesNotContain("segreta", text);
    }

    [Fact]
    public void Generate_WithPassword_WritesOnlyEncryptedPassword()
    {
        var c = new RdpConnection { Host = "srv", Password = "segreta" };
        var text = RdpFile.Generate(c, includePassword: true);
        Assert.Contains("password 51:b:", text);
        Assert.DoesNotContain("segreta", text);
        Assert.Equal("segreta", RdpFile.Parse(text).Password);
    }

    [Fact]
    public void GenerateThenParse_RoundTrips()
    {
        var original = new RdpConnection
        {
            Host = "fe80::1", Port = 3399, Username = "u", Domain = "D",
            ResolutionMode = ResolutionMode.FitToWindow, RedirectClipboard = false, RedirectDrives = false,
        };
        var parsed = RdpFile.Parse(RdpFile.Generate(original));
        Assert.Equal(original.Host, parsed.Host);
        Assert.Equal(original.Port, parsed.Port);
        Assert.Equal(original.Username, parsed.Username);
        Assert.Equal(original.Domain, parsed.Domain);
        Assert.Equal(ResolutionMode.FitToWindow, parsed.ResolutionMode);
        Assert.False(parsed.RedirectClipboard);
        Assert.False(parsed.RedirectDrives);

        var fixedRes = new RdpConnection { Host = "h", ResolutionMode = ResolutionMode.Fixed, DesktopWidth = 1024, DesktopHeight = 768 };
        var parsedFixed = RdpFile.Parse(RdpFile.Generate(fixedRes));
        Assert.Equal(ResolutionMode.Fixed, parsedFixed.ResolutionMode);
        Assert.Equal(1024, parsedFixed.DesktopWidth);
        Assert.Equal(768, parsedFixed.DesktopHeight);
    }

    [Fact]
    public void SaveAndLoad_UsesUtf16FileAndFileNameAsName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Ufficio Milano {Guid.NewGuid():N}.rdp");
        try
        {
            RdpFile.Save(path, new RdpConnection { Host = "mi-srv", Username = "è" });
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
            var c = RdpFile.Load(path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), c.Name);
            Assert.Equal("mi-srv", c.Host);
            Assert.Equal("è", c.Username);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("pc", "pc", null)]
    [InlineData("pc:3390", "pc", 3390)]
    [InlineData("10.1.2.3:4000", "10.1.2.3", 4000)]
    [InlineData("[fe80::1]:3390", "fe80::1", 3390)]
    [InlineData("[fe80::1]", "fe80::1", null)]
    [InlineData("fe80::1", "fe80::1", null)]
    [InlineData("pc:abc", "pc:abc", null)]
    public void SplitAddress_HandlesCommonForms(string input, string host, int? port)
    {
        Assert.Equal((host, port), RdpFile.SplitAddress(input));
    }
}

public class RdpConnectionTests
{
    [Fact]
    public void Matches_SearchesAcrossFieldsCaseInsensitive()
    {
        var c = new RdpConnection { Name = "Server Contabilità", Host = "10.0.0.7", Username = "anna", Group = "Clienti/Rossi" };
        Assert.True(c.Matches(""));
        Assert.True(c.Matches("contab"));
        Assert.True(c.Matches("rossi anna"));
        Assert.True(c.Matches("10.0.0"));
        Assert.False(c.Matches("bianchi"));
    }

    [Fact]
    public void Duplicate_CreatesNewIdAndKeepsSettings()
    {
        var c = new RdpConnection { Name = "A", Host = "h", Password = "p", Group = "G" };
        var d = c.Duplicate();
        Assert.NotEqual(c.Id, d.Id);
        Assert.Equal("A (copia)", d.Name);
        Assert.Equal("h", d.Host);
        Assert.Equal("p", d.Password);
        Assert.Equal("G", d.Group);
    }

    [Theory]
    [InlineData(" Clienti / Rossi ", "Clienti/Rossi")]
    [InlineData("a\\b//c/", "a/b/c")]
    [InlineData(null, "")]
    public void NormalizeGroup_CleansSeparators(string? input, string expected)
    {
        Assert.Equal(expected, RdpConnection.NormalizeGroup(input));
    }

    [Fact]
    public void DisconnectReasons_DescribesKnownAndUnknown()
    {
        Assert.Contains("DNS", DisconnectReasons.Describe(260));
        Assert.Contains("99999", DisconnectReasons.Describe(99999));
        Assert.True(DisconnectReasons.IsUserInitiated(1));
        Assert.False(DisconnectReasons.IsUserInitiated(264));
    }
}
