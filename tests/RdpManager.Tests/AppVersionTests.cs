using System.Text.RegularExpressions;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// La versione mostrata dal programma (titolo, Informazioni, controllo aggiornamenti) deve essere quella di
/// src/Directory.Build.props: il Core è un assembly a parte e senza <Version> comune risultava 1.0.0.
/// </summary>
public class AppVersionTests
{
    private static string PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
            dir = dir.Parent;
        var props = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("RdpManager.sln non trovato"),
            "src", "Directory.Build.props");
        return Regex.Match(File.ReadAllText(props), "<Version>([^<]+)</Version>").Groups[1].Value.Trim();
    }

    [Fact]
    public void VersionText_EqualsProgramVersion_AndIsNotDefault()
    {
        Assert.Equal(PropsVersion(), AppInfo.VersionText);
        Assert.NotEqual("1.0.0", AppInfo.VersionText);
    }
}
