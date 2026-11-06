using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Gesti a più dita sul riconoscitore (due, tre, quattro dita): logica pura, con l'orologio passato a mano.</summary>
[Collection("UI")]
public sealed class TouchMultiGestureTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // tolleranza 10 (soglia di movimento 20), una tacca ogni 20 px, scorrimento a più dita da 60 px, finestra di arrivo 200 ms, tocco breve 300 ms.
    private static TouchGestureRecognizer Recognizer(TouchGestureMap? map = null) => new(TimeSpan.FromMilliseconds(500), tolerance: 10, scrollStep: 20,
        doubleTap: TimeSpan.FromMilliseconds(400), pinchStep: 20, swipeDistance: 60, joinWindow: TimeSpan.FromMilliseconds(200),
        tapMax: TimeSpan.FromMilliseconds(300), map: map);

    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    private static TouchActionKind[] Kinds(IEnumerable<TouchAction> a) => a.Select(x => x.Kind).ToArray();

    private static readonly Point[] Row = [new(100, 300), new(200, 300), new(300, 300), new(400, 300)];

    /// <summary>Appoggia n dita sulla stessa riga, a 10 ms l'una dall'altra.</summary>
    private static void Land(TouchGestureRecognizer r, int n)
    {
        for (int i = 0; i < n; i++)
            Assert.Empty(r.Down(i + 1, Row[i], At(i * 10)));
    }

    /// <summary>Sposta tutte le dita di (dx, dy) in qualche passo e restituisce le azioni uscite.</summary>
    private static List<TouchAction> Swipe(TouchGestureRecognizer r, int n, int dx, int dy, int startMs = 100, int steps = 6)
    {
        var all = new List<TouchAction>();
        for (int s = 1; s <= steps; s++)
            for (int i = 0; i < n; i++)
                all.AddRange(r.Move(i + 1, new Point(Row[i].X + dx * s / steps, Row[i].Y + dy * s / steps), At(startMs + s * 10)));
        return all;
    }

    private static List<TouchAction> LiftAll(TouchGestureRecognizer r, int n, int ms, Func<int, Point>? where = null, bool reverse = false)
    {
        var all = new List<TouchAction>();
        var ids = Enumerable.Range(1, n);
        foreach (int id in reverse ? ids.Reverse() : ids)
            all.AddRange(r.Up(id, where?.Invoke(id) ?? Row[id - 1], At(ms)));
        return all;
    }

    // ---------- due dita ----------

    [Fact]
    public void TwoFingerTap_IsARightClickAtTheMidpoint()
    {
        var r = Recognizer();
        Land(r, 2);
        var actions = LiftAll(r, 2, 120);
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], Kinds(actions));
        Assert.All(actions, a => Assert.Equal(new Point(150, 300), a.Location));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void TwoFingerTap_WithASmallWobble_IsStillATap()
    {
        var r = Recognizer();
        Land(r, 2);
        r.Move(1, new Point(108, 304), At(60));
        r.Move(2, new Point(195, 296), At(70));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], Kinds(LiftAll(r, 2, 140)));
    }

    [Fact]
    public void TwoFingerTouch_HeldTooLong_IsNotATap()
    {
        var r = Recognizer();
        Land(r, 2);
        Assert.Empty(LiftAll(r, 2, 900));
    }

    [Fact]
    public void TwoFingersMovingTogether_Scroll_AndNeverZoom()
    {
        var r = Recognizer();
        Land(r, 2);
        var actions = Swipe(r, 2, 0, -60);
        Assert.NotEmpty(actions);
        Assert.All(actions, a => { Assert.Equal(TouchActionKind.Scroll, a.Kind); Assert.True(a.Amount < 0); });
        Assert.Empty(LiftAll(r, 2, 400, id => new Point(Row[id - 1].X, 240)));
    }

    [Fact]
    public void TwoFingersSpreading_ZoomIn_PinchingZoomsOut()
    {
        var r = Recognizer();
        r.Down(1, new Point(200, 300), At(0));
        r.Down(2, new Point(260, 300), At(10));
        var zoomIn = new List<TouchAction>();
        for (int s = 1; s <= 6; s++)
        {
            zoomIn.AddRange(r.Move(1, new Point(200 - s * 8, 300), At(20 + s * 10)));
            zoomIn.AddRange(r.Move(2, new Point(260 + s * 8, 300), At(20 + s * 10)));
        }
        Assert.NotEmpty(zoomIn);
        Assert.All(zoomIn, a => { Assert.Equal(TouchActionKind.Zoom, a.Kind); Assert.Equal(TouchGestureRecognizer.WheelNotch, a.Amount); });
        var zoomOut = new List<TouchAction>();
        for (int s = 1; s <= 12; s++)
        {
            zoomOut.AddRange(r.Move(1, new Point(152 + s * 8, 300), At(100 + s * 10)));
            zoomOut.AddRange(r.Move(2, new Point(308 - s * 8, 300), At(100 + s * 10)));
        }
        Assert.NotEmpty(zoomOut);
        Assert.Equal(-TouchGestureRecognizer.WheelNotch, zoomOut[^1].Amount);
        Assert.Empty(r.Up(1, new Point(250, 300), At(300)));
        Assert.Empty(r.Up(2, new Point(210, 300), At(310)));
    }

    [Fact]
    public void PinchWithDrift_StaysAZoom_AndScrollWithDistanceWobble_StaysAScroll()
    {
        var pinch = Recognizer();
        pinch.Down(1, new Point(200, 300), At(0));
        pinch.Down(2, new Point(260, 300), At(10));
        var a1 = new List<TouchAction>();
        for (int s = 1; s <= 8; s++)
        {
            a1.AddRange(pinch.Move(1, new Point(200 - s * 8, 300 - s * 2), At(20 + s * 10)));
            a1.AddRange(pinch.Move(2, new Point(260 + s * 8, 300 - s * 2), At(20 + s * 10)));
        }
        Assert.NotEmpty(a1);
        Assert.All(a1, a => Assert.Equal(TouchActionKind.Zoom, a.Kind));

        var scroll = Recognizer();
        scroll.Down(1, new Point(200, 300), At(0));
        scroll.Down(2, new Point(260, 300), At(10));
        var a2 = new List<TouchAction>();
        for (int s = 1; s <= 8; s++)
        {
            a2.AddRange(scroll.Move(1, new Point(200 - s, 300 + s * 10), At(20 + s * 10)));
            a2.AddRange(scroll.Move(2, new Point(260 + s, 300 + s * 10), At(20 + s * 10)));
        }
        Assert.NotEmpty(a2);
        Assert.All(a2, a => Assert.Equal(TouchActionKind.Scroll, a.Kind));
    }

    [Fact]
    public void OnceAScroll_AlwaysAScroll_EvenIfTheFingersThenSpread()
    {
        var r = Recognizer();
        r.Down(1, new Point(200, 300), At(0));
        r.Down(2, new Point(260, 300), At(10));
        Assert.NotEmpty(r.Move(1, new Point(200, 360), At(50)));
        r.Move(2, new Point(260, 360), At(60));
        var later = new List<TouchAction>();
        for (int s = 1; s <= 8; s++)
            later.AddRange(r.Move(2, new Point(260 + s * 15, 360), At(70 + s * 10)));
        Assert.DoesNotContain(TouchActionKind.Zoom, Kinds(later));
    }

    // ---------- tre dita ----------

    [Fact]
    public void ThreeFingerTap_IsAltTab_ByDefault()
    {
        var r = Recognizer();
        Land(r, 3);
        var actions = LiftAll(r, 3, 150);
        Assert.Equal([TouchActionKind.AltTab], Kinds(actions));
        Assert.Equal(new Point(200, 300), actions[0].Location);
    }

    [Theory]
    [InlineData(-80, 0, TouchActionKind.PreviousTab)]
    [InlineData(80, 0, TouchActionKind.NextTab)]
    [InlineData(0, -80, TouchActionKind.ShowBar)]
    [InlineData(0, 80, TouchActionKind.HideBar)]
    public void ThreeFingerSwipe_GivesTheTabAndBarCommands_OnceOnly(int dx, int dy, TouchActionKind expected)
    {
        var r = Recognizer();
        Land(r, 3);
        var actions = Swipe(r, 3, dx, dy);
        Assert.Equal([expected], Kinds(actions)); // una sola volta, mai mouse né rotella
        Assert.Empty(LiftAll(r, 3, 300, id => new Point(Row[id - 1].X + dx, Row[id - 1].Y + dy)));
    }

    [Fact]
    public void ThreeFingerSwipe_TooShort_DoesNothing_AndIsNotATap()
    {
        var r = Recognizer();
        Land(r, 3);
        Assert.Empty(Swipe(r, 3, 35, 0));
        Assert.Empty(LiftAll(r, 3, 200, id => new Point(Row[id - 1].X + 35, Row[id - 1].Y)));
    }

    [Fact]
    public void ThreeFingerSwipe_DiagonalPicksTheDominantAxis()
    {
        var r = Recognizer();
        Land(r, 3);
        Assert.Equal([TouchActionKind.NextTab], Kinds(Swipe(r, 3, 90, 30)));
    }

    // ---------- quattro dita ----------

    [Fact]
    public void FourFingersDown_ShowTheDesktop_FourFingersUp_TaskView()
    {
        var down = Recognizer();
        Land(down, 4);
        Assert.Equal([TouchActionKind.ShowDesktop], Kinds(Swipe(down, 4, 0, 90)));
        var up = Recognizer();
        Land(up, 4);
        Assert.Equal([TouchActionKind.TaskView], Kinds(Swipe(up, 4, 0, -90)));
    }

    [Fact]
    public void FourFingers_SidewaysOrTapping_DoNothing()
    {
        var r = Recognizer();
        Land(r, 4);
        Assert.Empty(Swipe(r, 4, 90, 0));
        Assert.Empty(LiftAll(r, 4, 300, id => new Point(Row[id - 1].X + 90, Row[id - 1].Y)));
        Land(r, 4);
        Assert.Empty(LiftAll(r, 4, 100));
    }

    // ---------- casi limite ----------

    [Fact]
    public void ThirdFingerArrivingLate_DoesNotTurnATwoFingerTapIntoAThreeFingerOne()
    {
        var r = Recognizer();
        Land(r, 2);
        Assert.Empty(r.Down(3, Row[2], At(260))); // oltre i 200 ms dal primo dito
        var actions = new List<TouchAction>();
        actions.AddRange(r.Up(3, Row[2], At(280)));
        actions.AddRange(r.Up(1, Row[0], At(285)));
        actions.AddRange(r.Up(2, Row[1], At(290)));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], Kinds(actions)); // clic destro, non Alt+Tab
    }

    [Fact]
    public void ThirdFingerArrivingLate_DuringATwoFingerScroll_KeepsScrolling_AndNeverSwipes()
    {
        var r = Recognizer();
        Land(r, 2);
        var actions = Swipe(r, 2, 0, -50, steps: 5);
        Assert.NotEmpty(actions);
        r.Down(3, new Point(300, 300), At(400));
        for (int s = 1; s <= 12; s++)
        {
            actions.AddRange(r.Move(1, new Point(100 + s * 8, 250), At(410 + s * 10)));
            actions.AddRange(r.Move(2, new Point(200 + s * 8, 250), At(410 + s * 10)));
            actions.AddRange(r.Move(3, new Point(300 + s * 8, 300), At(410 + s * 10))); // il dito in ritardo non conta
        }
        Assert.All(actions, a => Assert.Equal(TouchActionKind.Scroll, a.Kind));
        r.Up(3, new Point(400, 300), At(700));
        r.Up(1, new Point(190, 250), At(710));
        r.Up(2, new Point(290, 250), At(720));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void SecondFingerArrivingLate_StillMakesATwoFingerScroll()
    {
        var r = Recognizer();
        r.Down(1, Row[0], At(0));
        r.Down(2, Row[1], At(700));
        Assert.NotEmpty(r.Move(1, new Point(100, 240), At(720)));
    }

    [Fact]
    public void ThirdFingerWithinTheWindow_AfterTwoFingersStartedScrolling_RestartsAsThreeFingerGesture()
    {
        var r = Recognizer();
        r.Down(1, Row[0], At(0));
        r.Down(2, Row[1], At(10));
        Assert.NotEmpty(r.Move(1, new Point(100, 250), At(20))); // scorrimento già partito
        r.Down(3, Row[2], At(50));
        var rest = new List<TouchAction>();
        for (int s = 1; s <= 6; s++)
            for (int i = 0; i < 3; i++)
                rest.AddRange(r.Move(i + 1, new Point(Row[i].X + s * 15, i == 0 ? 250 : 300), At(60 + s * 10)));
        Assert.Equal([TouchActionKind.NextTab], Kinds(rest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FingersLiftedInAnyOrder_ThreeFingerTap_StillWorks(bool reverse)
    {
        var r = Recognizer();
        Land(r, 3);
        Assert.Equal([TouchActionKind.AltTab], Kinds(LiftAll(r, 3, 150, reverse: reverse)));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void ThreeFingerSwipe_WithFingersLiftedOneByOne_FiresOnlyOnce()
    {
        var r = Recognizer();
        Land(r, 3);
        var actions = Swipe(r, 3, 80, 0);
        actions.AddRange(r.Up(2, new Point(280, 300), At(300)));
        actions.AddRange(r.Move(1, new Point(260, 300), At(310)));
        actions.AddRange(r.Up(3, new Point(380, 300), At(320)));
        actions.AddRange(r.Up(1, new Point(260, 300), At(330)));
        Assert.Equal([TouchActionKind.NextTab], Kinds(actions));
    }

    [Fact]
    public void TwoFingersLiftingOneAfterTheOther_WhileScrolling_KeepsScrollingWithTheRemainingFinger()
    {
        var r = Recognizer();
        Land(r, 2);
        Assert.NotEmpty(Swipe(r, 2, 0, -60));
        r.Up(2, new Point(200, 240), At(300));
        var more = r.Move(1, new Point(100, 180), At(310));
        Assert.NotEmpty(more);
        Assert.All(more, a => Assert.Equal(TouchActionKind.Scroll, a.Kind));
        Assert.Empty(r.Up(1, new Point(100, 180), At(320)));
    }

    [Fact]
    public void AfterAMultiFingerGesture_ANewSingleTapWorks_AndDoesNotInheritState()
    {
        var r = Recognizer();
        Land(r, 3);
        Swipe(r, 3, 80, 0);
        LiftAll(r, 3, 300, id => new Point(Row[id - 1].X + 80, Row[id - 1].Y));
        r.Down(1, new Point(50, 50), At(2000));
        Assert.Equal([TouchActionKind.Move, TouchActionKind.Click], Kinds(r.Up(1, new Point(50, 50), At(2050))));
    }

    [Fact]
    public void Cancel_DuringAMultiFingerGesture_ForgetsEverything()
    {
        var r = Recognizer();
        Land(r, 3);
        Assert.Empty(r.Cancel());
        Assert.False(r.IsActive);
        Assert.Empty(r.Up(1, Row[0], At(100)));
    }

    // ---------- tabella dei gesti ----------

    [Fact]
    public void DefaultMap_HasAnActionForEveryGesture_AndKeysGoToTheRightKinds()
    {
        foreach (var g in Enum.GetValues<TouchGesture>())
            Assert.NotNull(TouchGestureMap.Default.ActionFor(g));
        Assert.Equal(TouchActionKind.RightClick, TouchGestureMap.Default.ActionFor(TouchGesture.TwoFingerTap));
        Assert.Equal(TouchActionKind.AltTab, TouchGestureMap.Default.ActionFor(TouchGesture.ThreeFingerTap));
        Assert.Equal(TouchActionKind.ShowDesktop, TouchGestureMap.Default.ActionFor(TouchGesture.FourFingerSwipeDown));
        Assert.Equal(TouchActionKind.TaskView, TouchGestureMap.Default.ActionFor(TouchGesture.FourFingerSwipeUp));
        Assert.True(TouchActionKinds.IsWindowCommand(TouchActionKind.PreviousTab));
        Assert.True(TouchActionKinds.IsRemoteKeys(TouchActionKind.AltTab));
        Assert.False(TouchActionKinds.IsWindowCommand(TouchActionKind.Zoom));
    }

    [Fact]
    public void CustomMap_ChangesTheAction_AndAMissingEntryDoesNothing()
    {
        var map = new TouchGestureMap(new Dictionary<TouchGesture, TouchActionKind> { [TouchGesture.ThreeFingerTap] = TouchActionKind.ShowBar });
        var r = Recognizer(map);
        Land(r, 3);
        Assert.Equal([TouchActionKind.ShowBar], Kinds(LiftAll(r, 3, 100)));
        Land(r, 2);
        Assert.Empty(LiftAll(r, 2, 100)); // il tocco a due dita non è nella tabella
    }
}
