using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>La mappa gesto → azione salvata nelle impostazioni: predefiniti, salvataggio, «Spento», uso nel riconoscitore.</summary>
[Collection("UI")]
public sealed class TouchGestureSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerGestures-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TouchUi.TouchScreenForTests = () => false;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    private static TouchGestureRecognizer Recognizer(TouchGestureMap map) => new(TimeSpan.FromMilliseconds(500), tolerance: 10, scrollStep: 20,
        doubleTap: TimeSpan.FromMilliseconds(400), pinchStep: 20, swipeDistance: 60, joinWindow: TimeSpan.FromMilliseconds(200),
        tapMax: TimeSpan.FromMilliseconds(300), map: map);

    private static List<TouchAction> ThreeFingerSwipe(TouchGestureRecognizer r, int dx)
    {
        var all = new List<TouchAction>();
        for (int i = 0; i < 3; i++)
            all.AddRange(r.Down(i + 1, new Point(100 + i * 100, 300), At(i * 10)));
        for (int s = 1; s <= 6; s++)
            for (int i = 0; i < 3; i++)
                all.AddRange(r.Move(i + 1, new Point(100 + i * 100 + dx * s / 6, 300), At(100 + s * 10)));
        for (int i = 0; i < 3; i++)
            all.AddRange(r.Up(i + 1, new Point(100 + i * 100 + dx, 300), At(200)));
        return all;
    }

    private static List<TouchAction> TwoFingerTap(TouchGestureRecognizer r)
    {
        var all = new List<TouchAction>();
        all.AddRange(r.Down(1, new Point(100, 300), At(0)));
        all.AddRange(r.Down(2, new Point(200, 300), At(10)));
        all.AddRange(r.Up(1, new Point(100, 300), At(120)));
        all.AddRange(r.Up(2, new Point(200, 300), At(120)));
        return all;
    }

    [Fact]
    public void OldSettingsWithoutMap_LoadWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument()); // mai cambiata: nessuna voce dei gesti
        var loaded = store.Load();
        Assert.Null(loaded.Settings.TouchGestures);
        foreach (var (gesture, kind) in TouchGestureMap.Defaults)
            Assert.Equal(kind, loaded.Settings.GestureMap.ActionFor(gesture));
        foreach (var gesture in Enum.GetValues<TouchGesture>())
            Assert.Equal(TouchGestureMap.Default.ActionFor(gesture), TouchGestureMap.FromSettings(null).ActionFor(gesture));
    }

    [Fact]
    public void Map_RoundTripsThroughTheStore()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var doc = new ConnectionsDocument();
        doc.Settings.GestureMap = TouchGestureMap.Default
            .With(TouchGesture.TwoFingerTap, TouchActionKind.Click)
            .With(TouchGesture.ThreeFingerTap, null)
            .With(TouchGesture.FourFingerSwipeUp, TouchActionKind.NextTab);
        store.Save(doc);

        var map = store.Load().Settings.GestureMap;
        Assert.Equal(TouchActionKind.Click, map.ActionFor(TouchGesture.TwoFingerTap));
        Assert.Null(map.ActionFor(TouchGesture.ThreeFingerTap));
        Assert.Equal(TouchActionKind.NextTab, map.ActionFor(TouchGesture.FourFingerSwipeUp));
        Assert.Equal(TouchActionKind.PreviousTab, map.ActionFor(TouchGesture.ThreeFingerSwipeLeft)); // non toccato = predefinito
    }

    [Fact]
    public void MissingOrInvalidEntries_FallBackToTheDefault()
    {
        var map = TouchGestureMap.FromSettings(new Dictionary<string, string>
        {
            ["TwoFingerTap"] = "Off",
            ["ThreeFingerTap"] = "Nonsense",
            ["Inesistente"] = "NextTab",
            ["FourFingerSwipeDown"] = "Scroll", // non è fra le scelte
        });
        Assert.Null(map.ActionFor(TouchGesture.TwoFingerTap));
        Assert.Equal(TouchActionKind.AltTab, map.ActionFor(TouchGesture.ThreeFingerTap));
        Assert.Equal(TouchActionKind.ShowDesktop, map.ActionFor(TouchGesture.FourFingerSwipeDown));
        Assert.Equal(TouchActionKind.NextTab, map.ActionFor(TouchGesture.ThreeFingerSwipeRight));
    }

    [Fact]
    public void Map_IsPerPc_NotExported_AndCloned()
    {
        var settings = new AppSettings { GestureMap = TouchGestureMap.Default.With(TouchGesture.TwoFingerTap, null) };
        Assert.Null(settings.ForExport().TouchGestures);
        var clone = settings.Clone();
        clone.GestureMap = TouchGestureMap.Default;
        Assert.Null(settings.GestureMap.ActionFor(TouchGesture.TwoFingerTap));
        var other = new AppSettings { GestureMap = TouchGestureMap.Default.With(TouchGesture.TwoFingerTap, null) };
        other.ApplyPortable(new AppSettings());
        Assert.Null(other.GestureMap.ActionFor(TouchGesture.TwoFingerTap));
    }

    [Fact]
    public void Choices_AreDescribedInItalian_AndDefaultsAreInThem()
    {
        Assert.Equal("Spento", TouchGestureMap.Describe((TouchActionKind?)null));
        foreach (var kind in TouchGestureMap.Choices)
            Assert.NotEqual(kind.ToString(), TouchGestureMap.Describe(kind));
        foreach (var kind in TouchGestureMap.Defaults.Values)
            Assert.Contains(kind, TouchGestureMap.Choices);
        foreach (var g in Enum.GetValues<TouchGesture>())
            Assert.NotEqual(g.ToString(), TouchGestureMap.Describe(g));
    }

    [Fact]
    public void OffGesture_ProducesNoActions()
    {
        var r = Recognizer(TouchGestureMap.Default.With(TouchGesture.TwoFingerTap, null).With(TouchGesture.ThreeFingerSwipeLeft, null));
        Assert.Empty(TwoFingerTap(r));
        Assert.Empty(ThreeFingerSwipe(r, -90));
        Assert.False(r.IsActive);
    }

    [Fact]
    public void Recognizer_UsesTheCustomMap_AndFollowsMapChanges()
    {
        var map = TouchGestureMap.Default
            .With(TouchGesture.TwoFingerTap, TouchActionKind.AltTab)
            .With(TouchGesture.ThreeFingerSwipeLeft, TouchActionKind.NextTab);
        var r = Recognizer(map);
        Assert.Equal([TouchActionKind.AltTab], TwoFingerTap(r).Select(a => a.Kind));
        Assert.Equal([TouchActionKind.NextTab], ThreeFingerSwipe(r, -90).Select(a => a.Kind));

        r.Map = TouchGestureMap.Default;
        Assert.Equal([TouchActionKind.Move, TouchActionKind.RightClick], TwoFingerTap(r).Select(a => a.Kind));
        Assert.Equal([TouchActionKind.PreviousTab], ThreeFingerSwipe(r, -90).Select(a => a.Kind));
    }

    [Fact]
    public void SettingsDialog_ShowsAndSavesTheMap()
    {
        var settings = new AppSettings { GestureMap = TouchGestureMap.Default.With(TouchGesture.TwoFingerTap, null).With(TouchGesture.ThreeFingerTap, TouchActionKind.HideBar) };
        OnSta(() =>
        {
            using var dlg = new SettingsDialog(settings);
            var boxes = Descendants(dlg).OfType<ComboBox>().Where(c => c.Items.Contains("Alt+Tab sul PC remoto")).ToList();
            Assert.Equal(Enum.GetValues<TouchGesture>().Length, boxes.Count);
            Assert.Equal("Spento", boxes[0].SelectedItem);
            Assert.Equal("Nascondi la barra", boxes[1].SelectedItem);

            var reset = Descendants(dlg).OfType<Button>().Single(b => b.Text == "Ripristina predefiniti");
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reset, [EventArgs.Empty]); // il dialogo non è mostrato: PerformClick non agirebbe
            Assert.Equal("Clic destro", boxes[0].SelectedItem);
            Assert.Equal("Alt+Tab sul PC remoto", boxes[1].SelectedItem);

            boxes[0].SelectedItem = "Spento";
            typeof(SettingsDialog).GetMethod("OnOk", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dlg, [null, EventArgs.Empty]);
        });
        Assert.Null(settings.GestureMap.ActionFor(TouchGesture.TwoFingerTap));
        Assert.Equal(TouchActionKind.AltTab, settings.GestureMap.ActionFor(TouchGesture.ThreeFingerTap));
    }

    [Fact]
    public void MainWindow_ListensToTabGesturesOnlyInTouchMode()
    {
        Directory.CreateDirectory(_dir);
        foreach (var (mode, expected) in new[] { (TouchMode.On, true), (TouchMode.Off, false) })
        {
            var store = new ConnectionStore(Path.Combine(_dir, $"c-{mode}.json"));
            var doc = new ConnectionsDocument();
            doc.Settings.TouchMode = mode;
            doc.Settings.GestureMap = TouchGestureMap.Default.With(TouchGesture.ThreeFingerSwipeLeft, null);
            store.Save(doc);
            OnSta(() =>
            {
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                form.Show();
                Application.DoEvents();
                Assert.NotNull(form.MainTouchInput);
                Assert.Equal(expected, form.MainTouchInput!.Enabled);
                Assert.Null(form.MainTouchInput.GestureMap.ActionFor(TouchGesture.ThreeFingerSwipeLeft));
                Assert.Equal(TouchActionKind.NextTab, form.MainTouchInput.GestureMap.ActionFor(TouchGesture.ThreeFingerSwipeRight));
                form.ExitApp();
            });
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
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
}
