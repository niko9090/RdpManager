using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Sezione «Schermi» di ConnectionDialog (carica/salva delle tre modalità) e scelta dello schermo intero.</summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class MonitorDialogTests
{
    private static readonly ScreenInfo Left = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true);
    private static readonly ScreenInfo Right = new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 1280, 1024), new Rectangle(1920, 0, 1280, 984), false);
    private static readonly ScreenInfo[] Two = [Left, Right];

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = StaTestRunner.Create(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(StaTestRunner.LimitSeconds)), "La prova UI non è terminata entro 60 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Ok(ConnectionDialog dlg) =>
        typeof(ConnectionDialog).GetMethod("OnOk", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dlg, [null, EventArgs.Empty]);

    private static RdpConnection Valid(Action<RdpConnection>? setup = null)
    {
        var c = new RdpConnection { Name = "Uno", Host = "host-inesistente.invalid" };
        setup?.Invoke(c);
        return c;
    }

    [Fact]
    public void Single_IsDefault_AndSaves()
    {
        OnStaThread(() =>
        {
            var c = Valid();
            using var dlg = new ConnectionDialog(c, [], "T", screens: Two);
            Assert.True(dlg.MonitorSingle.Checked);
            Assert.All(dlg.MonitorBoxes, b => Assert.False(b.Enabled));
            Ok(dlg);
            Assert.Equal(MonitorMode.Single, c.MonitorMode);
        });
    }

    [Fact]
    public void All_LoadsAndSaves()
    {
        OnStaThread(() =>
        {
            var c = Valid(x => x.MonitorMode = MonitorMode.Single);
            using (var dlg = new ConnectionDialog(c, [], "T", screens: Two))
            {
                dlg.MonitorAll.Checked = true;
                Ok(dlg);
            }
            Assert.Equal(MonitorMode.All, c.MonitorMode);
            Assert.True(c.UseAllMonitors);
            using var again = new ConnectionDialog(c, [], "T", screens: Two);
            Assert.True(again.MonitorAll.Checked);
        });
    }

    [Fact]
    public void Selected_SavesIndexesAndDevices_AndReloads()
    {
        OnStaThread(() =>
        {
            var c = Valid();
            using (var dlg = new ConnectionDialog(c, [], "T", screens: Two))
            {
                dlg.MonitorSelected.Checked = true;
                Assert.All(dlg.MonitorBoxes, b => Assert.True(b.Enabled));
                Assert.Equal("Schermo 1: \\\\.\\DISPLAY1 (1920×1080, principale)", dlg.MonitorBoxes[0].Text);
                Assert.Equal("Schermo 2: \\\\.\\DISPLAY2 (1280×1024)", dlg.MonitorBoxes[1].Text);
                dlg.MonitorBoxes[1].Checked = true;
                Ok(dlg);
            }
            Assert.Equal(MonitorMode.Selected, c.MonitorMode);
            Assert.Equal([1], c.SelectedMonitors);
            Assert.Equal([@"\\.\DISPLAY2"], c.SelectedMonitorDevices);

            using var again = new ConnectionDialog(c, [], "T", screens: Two);
            Assert.True(again.MonitorSelected.Checked);
            Assert.Equal([false, true], again.MonitorBoxes.Select(b => b.Checked).ToArray());
        });
    }

    [Fact]
    public void Selected_WithNoBox_IsAnError_AndNothingIsSaved()
    {
        OnStaThread(() =>
        {
            var c = Valid();
            using var dlg = new ConnectionDialog(c, [], "T", screens: Two);
            dlg.MonitorSelected.Checked = true;
            Assert.NotNull(dlg.MonitorError);
            dlg.MonitorBoxes[0].Checked = true;
            Assert.Null(dlg.MonitorError);
            dlg.MonitorBoxes[0].Checked = false;
            dlg.MonitorSingle.Checked = true;
            Assert.Null(dlg.MonitorError);
        });
    }

    [Fact]
    public void SavedScreenNotConnected_IsShownAndKept()
    {
        OnStaThread(() =>
        {
            var c = Valid(x =>
            {
                x.MonitorMode = MonitorMode.Selected;
                x.SelectedMonitors = [0, 2];
                x.SelectedMonitorDevices = [@"\\.\DISPLAY1", @"\\.\DISPLAY3"];
            });
            using var dlg = new ConnectionDialog(c, [], "T", screens: Two);
            var boxes = dlg.MonitorBoxes;
            Assert.Equal(3, boxes.Count);
            Assert.Equal("Schermo 3: \\\\.\\DISPLAY3 (non collegato)", boxes[2].Text);
            Assert.Equal([true, false, true], boxes.Select(b => b.Checked).ToArray());
            Ok(dlg);
            Assert.Equal([0, 2], c.SelectedMonitors);
            Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY3"], c.SelectedMonitorDevices);
        });
    }

    [Fact]
    public void NotSelectedMode_KeepsPreviousChoice()
    {
        OnStaThread(() =>
        {
            var c = Valid(x => { x.MonitorMode = MonitorMode.Selected; x.SelectedMonitors = [1]; x.SelectedMonitorDevices = [@"\\.\DISPLAY2"]; });
            using var dlg = new ConnectionDialog(c, [], "T", screens: Two);
            dlg.MonitorSingle.Checked = true;
            Ok(dlg);
            Assert.Equal(MonitorMode.Single, c.MonitorMode);
            Assert.Equal([1], c.SelectedMonitors);
        });
    }

    [Fact]
    public void FullScreenBounds_FollowsTheChoice()
    {
        var virtualBounds = MonitorLayout.VirtualBounds(Two);
        Assert.Equal(Left.Bounds, Screens.FullScreenBounds(Two, Valid(), null));
        Assert.Equal(virtualBounds, Screens.FullScreenBounds(Two, Valid(x => x.MonitorMode = MonitorMode.All), null));
        Assert.Equal(Right.Bounds, Screens.FullScreenBounds(Two, Valid(x => { x.MonitorMode = MonitorMode.Selected; x.SelectedMonitors = [1]; }), null));
        Assert.Equal(virtualBounds, Screens.FullScreenBounds(Two, Valid(x => { x.MonitorMode = MonitorMode.Selected; x.SelectedMonitors = [0, 1]; }), null));
        // schermo scelto non più collegato: ripiego su tutti
        Assert.Equal(virtualBounds, Screens.FullScreenBounds(Two, Valid(x => { x.MonitorMode = MonitorMode.Selected; x.SelectedMonitors = [5]; }), null));
        Assert.Equal(Left.Bounds, Screens.FullScreenBounds([Left], Valid(x => x.MonitorMode = MonitorMode.All), null));
    }
}
