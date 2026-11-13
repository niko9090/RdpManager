using System.Text.RegularExpressions;

namespace RdpManager.Tests;

/// <summary>
/// Controlli leggeri sui file dei pacchetti Linux (packaging/linux): coerenza delle versioni, voce di menu,
/// dipendenze e script di costruzione. I pacchetti veri si costruiscono in WSL (scripts/build-linux.ps1).
/// </summary>
public class LinuxPackagingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RdpManager.sln non trovato");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    private static string VersionOf(string csproj) =>
        Regex.Match(Read(csproj.Split('/')), "<Version>([^<]+)</Version>").Groups[1].Value;

    [Fact]
    public void LinuxVersionMatchesWindowsInstallerVersion()
    {
        // Versione unica in src/Directory.Build.props: i csproj di Windows e Linux non ne hanno una propria.
        Assert.Matches(@"^\d+\.\d+\.\d+$", VersionOf("src/Directory.Build.props"));
        Assert.Equal("", VersionOf("src/RdpManager/RdpManager.csproj"));
        Assert.Equal("", VersionOf("src/RdpManager.Linux/RdpManager.Linux.csproj"));
    }

    [Fact]
    public void DesktopEntryIsValid()
    {
        var lines = Read("packaging", "linux", "rdpmanager.desktop")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("[Desktop Entry]", lines[0]);
        var keys = lines.Skip(1).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("Application", keys["Type"]);
        Assert.Equal("rdpmanager", keys["Icon"]);
        Assert.StartsWith("rdpmanager", keys["Exec"]);
        Assert.EndsWith(";", keys["Categories"]);
        Assert.False(string.IsNullOrWhiteSpace(keys["Name"]));
    }

    [Fact]
    public void IconIsAPng()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "packaging", "linux", "rdpmanager.png"));
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
    }

    [Fact]
    public void BuildScriptDeclaresPackageDependencies()
    {
        var script = Read("packaging", "linux", "build-packages.sh");
        Assert.StartsWith("#!/usr/bin/env bash", script);
        Assert.Contains("Depends: freerdp3-x11 | freerdp2-x11", script);
        Assert.Contains("libsecret-1-0", script);
        Assert.Contains("Recommends: wireguard-tools", script);
        Assert.Contains("Requires:       freerdp", script);
        Assert.Contains("Requires:       libsecret", script);
        Assert.Contains("ln -s \"/opt/rdpmanager/$BIN\"", script);
        Assert.Contains("BIN=RdpManager.Linux", script);
    }

    [Fact]
    public void LinuxBuildIsSafeWhenRunInParallel()
    {
        // Le prove Linux del cancello di unione ricostruiscono insieme: niente cartella di publish condivisa,
        // dotnet publish serializzato da un mutex di sistema, consegna in dist con rinomina atomica.
        var build = Read("scripts", "build-linux.ps1");
        Assert.Contains("publish-linux-' + [guid]::NewGuid()", build);
        Assert.DoesNotContain("'artifacts\\publish-linux'", build);
        Assert.Contains("Global\\RdpManagerBuildLinux-", build);
        Assert.Contains("WaitOne()", build);
        var script = Read("packaging", "linux", "build-packages.sh");
        Assert.Contains("mv -f \"$tmp\" \"$dst\"", script);
        Assert.Contains("$TOOL.$$.part", script);
    }

    [Fact]
    public void ReleaseScriptsShipLinuxPackagesWithChecksums()
    {
        var installer = Read("build-installer.ps1");
        Assert.Contains("scripts\\build-linux.ps1", installer);
        Assert.Contains("x86_64.AppImage", installer);
        Assert.Contains("amd64.deb", installer);
        Assert.Contains("x86_64.rpm", installer);
        var release = Read("scripts", "release.ps1");
        Assert.Contains("$linux", release);
    }
}
