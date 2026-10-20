using System.Drawing;
using System.Windows.Forms;
using RdpManager.Core;

namespace RdpManager.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Destra", true, true, false, Keys.Right)]
    [InlineData("ctrl + alt + right", true, true, false, Keys.Right)]
    [InlineData("Ctrl+Alt+Sinistra", true, true, false, Keys.Left)]
    [InlineData("Ctrl+Alt+1", true, true, false, Keys.D1)]
    [InlineData("Ctrl+Alt+9", true, true, false, Keys.D9)]
    [InlineData("Ctrl+Alt+Invio", true, true, false, Keys.Enter)]
    [InlineData("Alt+Ctrl+Enter", true, true, false, Keys.Enter)]
    [InlineData("Ctrl+Maiusc+F5", true, false, true, Keys.F5)]
    [InlineData("Control+Shift+b", true, false, true, Keys.B)]
    [InlineData("Ctrl+Alt+PagGiù", true, true, false, Keys.PageDown)]
    public void Parse(string text, bool ctrl, bool alt, bool shift, Keys key)
    {
        Assert.True(KeyCombo.TryParse(text, out var combo));
        Assert.Equal(new KeyCombo(ctrl, alt, shift, key), combo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+Destra+Sinistra")]
    [InlineData("Ctrl++")]
    [InlineData("Ctrl+Alt+Boh")]
    [InlineData("Ctrl+F25")]
    public void Parse_Invalid(string? text)
    {
        Assert.False(KeyCombo.TryParse(text, out _));
    }

    [Theory]
    [InlineData("ctrl+alt+right", "Ctrl+Alt+Destra")]
    [InlineData("alt+ctrl+left", "Ctrl+Alt+Sinistra")]
    [InlineData("Ctrl+Alt+Return", "Ctrl+Alt+Invio")]
    [InlineData("shift+ctrl+3", "Ctrl+Maiusc+3")]
    [InlineData("Ctrl+Alt+f12", "Ctrl+Alt+F12")]
    public void Format_IsItalianAndCanonical(string text, string expected)
    {
        var formatted = KeyCombo.Parse(text).ToString();
        Assert.Equal(expected, formatted);
        Assert.Equal(KeyCombo.Parse(text), KeyCombo.Parse(formatted));
    }

    [Fact]
    public void FromKeys_AndVirtualKey_MatchParsedCombo()
    {
        var expected = KeyCombo.Parse("Ctrl+Alt+Destra");
        Assert.Equal(expected, KeyCombo.FromKeys(Keys.Control | Keys.Alt | Keys.Right));
        Assert.Equal(expected, KeyCombo.FromVirtualKey(0x27, ctrl: true, alt: true, shift: false));
        Assert.Equal(Keys.Control | Keys.Alt | Keys.Right, expected.ToKeys());
        // Premere solo i modificatori non forma una combinazione.
        Assert.True(KeyCombo.FromVirtualKey((int)Keys.LControlKey, true, false, false).IsEmpty);
    }

    [Fact]
    public void DefaultMap_HasRequestedShortcuts()
    {
        var map = new ShortcutMap();
        Assert.Equal("Ctrl+Alt+Destra", map[ShortcutAction.NextSession].ToString());
        Assert.Equal("Ctrl+Alt+Sinistra", map[ShortcutAction.PreviousSession].ToString());
        Assert.Equal("Ctrl+Alt+Invio", map[ShortcutAction.ToggleFullScreen].ToString());
        for (int n = 1; n <= 9; n++)
            Assert.Equal($"Ctrl+Alt+{n}", map[ShortcutMap.SessionAction(n)].ToString());
        Assert.Empty(map.Conflicts());
    }

    [Fact]
    public void TryGetAction_MapsKeyToAction()
    {
        var map = new ShortcutMap();
        Assert.True(map.TryGetAction(Keys.Control | Keys.Alt | Keys.D3, out var action));
        Assert.Equal(ShortcutAction.Session3, action);
        Assert.Equal(3, ShortcutMap.SessionNumber(action));
        Assert.True(map.TryGetAction(KeyCombo.Parse("Ctrl+Alt+Sinistra"), out action));
        Assert.Equal(ShortcutAction.PreviousSession, action);
        Assert.False(map.TryGetAction(Keys.Control | Keys.Right, out _));
        Assert.False(map.TryGetAction(default(KeyCombo), out _));
    }

    [Fact]
    public void Settings_RoundTripOnlyChanges_AndIgnoreInvalid()
    {
        var map = new ShortcutMap();
        map[ShortcutAction.NextSession] = KeyCombo.Parse("Ctrl+Alt+PagGiù");
        map[ShortcutAction.ToggleSidebar] = default; // disattivata
        var saved = map.ToSettings();
        Assert.Equal(new Dictionary<string, string> { ["NextSession"] = "Ctrl+Alt+PagGiù", ["ToggleSidebar"] = "" }, saved);

        saved["PreviousSession"] = "Destra";            // senza Ctrl/Alt: rifiutata
        saved["Session1"] = "non valida";              // illeggibile: rifiutata
        saved["AzioneSconosciuta"] = "Ctrl+Alt+X";      // ignorata
        var loaded = ShortcutMap.FromSettings(saved);
        Assert.Equal(KeyCombo.Parse("Ctrl+Alt+PagGiù"), loaded[ShortcutAction.NextSession]);
        Assert.True(loaded[ShortcutAction.ToggleSidebar].IsEmpty);
        Assert.Equal(ShortcutMap.Defaults[ShortcutAction.PreviousSession], loaded[ShortcutAction.PreviousSession]);
        Assert.Equal(ShortcutMap.Defaults[ShortcutAction.Session1], loaded[ShortcutAction.Session1]);
    }

    [Fact]
    public void Conflicts_AreReported()
    {
        var map = new ShortcutMap();
        map[ShortcutAction.ToggleSidebar] = map[ShortcutAction.NextSession];
        var conflict = Assert.Single(map.Conflicts());
        Assert.Equal([ShortcutAction.NextSession, ShortcutAction.ToggleSidebar], conflict);
    }
}

public class SessionCyclerTests
{
    private static readonly bool[] Mixed = [true, false, true, true, false];

    [Theory]
    [InlineData(0, true, 2)]
    [InlineData(2, true, 3)]
    [InlineData(3, true, 0)]   // salta l'ultima non connessa e ricomincia
    [InlineData(0, false, 3)]  // all'indietro dal primo va all'ultimo connesso
    [InlineData(3, false, 2)]
    [InlineData(2, false, 0)]  // salta la scheda 1 non connessa
    [InlineData(1, true, 2)]   // dalla scheda corrente non connessa
    [InlineData(-1, true, 0)]
    [InlineData(-1, false, 3)]
    public void Next_SkipsDisconnectedAndWraps(int current, bool forward, int expected) =>
        Assert.Equal(expected, SessionCycler.Next(Mixed, current, forward));

    [Fact]
    public void Next_EdgeCases()
    {
        Assert.Equal(-1, SessionCycler.Next([], 0));
        Assert.Equal(-1, SessionCycler.Next([false, false], 0));
        Assert.Equal(1, SessionCycler.Next([false, true], 1));     // solo la corrente: resta lì
        Assert.Equal(1, SessionCycler.Previous([false, true, false], 1));
    }

    [Fact]
    public void Nth_CountsOnlyConnected()
    {
        Assert.Equal(0, SessionCycler.Nth(Mixed, 1));
        Assert.Equal(2, SessionCycler.Nth(Mixed, 2));
        Assert.Equal(3, SessionCycler.Nth(Mixed, 3));
        Assert.Equal(-1, SessionCycler.Nth(Mixed, 4));
        Assert.Equal(-1, SessionCycler.Nth(Mixed, 0));
    }

    [Fact]
    public void Target_FromShortcutAction()
    {
        Assert.Equal(2, SessionCycler.Target(ShortcutAction.NextSession, Mixed, 0));
        Assert.Equal(3, SessionCycler.Target(ShortcutAction.PreviousSession, Mixed, 0));
        Assert.Equal(3, SessionCycler.Target(ShortcutAction.Session3, Mixed, 0));
        Assert.Null(SessionCycler.Target(ShortcutAction.Session9, Mixed, 0));
        Assert.Null(SessionCycler.Target(ShortcutAction.ToggleFullScreen, Mixed, 0));
    }
}

public class MonitorLayoutTests
{
    // Due monitor affiancati: principale 1920x1080 a 96 DPI, secondo 2560x1440 a 144 DPI (150%).
    private static readonly ScreenInfo Primary = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 96);
    private static readonly ScreenInfo Second = new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), false, 144);
    private static readonly ScreenInfo[] Both = [Second, Primary];

    [Fact]
    public void ChooseScreen_SavedPrimaryOrFirst()
    {
        Assert.Equal(Second, MonitorLayout.ChooseScreen(Both, @"\\.\display2"));
        Assert.Equal(Primary, MonitorLayout.ChooseScreen(Both, @"\\.\DISPLAY7")); // non c'è più
        Assert.Equal(Primary, MonitorLayout.ChooseScreen(Both, null));
        Assert.Equal(Second, MonitorLayout.ChooseScreen([Second], null));
        Assert.Null(MonitorLayout.ChooseScreen([], "x"));
    }

    [Fact]
    public void ScreenOf_LargestOverlapOrNearest()
    {
        Assert.Equal(Second, MonitorLayout.ScreenOf(Both, new Rectangle(1800, 100, 800, 600)));
        Assert.Equal(Primary, MonitorLayout.ScreenOf(Both, new Rectangle(-900, 100, 800, 600)));
    }

    [Fact]
    public void NextScreen_LeftToRightWithWrap()
    {
        Assert.Equal(Second, MonitorLayout.NextScreen(Both, Primary));
        Assert.Equal(Primary, MonitorLayout.NextScreen(Both, Second));
        Assert.Equal(Primary, MonitorLayout.NextScreen(Both, Second, forward: false));
        Assert.Equal(Primary, MonitorLayout.NextScreen(Both, null));
    }

    [Fact]
    public void VirtualBounds_CoversAllMonitors() =>
        Assert.Equal(new Rectangle(0, 0, 4480, 1440), MonitorLayout.VirtualBounds(Both));

    [Fact]
    public void MoveToScreen_ScalesByDpiAndStaysInside()
    {
        var moved = MonitorLayout.MoveToScreen(new Rectangle(0, 0, 800, 600), Primary, Second);
        Assert.Equal(new Size(1200, 900), moved.Size);
        Assert.Equal(new Point(1920, 0), moved.Location);
        Assert.True(Second.WorkingArea.Contains(moved));

        var back = MonitorLayout.MoveToScreen(moved, Second, Primary);
        Assert.Equal(new Rectangle(0, 0, 800, 600), back);

        var huge = MonitorLayout.MoveToScreen(new Rectangle(0, 0, 1900, 1000), Primary, Second);
        Assert.True(Second.WorkingArea.Contains(huge));
    }

    [Fact]
    public void EnsureVisible_RelocatesWindowsFromMissingMonitor()
    {
        var onPrimary = new Rectangle(100, 100, 800, 600);
        Assert.Equal(onPrimary, MonitorLayout.EnsureVisible(onPrimary, Both));

        // Era sul secondo monitor, che ora non c'è più.
        var orphan = new Rectangle(2500, 200, 800, 600);
        var fixedRect = MonitorLayout.EnsureVisible(orphan, [Primary]);
        Assert.True(Primary.WorkingArea.Contains(fixedRect));
        Assert.Equal(orphan.Size, fixedRect.Size);

        // Più grande del monitor rimasto: viene ridotta.
        var big = MonitorLayout.EnsureVisible(new Rectangle(3000, 0, 2400, 1300), [Primary]);
        Assert.Equal(Primary.WorkingArea, big);
    }

    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(144, 150)]
    [InlineData(192, 200)]
    [InlineData(72, 100)]
    public void DesktopScaleFactor(int dpi, int expected) =>
        Assert.Equal(expected, MonitorLayout.DesktopScaleFactor(dpi));

    [Fact]
    public void ToLogical_DividesByScale() =>
        Assert.Equal(new Size(1707, 960), MonitorLayout.ToLogical(new Size(2560, 1440), 144));
}
