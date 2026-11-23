using System.Text.RegularExpressions;
using RdpManager.Linux.ViewModels;
using Xunit;

namespace RdpManager.Linux.Tests;

/// <summary>I colori di stato sono quelli di Windows (RdpSessionView.StateColor) e stanno in un posto solo.</summary>
public class UiColorsTests
{
    [Fact]
    public void StatusColors_MatchWindowsValues()
    {
        // Windows: Color.FromArgb(46, 160, 67) e (200, 60, 50); albero: grigio 150.
        Assert.Equal("#2EA043", UiColors.Connected);
        Assert.Equal("#C83C32", UiColors.Disconnected);
        Assert.Equal("#969696", UiColors.Unreachable);
    }

    [Theory]
    [InlineData("src/RdpManager.Linux/MainWindow.axaml")]
    [InlineData("src/RdpManager.Linux/Menus/MainMenu.axaml")]
    public void MainViews_HaveNoScatteredColorCodes(string file)
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile(file));
        Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{6,8}\b"), axaml);
    }
}
