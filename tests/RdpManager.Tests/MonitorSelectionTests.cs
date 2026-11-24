using System.Drawing;
using RdpManager.Core;

namespace RdpManager.Tests;

public class MonitorSelectionTests
{
    private static ScreenInfo Screen(int n, int x, bool primary = false) =>
        new($@"\\.\DISPLAY{n}", new Rectangle(x, 0, 1920, 1080), new Rectangle(x, 0, 1920, 1040), primary);

    private static readonly ScreenInfo[] Two = [Screen(1, 0, true), Screen(2, 1920)];
    private static readonly ScreenInfo[] Three = [Screen(1, 0, true), Screen(2, 1920), Screen(3, 3840)];

    // ---- compatibilità con i file vecchi ----

    [Fact]
    public void Load_OldFixtureWithUseAllMonitors_BecomesAll()
    {
        string path = Path.Combine(Path.GetTempPath(), "mon-" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "connections-1.1.0.json"), path);
        try
        {
            var doc = new ConnectionStore(path).Load();
            int expected = File.ReadAllText(path).Split("\"useAllMonitors\": true").Length - 1;
            Assert.True(expected > 0);
            Assert.Equal(expected, doc.Connections.Count(c => c.MonitorMode == MonitorMode.All && c.UseAllMonitors));
            Assert.All(doc.Connections.Where(c => !c.UseAllMonitors), c => Assert.Equal(MonitorMode.Single, c.MonitorMode));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UseAllMonitors_StaysConsistentWithMode()
    {
        var c = new RdpConnection();
        Assert.Equal(MonitorMode.Single, c.MonitorMode);
        c.UseAllMonitors = true;
        Assert.Equal(MonitorMode.All, c.MonitorMode);
        c.UseAllMonitors = false;
        Assert.Equal(MonitorMode.Single, c.MonitorMode);
        c.MonitorMode = MonitorMode.Selected;
        c.UseAllMonitors = false; // non deve cancellare la scelta
        Assert.Equal(MonitorMode.Selected, c.MonitorMode);
        Assert.False(c.UseAllMonitors);
    }

    // ---- .rdp ----

    [Fact]
    public void Rdp_All_WritesMultimonWithoutSelected_AndRoundTrips()
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "h", MonitorMode = MonitorMode.All });
        Assert.Contains("use multimon:i:1", text);
        Assert.DoesNotContain("selectedmonitors", text);
        Assert.Equal(MonitorMode.All, RdpFile.Parse(text).MonitorMode);
    }

    [Fact]
    public void Rdp_Single_WritesMultimonZero()
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "h" });
        Assert.Contains("use multimon:i:0", text);
        Assert.Equal(MonitorMode.Single, RdpFile.Parse(text).MonitorMode);
    }

    [Fact]
    public void Rdp_Selected_RoundTripsIndexes()
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "h", MonitorMode = MonitorMode.Selected, SelectedMonitors = [1, 0] });
        Assert.Contains("use multimon:i:1", text);
        Assert.Contains("selectedmonitors:s:0,1", text);
        var back = RdpFile.Parse(text);
        Assert.Equal(MonitorMode.Selected, back.MonitorMode);
        Assert.Equal([0, 1], back.SelectedMonitors);
    }

    [Fact]
    public void Rdp_ParseFromMstsc_RecognizesBothForms()
    {
        Assert.Equal(MonitorMode.All, RdpFile.Parse("full address:s:h\r\nuse multimon:i:1\r\n").MonitorMode);
        var sel = RdpFile.Parse("full address:s:h\r\nuse multimon:i:1\r\nselectedmonitors:s:2,0\r\n");
        Assert.Equal(MonitorMode.Selected, sel.MonitorMode);
        Assert.Equal([0, 2], sel.SelectedMonitors);
        Assert.Equal(MonitorMode.Single, RdpFile.Parse("full address:s:h\r\nselectedmonitors:s:0,1\r\n").MonitorMode);
    }

    [Fact]
    public void Rdp_SelectedWithEmptyList_WritesAsSingle()
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "h", MonitorMode = MonitorMode.Selected });
        Assert.Contains("use multimon:i:0", text);
    }

    // ---- .rdpm e copie ----

    [Fact]
    public void Rdpm_RoundTrip_KeepsMonitorChoice()
    {
        var sel = new RdpConnection
        {
            Name = "S", Host = "s", MonitorMode = MonitorMode.Selected, SelectedMonitors = [0, 2],
            SelectedMonitorDevices = [@"\\.\DISPLAY1", @"\\.\DISPLAY3"],
        };
        var all = new RdpConnection { Name = "A", Host = "a", UseAllMonitors = true };
        var json = RdpmFile.Export([sel, all], [], null);
        var doc = RdpmFile.Import(json);
        Assert.Equal(MonitorMode.Selected, doc.Connections[0].MonitorMode);
        Assert.Equal([0, 2], doc.Connections[0].SelectedMonitors);
        Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY3"], doc.Connections[0].SelectedMonitorDevices);
        Assert.Equal(MonitorMode.All, doc.Connections[1].MonitorMode);
        Assert.True(doc.Connections[1].UseAllMonitors);
    }

    [Fact]
    public void Clone_DoesNotShareSelection()
    {
        var a = new RdpConnection { MonitorMode = MonitorMode.Selected, SelectedMonitors = [0, 1] };
        var b = a.Clone();
        b.SelectedMonitors = [2];
        Assert.Equal([0, 1], a.SelectedMonitors);
    }

    // ---- scelta degli schermi ----

    [Fact]
    public void Resolve_Single_IsPrimaryOnly()
    {
        var r = MonitorSelection.Resolve([Screen(2, 1920), Screen(1, 0, true)], new RdpConnection());
        Assert.Equal([1], r.Indexes);
        Assert.False(r.UseMultimon);
        Assert.False(r.FellBack);
    }

    [Fact]
    public void Resolve_All_ReturnsEveryScreen()
    {
        var r = MonitorSelection.Resolve(Three, new RdpConnection { UseAllMonitors = true });
        Assert.Equal([0, 1, 2], r.Indexes);
        Assert.True(r.UseMultimon);
    }

    [Fact]
    public void Resolve_Selected_UsesChosenScreens()
    {
        var r = MonitorSelection.Resolve(Three, new RdpConnection { MonitorMode = MonitorMode.Selected, SelectedMonitors = [0, 2] });
        Assert.Equal([0, 2], r.Indexes);
        Assert.True(r.UseMultimon);
        Assert.False(r.FellBack);
    }

    [Fact]
    public void Resolve_Selected_FindsByDeviceNameWhenIndexesMoved()
    {
        var c = new RdpConnection
        {
            MonitorMode = MonitorMode.Selected, SelectedMonitors = [0, 1],
            SelectedMonitorDevices = [@"\\.\DISPLAY1", @"\\.\DISPLAY3"],
        };
        var r = MonitorSelection.Resolve(Three, c);
        Assert.Equal([0, 2], r.Indexes);
        Assert.False(r.FellBack);
    }

    [Fact]
    public void Resolve_Selected_SomeMissing_UsesTheOthersAndReports()
    {
        var r = MonitorSelection.Resolve(Two, new RdpConnection { MonitorMode = MonitorMode.Selected, SelectedMonitors = [1, 2] });
        Assert.Equal([1], r.Indexes);
        Assert.False(r.UseMultimon);
        Assert.True(r.FellBack);
        Assert.Equal(["#2"], r.MissingDevices);
    }

    [Fact]
    public void Resolve_Selected_AllMissing_FallsBackToAllPresent()
    {
        var r = MonitorSelection.Resolve(Two, new RdpConnection { MonitorMode = MonitorMode.Selected, SelectedMonitors = [3, 4] });
        Assert.Equal([0, 1], r.Indexes);
        Assert.True(r.UseMultimon);
        Assert.True(r.FellBack);
    }

    [Fact]
    public void Resolve_EmptyList_IsEmpty()
    {
        var r = MonitorSelection.Resolve([], new RdpConnection { UseAllMonitors = true });
        Assert.Empty(r.Screens);
        Assert.False(r.UseMultimon);
    }

    [Fact]
    public void FreeRdpArguments_AllSubsetAndSingle()
    {
        var all = MonitorSelection.Resolve(Three, new RdpConnection { UseAllMonitors = true });
        Assert.Equal(["/multimon"], MonitorSelection.FreeRdpArguments(all, Three.Length));
        var sub = MonitorSelection.Resolve(Three, new RdpConnection { MonitorMode = MonitorMode.Selected, SelectedMonitors = [0, 2] });
        Assert.Equal(["/multimon", "/monitors:0,2"], MonitorSelection.FreeRdpArguments(sub, Three.Length));
        var one = MonitorSelection.Resolve(Three, new RdpConnection());
        Assert.Empty(MonitorSelection.FreeRdpArguments(one, Three.Length));
    }
}
