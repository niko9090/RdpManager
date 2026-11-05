using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Logica della modalità tocco della finestra principale (senza finestre): attivazione, X delle schede, scorrimento, gesti.</summary>
[Collection("UI")]
public sealed class TouchModeCoreTests
{
    [Theory]
    [InlineData(TouchMode.Auto, true, true)]
    [InlineData(TouchMode.Auto, false, false)]
    [InlineData(TouchMode.On, false, true)]
    [InlineData(TouchMode.On, true, true)]
    [InlineData(TouchMode.Off, true, false)]
    [InlineData(TouchMode.Off, false, false)]
    public void IsActive_FollowsTheModeAndTheScreen(TouchMode mode, bool hasTouch, bool expected) =>
        Assert.Equal(expected, TouchPolicy.IsActive(mode, hasTouch));

    [Fact]
    public void TargetSize_IsLargerWithTheFinger_AndNeverBelowTheOldMinimum()
    {
        Assert.Equal(48, TouchPolicy.TargetSize(true));
        Assert.Equal(TouchMetrics.MinTarget, TouchPolicy.TargetSize(false));
        Assert.True(TouchPolicy.ComfortTarget >= 48 && TouchPolicy.RowHeight >= TouchPolicy.ComfortTarget);
    }

    [Fact]
    public void Normalize_ReplacesUnknownValuesWithAuto()
    {
        Assert.Equal(TouchMode.Auto, TouchPolicy.Normalize((TouchMode)42));
        Assert.Equal(TouchMode.Off, TouchPolicy.Normalize(TouchMode.Off));
    }

    [Theory]
    [InlineData(0x01 | 0x80, 10, true)]   // touch integrato pronto
    [InlineData(0x02 | 0x80, 2, true)]    // touch esterno pronto
    [InlineData(0x01, 10, false)]         // non pronto
    [InlineData(0x80, 10, false)]         // pronto ma senza touch (solo penna/altro)
    [InlineData(0x01 | 0x80, 0, false)]   // nessun punto di contatto
    [InlineData(0, 0, false)]
    public void IsTouchCapable_NeedsAReadyTouchDigitizer(int digitizer, int touches, bool expected) =>
        Assert.Equal(expected, TouchPolicy.IsTouchCapable(digitizer, touches));

    [Theory]
    [InlineData(0xFF515700L, true)]
    [InlineData(0xFF515780L, true)]   // il byte basso porta l'identificatore del contatto
    [InlineData(0L, false)]
    [InlineData(0x00515700L, false)]
    public void IsTouchOrigin_RecognizesFingerEvents(long extra, bool expected) =>
        Assert.Equal(expected, TouchPolicy.IsTouchOrigin(extra));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // mouse vero in modalità tocco: niente tastiera
    [InlineData(false, true, false)]   // modalità tocco spenta
    [InlineData(false, false, false)]
    public void SearchTap_OpensTheKeyboardOnlyForAFingerInTouchMode(bool active, bool finger, bool expected)
    {
        Assert.Equal(expected, TouchPolicy.ShouldOpenKeyboardOnTap(active, finger));
        Assert.Equal(!(active && finger), TouchPolicy.AllowItemDrag(active, finger));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void TabClose_IsBigAndAwayFromTheEdge(int dpi)
    {
        var tab = new Rectangle(100, 40, TouchMetrics.Scale(TabCloseLayout.TabWidth, dpi), TouchMetrics.Scale(48, dpi));
        var close = TabCloseLayout.CloseRect(tab, dpi);
        Assert.True(close.Width >= TouchMetrics.Scale(32, dpi), $"X larga {close.Width} px");
        Assert.Equal(close.Width, close.Height);
        Assert.True(tab.Right - close.Right >= TouchMetrics.Scale(8, dpi), "X troppo vicina al bordo");
        Assert.True(tab.Contains(close));
        Assert.Equal(tab.Y + (tab.Height - close.Height) / 2, close.Y);
        var text = TabCloseLayout.TextArea(tab, dpi, 10);
        Assert.True(text.Right <= close.Left, "il testo non deve finire sotto la X");
        Assert.True(text.Width > 0);
    }

    [Fact]
    public void TabClose_NeverExceedsAShortTab()
    {
        var tab = new Rectangle(0, 0, 200, 20);
        Assert.True(TabCloseLayout.CloseRect(tab, 96).Height <= 20);
    }

    [Fact]
    public void HitClose_FindsTheTabOnlyWhenTheTouchIsOnItsX()
    {
        var tabs = new[] { new Rectangle(0, 0, 210, 48), new Rectangle(210, 0, 210, 48) };
        var close1 = TabCloseLayout.CloseRect(tabs[1], 96);
        Assert.Equal(1, TabCloseLayout.HitClose(tabs, new Point(close1.X + 3, close1.Y + 3), 96));
        Assert.Equal(-1, TabCloseLayout.HitClose(tabs, new Point(220, 24), 96)); // sul nome
        Assert.Equal(-1, TabCloseLayout.HitClose(tabs, new Point(1000, 24), 96)); // fuori dalle schede
        Assert.Equal(0, TabCloseLayout.HitClose(tabs, TabCloseLayout.CloseRect(tabs[0], 96).Location, 96));
    }

    [Fact]
    public void PanScroller_IgnoresSmallMovement_ThenScrollsByWholeRows()
    {
        var pan = new PanScroller(rowHeight: 50, threshold: 8);
        pan.Begin(300);
        Assert.Equal(0, pan.Move(296)); // un tocco poco mosso non è un trascinamento
        Assert.False(pan.Active);
        Assert.Equal(0, pan.Move(280)); // oltre la soglia ma meno di una riga (20 px)
        Assert.True(pan.Active);
        Assert.Equal(1, pan.Move(240)); // altri 40 px: 60 px in tutto = 1 riga, resto 10
        Assert.Equal(0, pan.Move(235)); // resto 15
        Assert.Equal(1, pan.Move(195)); // resto 55 -> 1 riga, resto 5
    }

    [Fact]
    public void PanScroller_DraggingDownScrollsUp_AndEndStopsIt()
    {
        var pan = new PanScroller(40, 8);
        pan.Begin(100);
        Assert.Equal(-3, pan.Move(220));
        pan.End();
        Assert.Equal(0, pan.Move(400));
        Assert.True(pan.Active); // resta vero: serve a ignorare il clic che segue
        pan.Begin(10);
        Assert.False(pan.Active);
    }

    [Theory]
    [InlineData(300, 100, 100, 100, 1)]    // verso sinistra = successiva
    [InlineData(100, 300, 100, 100, -1)]   // verso destra = precedente
    [InlineData(100, 130, 100, 100, 0)]    // troppo corto
    [InlineData(100, 400, 100, 300, 0)]    // più verticale che laterale
    public void Swipe_ChoosesTheDirection(int x1, int x2, int y1, int y2, int expected) =>
        Assert.Equal(expected, SwipeDetector.Direction(new Point(x1, y1), new Point(x2, y2), 70));

    [Fact]
    public void LongPress_FiresOnceAfterTheHold_AndNotWhenTheFingerMoves()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var d = new LongPressDetector();
        d.Press(new Point(10, 10), t0);
        Assert.False(d.Poll(t0 + TouchMetrics.LongPressHold - TimeSpan.FromMilliseconds(1)));
        Assert.True(d.Poll(t0 + TouchMetrics.LongPressHold));
        Assert.False(d.Poll(t0 + TimeSpan.FromSeconds(5))); // una volta sola
        d.Press(new Point(10, 10), t0);
        d.Move(new Point(10, 10 + TouchMetrics.LongPressTolerance + 5)); // trascinamento (scorrimento): niente menu
        Assert.False(d.Poll(t0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TouchMode_IsSavedPerPc_AndNotExported()
    {
        var settings = new AppSettings { TouchMode = TouchMode.On };
        Assert.Equal(TouchMode.Auto, settings.ForExport().TouchMode);
        var other = new AppSettings { TouchMode = TouchMode.Off };
        other.ApplyPortable(new AppSettings { TouchMode = TouchMode.On });
        Assert.Equal(TouchMode.Off, other.TouchMode);
        Assert.Equal(TouchMode.Auto, new AppSettings().TouchMode);
        var bad = new AppSettings { TouchMode = (TouchMode)99 };
        bad.Normalize();
        Assert.Equal(TouchMode.Auto, bad.TouchMode);
    }
}

/// <summary>La modalità tocco nella finestra principale: misure dei bersagli, X delle schede, tastiera sulla ricerca.</summary>
[Collection("UI")]
public sealed class TouchModeUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTouchMode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TouchUi.TouchScreenForTests = () => false;
        TouchUi.TouchInputForTests = null;
        TouchUi.ScreenKeyboardForTests = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConnectionStore Store(TouchMode mode)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var doc = new ConnectionsDocument
        {
            Connections =
            [
                new RdpConnection { Name = "Uno", Host = "host-inesistente.invalid", Username = "u", Password = "p" },
                new RdpConnection { Name = "Due", Host = "host-inesistente.invalid", Username = "u", Password = "p" },
            ],
        };
        doc.Settings.TouchMode = mode;
        store.Save(doc);
        return store;
    }

    private static void OnSta(Action body)
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
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private void With(TouchMode mode, Action<MainForm> body) => OnSta(() =>
    {
        using var form = new MainForm(Store(mode)) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.Show();
        Pump(300);
        body(form);
        form.ExitApp();
        Pump(200);
    });

    private static void Raise(Control c, string handler, EventArgs e) =>
        typeof(Control).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, [e]);

    [Fact]
    public void ModeOn_MakesRowsAndTargetsComfortable()
    {
        With(TouchMode.On, form =>
        {
            Assert.True(form.TouchModeActive);
            Assert.True(form.Tree.ItemHeight >= TouchUi.Px(form, TouchPolicy.RowHeight), $"righe alte {form.Tree.ItemHeight} px");
            int comfort = TouchUi.Px(form, TouchPolicy.ComfortTarget);
            Assert.True(form.SearchBox.Height >= comfort, "ricerca bassa");
            foreach (var b in form.TouchButtons)
                Assert.True(b.Height >= comfort, $"«{b.Text}» alto {b.Height} px (minimo {comfort})");
            foreach (ToolStripMenuItem top in form.MainMenuStrip!.Items)
            {
                Assert.True(top.Height >= comfort - 2, $"menu «{top.Text}» alto {top.Height} px");
                top.ShowDropDown();
                Pump(80);
                foreach (ToolStripItem item in top.DropDownItems.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator && i.Visible))
                    Assert.True(item.Height >= comfort - 2, $"voce «{item.Text}» di «{top.Text}» alta {item.Height} px (minimo {comfort})");
                top.HideDropDown();
            }
        });
    }

    [Fact]
    public void ModeOn_TabsAreWide_AndTheBigXClosesTheSession()
    {
        With(TouchMode.On, form =>
        {
            Assert.True(TouchUi.HasWideTabs(form.Tabs));
            form.OpenSession(form.Document.Connections[0]);
            form.OpenSession(form.Document.Connections[1]);
            Pump(200);
            Assert.Equal(2, form.Tabs.TabCount);
            int dpi = form.DeviceDpi;
            var tab = form.Tabs.GetTabRect(0);
            Assert.True(tab.Width >= TouchMetrics.Scale(TabCloseLayout.TabWidth, dpi) - 2, $"scheda larga {tab.Width} px");
            Assert.True(tab.Height >= TouchUi.Px(form, TouchPolicy.ComfortTarget) - 4, $"scheda alta {tab.Height} px");
            var close = TabCloseLayout.CloseRect(tab, dpi);
            Assert.True(close.Width >= TouchMetrics.Scale(32, dpi));

            Assert.False(form.TryCloseTabAt(new Point(tab.X + 20, tab.Y + tab.Height / 2)), "un tocco sul nome non chiude");
            Assert.Equal(2, form.Tabs.TabCount);
            var second = form.Tabs.TabPages[1].Tag;
            Assert.True(form.TryCloseTabAt(new Point(close.X + close.Width / 2, close.Y + close.Height / 2)));
            Pump(100);
            Assert.Equal(1, form.Tabs.TabCount);
            Assert.Same(second, form.Tabs.TabPages[0].Tag);
        });
    }

    [Fact]
    public void ModeOn_SwipeOnTheTabStripSwitchesSession()
    {
        With(TouchMode.On, form =>
        {
            form.OpenSession(form.Document.Connections[0]);
            form.OpenSession(form.Document.Connections[1]);
            Pump(200);
            Assert.Equal(1, form.Tabs.SelectedIndex);
            Assert.Equal(-1, form.SwipeTabs(new Point(100, 10), new Point(400, 12))); // verso destra: precedente
            Assert.Equal(0, form.Tabs.SelectedIndex);
            Assert.Equal(1, form.SwipeTabs(new Point(400, 10), new Point(100, 12))); // verso sinistra: successiva
            Assert.Equal(1, form.Tabs.SelectedIndex);
            Assert.Equal(0, form.SwipeTabs(new Point(100, 10), new Point(110, 10))); // tocco, non scorrimento
        });
    }

    [Fact]
    public void ModeOff_KeepsTheOrdinaryMeasures_AndNoXOnTabs()
    {
        TouchUi.TouchScreenForTests = () => true; // anche con uno schermo touch, «Spenta» vince
        With(TouchMode.Off, form =>
        {
            Assert.False(form.TouchModeActive);
            Assert.False(TouchUi.HasWideTabs(form.Tabs));
            Assert.Equal(TouchUi.MinTarget(form), form.Tree.ItemHeight);
            form.OpenSession(form.Document.Connections[0]);
            Pump(200);
            var tab = form.Tabs.GetTabRect(0);
            Assert.False(form.TryCloseTabAt(new Point(tab.Right - 20, tab.Y + tab.Height / 2)));
            Assert.Equal(1, form.Tabs.TabCount);
        });
    }

    [Fact]
    public void ModeAuto_FollowsTheTouchScreen_AndTheSettingAtRuntime()
    {
        TouchUi.TouchScreenForTests = () => true;
        With(TouchMode.Auto, form =>
        {
            Assert.True(form.TouchModeActive);
            Assert.True(form.Tree.ItemHeight >= TouchUi.Px(form, TouchPolicy.RowHeight));

            TouchUi.TouchScreenForTests = () => false; // il tablet torna portatile
            form.RefreshTouchMode();
            Assert.False(form.TouchModeActive);
            Assert.Equal(TouchUi.MinTarget(form), form.Tree.ItemHeight);

            form.Document.Settings.TouchMode = TouchMode.On; // scelta nelle impostazioni
            form.RefreshTouchMode();
            Assert.True(form.TouchModeActive);
            Assert.True(form.Tree.ItemHeight >= TouchUi.Px(form, TouchPolicy.RowHeight));
        });
    }

    [Fact]
    public void ModeAuto_WithoutATouchScreen_LooksLikeBefore()
    {
        TouchUi.TouchScreenForTests = () => false;
        With(TouchMode.Auto, form =>
        {
            Assert.False(form.TouchModeActive);
            Assert.False(TouchUi.HasWideTabs(form.Tabs));
            Assert.True(form.Tree.ItemHeight >= TouchUi.MinTarget(form)); // il minimo della 1.8 resta
        });
    }

    [Fact]
    public void Search_OpensTheKeyboardOnlyWhenAFingerTouchesIt_InTouchMode()
    {
        int opened = 0;
        TouchUi.ScreenKeyboardForTests = () => { opened++; return true; };
        With(TouchMode.On, form =>
        {
            var down = new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0);
            TouchUi.TouchInputForTests = () => false; // mouse vero
            Raise(form.SearchBox, "OnMouseDown", down);
            Assert.Equal(0, opened);
            TouchUi.TouchInputForTests = () => true; // dito
            Raise(form.SearchBox, "OnMouseDown", down);
            Assert.Equal(1, opened);

            form.Document.Settings.TouchMode = TouchMode.Off;
            form.RefreshTouchMode();
            Raise(form.SearchBox, "OnMouseDown", down);
            Assert.Equal(1, opened);
        });
    }

    [Fact]
    public void Overview_HasABigCloseButton_InsideTheHeader()
    {
        OnSta(() =>
        {
            using var overview = new OverviewForm { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(900, 600) };
            overview.Show();
            Pump(100);
            var r = overview.CloseButtonRect;
            int comfort = TouchUi.Px(overview, TouchPolicy.ComfortTarget);
            Assert.True(r.Height >= comfort && r.Width >= comfort);
            Assert.True(overview.ClientRectangle.Contains(r), "il pulsante deve stare nella finestra");
            Assert.True(overview.Cells.Count == 0 || overview.Cells.All(c => !c.IntersectsWith(r)));
            overview.Close();
        });
    }

    [Fact]
    public void ToolStripMinimum_CanGoDownAgain()
    {
        OnSta(() =>
        {
            using var strip = new ToolStrip();
            var button = new ToolStripButton("Prova");
            strip.Items.Add(button);
            TouchUi.TouchifyStrip(strip, 72);
            Assert.True(button.GetPreferredSize(Size.Empty).Height >= 72 - 1);
            TouchUi.TouchifyStrip(strip, 40);
            int height = button.GetPreferredSize(Size.Empty).Height;
            Assert.True(height < 72 - 1 && height >= 40 - 1, $"alto {height} px");
            var later = new ToolStripButton("Dopo");
            strip.Items.Add(later); // aggiunta dopo: usa il minimo in corso
            Assert.True(later.GetPreferredSize(Size.Empty).Height >= 40 - 1);
        });
    }
}
