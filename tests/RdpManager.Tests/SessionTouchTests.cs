using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Dito dentro la sessione remota (1.9.1): gesti da mouse, adattamento allo schermo, modalità, pulsanti grandi.</summary>
public sealed class SessionTouchGestureTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static TouchGestureRecognizer Recognizer() => new(TimeSpan.FromMilliseconds(500), tolerance: 10, scrollStep: 20, doubleTap: TimeSpan.FromMilliseconds(400));

    private static TouchActionKind[] Kinds(IEnumerable<TouchAction> a) => a.Select(x => x.Kind).ToArray();

    [Fact]
    public void SingleTap_IsOneLeftClickAtTheTouchedPoint()
    {
        var r = Recognizer();
        Assert.Empty(r.Down(1, new Point(100, 100), T0));
        var actions = r.Up(1, new Point(102, 101), T0.AddMilliseconds(80));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.Click], Kinds(actions));
        Assert.Equal(new TouchAction(TouchActionKind.Click, new Point(100, 100), 1), actions[1]);
        Assert.False(r.IsActive);
    }

    [Fact]
    public void SecondQuickTapNearby_IsADoubleClickAtTheFirstPoint()
    {
        var r = Recognizer();
        r.Down(1, new Point(100, 100), T0);
        r.Up(1, new Point(100, 100), T0.AddMilliseconds(60));
        r.Down(1, new Point(104, 103), T0.AddMilliseconds(200));
        var actions = r.Up(1, new Point(104, 103), T0.AddMilliseconds(260));
        var click = actions.Single(a => a.Kind == TouchActionKind.Click);
        Assert.Equal(2, click.Amount);
        Assert.Equal(new Point(100, 100), click.Location);
    }

    [Fact]
    public void SecondTapTooLateOrTooFar_IsAnotherSingleClick()
    {
        var r = Recognizer();
        r.Down(1, new Point(100, 100), T0);
        r.Up(1, new Point(100, 100), T0.AddMilliseconds(60));
        r.Down(1, new Point(100, 100), T0.AddMilliseconds(900));
        Assert.Equal(1, r.Up(1, new Point(100, 100), T0.AddMilliseconds(950)).Single(a => a.Kind == TouchActionKind.Click).Amount);
        r.Down(1, new Point(300, 300), T0.AddMilliseconds(1000));
        Assert.Equal(1, r.Up(1, new Point(300, 300), T0.AddMilliseconds(1050)).Single(a => a.Kind == TouchActionKind.Click).Amount);
    }

    [Fact]
    public void ThirdTapAfterADoubleTap_StartsOverWithASingleClick()
    {
        var r = Recognizer();
        for (int i = 0; i < 2; i++)
        {
            r.Down(1, new Point(50, 50), T0.AddMilliseconds(i * 150));
            r.Up(1, new Point(50, 50), T0.AddMilliseconds(i * 150 + 40));
        }
        r.Down(1, new Point(50, 50), T0.AddMilliseconds(320));
        Assert.Equal(1, r.Up(1, new Point(50, 50), T0.AddMilliseconds(360)).Single(a => a.Kind == TouchActionKind.Click).Amount);
    }

    [Fact]
    public void LongPress_IsARightClickOnce_AndLiftingDoesNotClick()
    {
        var r = Recognizer();
        r.Down(1, new Point(40, 60), T0);
        Assert.Empty(r.Poll(T0.AddMilliseconds(300)));
        var fired = r.Poll(T0.AddMilliseconds(520));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], Kinds(fired));
        Assert.Equal(new Point(40, 60), fired[1].Location);
        Assert.Empty(r.Poll(T0.AddMilliseconds(900)));
        Assert.Empty(r.Up(1, new Point(40, 60), T0.AddMilliseconds(1000)));
    }

    [Fact]
    public void FingerThatMoves_DragsWithTheLeftButtonAndCancelsTheLongPress()
    {
        var r = Recognizer();
        r.Down(1, new Point(100, 100), T0);
        Assert.Empty(r.Move(1, new Point(105, 100), T0.AddMilliseconds(50))); // dentro la tolleranza
        var start = r.Move(1, new Point(130, 100), T0.AddMilliseconds(100));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.LeftDown, TouchActionKind.Move], Kinds(start));
        Assert.Equal(new Point(100, 100), start[1].Location); // la pressione parte da dove è cominciato il tocco
        Assert.Empty(r.Poll(T0.AddMilliseconds(800)));
        var end = r.Up(1, new Point(160, 120), T0.AddMilliseconds(900));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.LeftUp], Kinds(end));
    }

    [Fact]
    public void TwoFingersDragging_ScrollTheWheel_FingerUpScrollsDown()
    {
        var r = Recognizer();
        r.Down(1, new Point(100, 200), T0);
        Assert.Empty(r.Down(2, new Point(160, 200), T0.AddMilliseconds(10)));
        Assert.Empty(r.Move(1, new Point(100, 190), T0.AddMilliseconds(20)));
        var up = r.Move(2, new Point(160, 170), T0.AddMilliseconds(30)); // il centro è salito di 20 px: una tacca
        Assert.Equal(-TouchGestureRecognizer.WheelNotch, up.Single().Amount);
        Assert.Equal(TouchActionKind.Scroll, up.Single().Kind);
        var down = r.Move(1, new Point(100, 240), T0.AddMilliseconds(40)); // il centro scende di 35 px: una tacca in su
        Assert.All(down, a => Assert.True(a.Amount > 0));
        Assert.NotEmpty(down);
    }

    [Fact]
    public void TwoFingers_NeverClick_EvenWhenLiftedWithoutMoving()
    {
        var r = Recognizer();
        r.Down(1, new Point(10, 10), T0);
        r.Down(2, new Point(80, 10), T0.AddMilliseconds(10));
        Assert.Empty(r.Up(2, new Point(80, 10), T0.AddMilliseconds(60)));
        Assert.Empty(r.Up(1, new Point(10, 10), T0.AddMilliseconds(70)));
        Assert.Empty(r.Poll(T0.AddSeconds(2)));
        // finito tutto, un nuovo tocco funziona di nuovo
        r.Down(1, new Point(10, 10), T0.AddSeconds(3));
        Assert.Contains(TouchActionKind.Click, Kinds(r.Up(1, new Point(10, 10), T0.AddSeconds(3).AddMilliseconds(50))));
    }

    [Fact]
    public void SecondFingerWhileDragging_ReleasesTheLeftButton()
    {
        var r = Recognizer();
        r.Down(1, new Point(0, 0), T0);
        r.Move(1, new Point(50, 0), T0.AddMilliseconds(30));
        var actions = r.Down(2, new Point(200, 0), T0.AddMilliseconds(40));
        Assert.Equal([TouchActionKind.LeftUp], Kinds(actions));
    }

    [Fact]
    public void ArmedRightClick_TurnsTheNextTapIntoARightClick_ThenGoesBackToNormal()
    {
        var r = Recognizer();
        r.NextTapIsRightClick = true;
        r.Down(1, new Point(30, 30), T0);
        var actions = r.Up(1, new Point(30, 30), T0.AddMilliseconds(50));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], Kinds(actions));
        Assert.False(r.NextTapIsRightClick);
        r.Down(1, new Point(30, 30), T0.AddSeconds(2));
        Assert.Contains(TouchActionKind.Click, Kinds(r.Up(1, new Point(30, 30), T0.AddSeconds(2).AddMilliseconds(50))));
    }

    [Fact]
    public void Cancel_WhileDragging_ReleasesTheButtonAndForgetsTheFingers()
    {
        var r = Recognizer();
        r.Down(1, new Point(0, 0), T0);
        r.Move(1, new Point(50, 0), T0.AddMilliseconds(30));
        Assert.Equal([TouchActionKind.LeftUp], Kinds(r.Cancel()));
        Assert.False(r.IsActive);
        Assert.Empty(r.Cancel());
    }

    [Theory]
    [InlineData(ResolutionMode.Fixed, 1920, 1080, 1366, 768, true)]
    [InlineData(ResolutionMode.Fixed, 1920, 1080, 1920, 1080, false)]
    [InlineData(ResolutionMode.Fixed, 1280, 720, 1366, 768, false)]
    [InlineData(ResolutionMode.Fixed, 1920, 1080, 1920, 700, true)] // tablet ruotato: troppo basso
    [InlineData(ResolutionMode.FitToWindow, 1920, 1080, 800, 600, false)] // lì la risoluzione si adatta da sola
    [InlineData(ResolutionMode.Fixed, 1920, 1080, 0, 0, false)]
    public void SmartSizing_OnlyForAFixedResolutionBiggerThanTheScreen(ResolutionMode mode, int dw, int dh, int aw, int ah, bool expected) =>
        Assert.Equal(expected, SessionFit.ShouldSmartSize(mode, new Size(dw, dh), new Size(aw, ah)));

    [Fact]
    public void SessionTouchMode_ToggleAndLabels()
    {
        Assert.Equal(SessionTouchMode.Mouse, SessionTouchModes.Toggle(SessionTouchMode.Remote));
        Assert.Equal(SessionTouchMode.Remote, SessionTouchModes.Toggle(SessionTouchMode.Mouse));
        Assert.NotEqual(SessionTouchModes.Short(SessionTouchMode.Remote), SessionTouchModes.Short(SessionTouchMode.Mouse));
        Assert.Equal(SessionTouchMode.Remote, new AppSettings().SessionTouch);
    }

    [Fact]
    public void SessionTarget_IsAtLeast48_AndScalesWithDpi()
    {
        Assert.True(TouchMetrics.SessionTarget >= 48);
        Assert.Equal(72, TouchMetrics.Scale(TouchMetrics.SessionTarget, 144));
    }

    [Fact]
    public void NewSessionActions_AreListedAndDescribed()
    {
        foreach (var a in new[] { SessionBarAction.SendEscape, SessionBarAction.SendWindows, SessionBarAction.RightClick, SessionBarAction.TouchMode, SessionBarAction.HideBar })
        {
            Assert.Contains(a, FullScreenBarLayout.SessionActions);
            Assert.False(string.IsNullOrWhiteSpace(FullScreenBarSettings.Describe(a)));
        }
    }

    [Fact]
    public void TouchModeInTheSettingsFile_SurvivesSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "rdpm-touch-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ConnectionStore(path);
            var doc = store.Load();
            Assert.Equal(SessionTouchMode.Remote, doc.Settings.SessionTouch);
            doc.Settings.SessionTouch = SessionTouchMode.Mouse;
            store.Save(doc);
            Assert.Equal(SessionTouchMode.Mouse, new ConnectionStore(path).Load().Settings.SessionTouch);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class SessionTouchUiTests
{
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    [Fact]
    public void SessionBar_HasTheTouchButtonsAndTheyAreBigEnough()
    {
        OnStaThread(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 700) };
            using var view = new RdpSessionView(new RdpConnection { Name = "Prova", Host = "192.0.2.1" });
            form.Controls.Add(view);
            form.Show();
            Pump(200);
            var items = view.Bar.Items.OfType<ToolStripButton>().ToList();
            foreach (var text in new[] { "Ctrl+Alt+Canc", "Esc", "Win", "Clic destro", "Tastiera", "Schermo intero" })
                Assert.Contains(items, b => b.Text == text);
            int min = TouchUi.Px(view, TouchMetrics.SessionTarget);
            foreach (var b in items.Where(b => b.Text is "Ctrl+Alt+Canc" or "Esc" or "Win" or "Clic destro" or "Tastiera" or "Schermo intero"))
                Assert.True(b.GetPreferredSize(Size.Empty).Height >= min, $"{b.Text}: {b.GetPreferredSize(Size.Empty).Height} < {min}");
        });
    }

    [Fact]
    public void TouchMode_TurnsTheMouseFilterOnAndOff_AndArmedRightClickWorksInBothModes()
    {
        OnStaThread(() =>
        {
            using var view = new RdpSessionView(new RdpConnection { Name = "Prova", Host = "192.0.2.1" });
            Assert.Equal(SessionTouchMode.Remote, view.TouchMode);
            Assert.False(view.TouchInput.Enabled);
            view.TouchMode = SessionTouchMode.Mouse;
            Assert.True(view.TouchInput.Enabled);
            view.TouchMode = SessionTouchMode.Remote;
            Assert.False(view.TouchInput.Enabled);
            view.ArmRightClick();
            Assert.True(view.TouchInput.Enabled);
            Assert.True(view.TouchInput.RightClickArmed);
        });
    }

    [Fact]
    public void Handle_IsShownOnTouchScreensOnlyWhileFullScreenWithTheBarHidden()
    {
        OnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "rdpm-touch-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using var form = new MainForm(new ConnectionStore(path)) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                form.TouchDeviceForTests = true;
                form.Show();
                Pump(300);
                Assert.True(form.FullScreenHandle is null or { Visible: false }); // non a schermo intero
                form.ApplyFullScreenBarForTests(new FullScreenBarSettings { Mode = BarMode.Hidden });
                form.ToggleFullScreen();
                Pump(300);
                Assert.True(form.FullScreenHandle is { Visible: true });
                Assert.True(form.FullScreenHandle!.Width >= TouchUi.Px(form, TouchMetrics.SessionTarget));
                form.FullScreenHandle.PerformClick();
                Pump(100);
                Assert.True(form.FullScreenBarShown);
                Assert.False(form.FullScreenHandle.Visible);
                form.ExecuteSessionAction(SessionBarAction.HideBar);
                Pump(100);
                Assert.False(form.FullScreenBarShown);
                Assert.True(form.FullScreenHandle.Visible);
                form.TouchDeviceForTests = false;
                form.ExitApp();
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
