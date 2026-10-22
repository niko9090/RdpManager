using System.Drawing;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Core 1.5: barra a schermo intero personalizzabile (niente in alto), conversione dalle impostazioni
/// 1.1-1.4, esportazione .rdpm, geometria senza sovrapposizioni, compatibilità con i file 1.0-1.4.</summary>
public sealed class V15CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV15-" + Guid.NewGuid().ToString("N"));

    public V15CoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Fixture(string version)
    {
        var path = Path.Combine(_dir, $"connections-{version}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json"), path);
        return path;
    }

    private string WriteJson(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static List<(BarSection, bool)> Sections(FullScreenBarSettings bar) => bar.Sections.Select(s => (s.Kind, s.Visible)).ToList();

    // ---------- valori predefiniti ----------

    [Fact]
    public void Defaults_LeftFixedWithConnectedSessionsFirst()
    {
        var bar = new AppSettings().Bar;
        Assert.Equal((BarPosition.Left, BarMode.Fixed, 260, 300, SessionListStyle.Compact),
            (bar.Position, bar.Mode, bar.Width, bar.RevealDelayMs, bar.SessionStyle));
        Assert.Equal(
            [BarSection.ConnectedSessions, BarSection.Favorites, BarSection.AllConnections, BarSection.Workspaces,
             BarSection.QuickButtons, BarSection.SessionActions, BarSection.QuickTexts],
            bar.VisibleSections);
        Assert.Equal(Enum.GetValues<BarSection>().Length, bar.Sections.Count);
    }

    // ---------- conversione dalle impostazioni 1.1-1.4 ----------

    [Theory]
    [InlineData("1.0.0", BarMode.Fixed)]
    [InlineData("1.1.0", BarMode.AutoReveal)]
    [InlineData("1.2.0", BarMode.Fixed)]
    [InlineData("1.3.0", BarMode.AutoReveal)]
    [InlineData("1.4.0", BarMode.AutoReveal)]
    public void OlderSettings_TheTopBarBecomesConnectedSessionsAtTheTopOfTheLeftBar(string version, BarMode expectedMode)
    {
        Assert.DoesNotContain("fullScreenBar", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json")));
        var store = new ConnectionStore(Fixture(version));
        var doc = store.Load();
        var bar = doc.Settings.FullScreenBar;
        Assert.NotNull(bar);
        Assert.Equal(BarPosition.Left, bar.Position);
        Assert.Equal(expectedMode, bar.Mode); // fissa o a comparsa come la barra laterale di prima
        Assert.Equal(BarSection.ConnectedSessions, bar.VisibleSections[0]);
        Assert.Contains(BarSection.QuickButtons, bar.VisibleSections);   // i comandi della vecchia barra in alto
        Assert.Contains(BarSection.AllConnections, bar.VisibleSections); // l'elenco della vecchia barra laterale

        // Risalvato, il file contiene la barra e la rilegge uguale; la barra laterale della finestra resta com'era.
        var sidebar = doc.Settings.SidebarMode;
        store.Save(doc);
        Assert.Contains("fullScreenBar", File.ReadAllText(store.FilePath));
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Assert.Equal(sidebar, again.Settings.SidebarMode);
        Assert.Equal((bar.Position, bar.Mode, bar.Width, bar.RevealDelayMs), (again.Settings.Bar.Position, again.Settings.Bar.Mode, again.Settings.Bar.Width, again.Settings.Bar.RevealDelayMs));
        Assert.Equal(Sections(bar), Sections(again.Settings.Bar));
    }

    [Fact]
    public void Version14File_LoadsAndResavesWithoutLosses()
    {
        var store = new ConnectionStore(Fixture("1.4.0"));
        void Check(ConnectionsDocument d)
        {
            Assert.Equal(["Sede"], d.Groups);
            Assert.Equal(["Contabilità", "Magazzino"], d.Connections.Select(c => c.Name));
            var a = d.Connections[0];
            Assert.Equal(("operatore", "SEDE", "AQIDBAUGBwgJCgsMDQ4PFA==", true, "PROD"), (a.Username, a.Domain, a.ProtectedPassword, a.IsFavorite, a.Label));
            Assert.Equal((QualityProfile.SlowConnection, true), (a.Quality, a.KeepAlive));
            Assert.Equal((QualityProfile.LocalNetwork, false, 3390), (d.Connections[1].Quality, d.Connections[1].KeepAlive, d.Connections[1].Port));
            var s = d.Settings;
            Assert.Equal((SidebarMode.AutoHide, AppTheme.Dark, 5), (s.SidebarMode, s.Theme, s.LockAfterMinutes));
            Assert.True(s.LockEnabled);
            Assert.Equal(("AAECAwQFBgcICQoLDA0ODw==", 210000), (s.LockPin!.Salt, s.LockPin.Iterations));
            Assert.Equal("Ctrl+Alt+N", s.GetShortcuts()[ShortcutAction.ToggleSidebar].ToString());
            var ws = Assert.Single(d.Workspaces);
            Assert.Equal(("Mattina", true, "Ctrl+Alt+Maiusc+1", 2), (ws.Name, ws.FullScreen, ws.Shortcut, ws.Connections.Count));
            Assert.Equal("Data", Assert.Single(d.QuickTexts).Name);
            Assert.Equal(BarMode.AutoReveal, s.Bar.Mode);
        }
        var doc = store.Load();
        Assert.Equal(5, doc.Version);
        Check(doc);
        store.Save(doc);
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Check(again);
    }

    // ---------- «alto» rifiutato ----------

    [Theory]
    [InlineData("\"Top\"")]
    [InlineData("\"top\"")]
    [InlineData("\"Alto\"")]
    [InlineData("\"3\"")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("{}")]
    public void TopOrUnknownPosition_IsRejectedAndBecomesLeft(string position)
    {
        var json = $$"""
            { "version": 6, "settings": { "sidebarMode": "Pinned",
              "fullScreenBar": { "position": {{position}}, "mode": "AutoReveal", "width": 300 } },
              "connections": [ { "id": "6f000000-0000-4000-8000-000000000001", "name": "A", "host": "a.example.test" } ] }
            """;
        var doc = new ConnectionStore(WriteJson(json)).Load();
        Assert.Equal(BarPosition.Left, doc.Settings.Bar.Position);
        Assert.Equal((BarMode.AutoReveal, 300), (doc.Settings.Bar.Mode, doc.Settings.Bar.Width)); // il resto si tiene
        Assert.Single(doc.Connections);
    }

    [Fact]
    public void TopSetInCode_IsRepairedAndTheLayoutNeverUsesTheTopEdge()
    {
        var bar = new FullScreenBarSettings { Position = (BarPosition)99 };
        bar.Normalize();
        Assert.Equal(BarPosition.Left, bar.Position);

        var area = new Rectangle(100, 50, 1920, 1080);
        foreach (var position in Enum.GetValues<BarPosition>())
            foreach (var mode in Enum.GetValues<BarMode>())
            {
                var r = FullScreenBarLayout.Compute(area, new FullScreenBarSettings { Position = position, Mode = mode }, barShown: true);
                Assert.True(area.Contains(r.Bar));
                // Una barra che occupa la parte alta tocca il bordo in alto senza arrivare in fondo: mai.
                Assert.False(r.Bar.Top == area.Top && r.Bar.Bottom < area.Bottom && r.Bar.Width == area.Width);
                Assert.Equal(area.Top, r.Session.Top); // la parte alta resta della sessione
            }
        Assert.DoesNotContain(Enum.GetNames<BarPosition>(), n => n is "Top" or "Up");
    }

    // ---------- sezioni e ordine ----------

    [Fact]
    public void SectionOrder_IsKeptAcrossSaveAndLoad_WithDuplicatesAndUnknownsRepaired()
    {
        var doc = new ConnectionsDocument();
        var bar = doc.Settings.Bar;
        bar.Position = BarPosition.Bottom;
        bar.Mode = BarMode.Hidden;
        bar.Width = 180;
        bar.RevealDelayMs = 800;
        bar.SessionStyle = SessionListStyle.Thumbnails;
        bar.Sections =
        [
            new(BarSection.QuickButtons),
            new(BarSection.ConnectedSessions),
            new(BarSection.QuickTexts, visible: false),
            new(BarSection.Workspaces),
            new(BarSection.Favorites, visible: false),
            new(BarSection.AllConnections),
            new(BarSection.SessionActions, visible: false),
        ];
        var store = new ConnectionStore(Path.Combine(_dir, "ordine.json"));
        store.Save(doc);
        var loaded = store.Load().Settings.Bar;
        Assert.Equal((BarPosition.Bottom, BarMode.Hidden, 180, 800, SessionListStyle.Thumbnails),
            (loaded.Position, loaded.Mode, loaded.Width, loaded.RevealDelayMs, loaded.SessionStyle));
        Assert.Equal(Sections(bar), Sections(loaded));
        Assert.Equal([BarSection.QuickButtons, BarSection.ConnectedSessions, BarSection.Workspaces, BarSection.AllConnections],
            loaded.VisibleSections);

        // Doppioni e sezioni sconosciute si scartano; le mancanti finiscono in fondo, nascoste.
        var json = """
            { "version": 6, "settings": { "fullScreenBar": { "position": "Right", "width": 5000, "revealDelayMs": -3,
              "sections": [ { "kind": "Workspaces" }, { "kind": "SezioneFutura" }, { "kind": "Workspaces", "visible": false },
                            { "kind": "ConnectedSessions", "visible": false } ] } } }
            """;
        var repaired = new ConnectionStore(WriteJson(json)).Load().Settings.Bar;
        Assert.Equal((BarPosition.Right, FullScreenBarSettings.MaxWidth, 0), (repaired.Position, repaired.Width, repaired.RevealDelayMs));
        Assert.Equal((BarSection.Workspaces, true), Sections(repaired)[0]);
        Assert.Equal((BarSection.ConnectedSessions, false), Sections(repaired)[1]);
        Assert.Equal(Enum.GetValues<BarSection>().Length, repaired.Sections.Count);
        // Le mancanti finiscono nascoste, tranne le azioni sulla sessione (aggiunte dopo la 1.5.0), che arrivano mostrate.
        Assert.Equal([BarSection.Workspaces, BarSection.SessionActions], repaired.VisibleSections);
    }

    [Fact]
    public void MoveSection_ChangesTheOrderWithinBounds()
    {
        var bar = new FullScreenBarSettings();
        Assert.Equal(1, bar.MoveSection(BarSection.ConnectedSessions, +1));
        Assert.Equal(BarSection.Favorites, bar.Sections[0].Kind);
        Assert.Equal(0, bar.MoveSection(BarSection.ConnectedSessions, -5));
        int last = bar.Sections.Count - 1;
        Assert.Equal(last, bar.MoveSection(BarSection.QuickTexts, +1));
        Assert.Equal(last - 1, bar.MoveSection(BarSection.QuickTexts, -1));
        Assert.Equal(BarSection.QuickTexts, bar.Sections[last - 1].Kind);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var settings = new AppSettings();
        var copy = settings.Clone();
        copy.Bar.Position = BarPosition.Right;
        copy.Bar.Sections[0].Visible = false;
        Assert.Equal(BarPosition.Left, settings.Bar.Position);
        Assert.True(settings.Bar.Sections[0].Visible);
    }

    // ---------- esportazione e importazione .rdpm ----------

    [Fact]
    public void Rdpm_ExportsAndImportsTheBarWithTheSettings()
    {
        var source = new ConnectionsDocument { Connections = [new RdpConnection { Name = "A", Host = "a.example.test" }] };
        var bar = source.Settings.Bar;
        bar.Position = BarPosition.Right;
        bar.Mode = BarMode.AutoReveal;
        bar.Width = 340;
        bar.RevealDelayMs = 600;
        bar.SessionStyle = SessionListStyle.Thumbnails;
        bar.MoveSection(BarSection.QuickTexts, -10);
        bar.Sections.Single(s => s.Kind == BarSection.Favorites).Visible = false;
        var expected = Sections(bar);

        foreach (var password in new[] { null, "password-del-file" })
        {
            var json = RdpmFile.Export(source, password, 1000, includeSettings: true);
            Assert.Contains("fullScreenBar", password is null ? json : "fullScreenBar");

            var target = new ConnectionsDocument();
            var kept = RdpmFile.ApplyImport(target, RdpmFile.Import(json, password), ImportMode.Merge);
            Assert.False(kept.SettingsApplied);
            Assert.Equal(BarPosition.Left, target.Settings.Bar.Position); // senza «applica impostazioni» non cambia

            var applied = RdpmFile.ApplyImport(target, RdpmFile.Import(json, password), ImportMode.Merge, applySettings: true);
            Assert.True(applied.SettingsApplied);
            var got = target.Settings.Bar;
            Assert.Equal((BarPosition.Right, BarMode.AutoReveal, 340, 600, SessionListStyle.Thumbnails),
                (got.Position, got.Mode, got.Width, got.RevealDelayMs, got.SessionStyle));
            Assert.Equal(expected, Sections(got));
            Assert.Equal(BarSection.QuickTexts, got.Sections[0].Kind);
            Assert.NotSame(source.Settings.Bar, got);
        }
    }

    [Fact]
    public void Rdpm_WithTopPosition_ImportsAsLeft()
    {
        var source = new ConnectionsDocument();
        source.Settings.Bar.Mode = BarMode.Hidden;
        var json = RdpmFile.Export(source, includeSettings: true).Replace("\"position\": \"Left\"", "\"position\": \"Top\"");
        Assert.Contains("\"Top\"", json);
        var target = new ConnectionsDocument();
        RdpmFile.ApplyImport(target, RdpmFile.Import(json), ImportMode.Merge, applySettings: true);
        Assert.Equal((BarPosition.Left, BarMode.Hidden), (target.Settings.Bar.Position, target.Settings.Bar.Mode));
    }

    // ---------- geometria ----------

    [Theory]
    [InlineData(BarPosition.Left)]
    [InlineData(BarPosition.Right)]
    [InlineData(BarPosition.Bottom)]
    public void FixedBar_SessionUsesTheRestWithoutOverlap(BarPosition position)
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var settings = new FullScreenBarSettings { Position = position, Mode = BarMode.Fixed, Width = 300 };
        var r = FullScreenBarLayout.Compute(area, settings, barShown: true);
        Assert.False(r.Overlay);
        Assert.False(r.Session.IntersectsWith(r.Bar));
        Assert.Equal(area.Width * area.Height, r.Session.Width * r.Session.Height + r.Bar.Width * r.Bar.Height);
        Assert.Equal(300, position == BarPosition.Bottom ? r.Bar.Height : r.Bar.Width);
        Assert.Equal(area, Rectangle.Union(r.Session, r.Bar));

        // Barra fissa nascosta a mano: la sessione torna a tutto lo schermo.
        Assert.Equal(area, FullScreenBarLayout.Compute(area, settings, barShown: false).Session);
    }

    [Theory]
    [InlineData(BarMode.AutoReveal)]
    [InlineData(BarMode.Hidden)]
    public void RevealedBar_OverlaysTheSessionWhichKeepsTheWholeScreen(BarMode mode)
    {
        var area = new Rectangle(-1920, 0, 1920, 1080);
        var r = FullScreenBarLayout.Compute(area, new FullScreenBarSettings { Position = BarPosition.Right, Mode = mode }, barShown: true);
        Assert.True(r.Overlay);
        Assert.Equal(area, r.Session);
        Assert.Equal(new Rectangle(-260, 0, 260, 1080), r.Bar);
        var closed = FullScreenBarLayout.Compute(area, new FullScreenBarSettings { Mode = mode }, barShown: false);
        Assert.Equal((area, Rectangle.Empty), (closed.Session, closed.Bar));
    }

    [Fact]
    public void Layout_BarNeverTakesMoreThanHalfTheScreen()
    {
        var r = FullScreenBarLayout.Compute(new Rectangle(0, 0, 800, 300), new FullScreenBarSettings { Position = BarPosition.Bottom, Width = 600 }, true);
        Assert.Equal(150, r.Bar.Height);
        Assert.Equal(150, r.Session.Height);
    }

    [Fact]
    public void RevealEdge_IsOnTheBarSideOnly()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.True(FullScreenBarLayout.IsOnRevealEdge(new Point(0, 500), area, BarPosition.Left));
        Assert.False(FullScreenBarLayout.IsOnRevealEdge(new Point(5, 500), area, BarPosition.Left));
        Assert.True(FullScreenBarLayout.IsOnRevealEdge(new Point(1919, 10), area, BarPosition.Right));
        Assert.True(FullScreenBarLayout.IsOnRevealEdge(new Point(900, 1079), area, BarPosition.Bottom));
        // Il bordo in alto non apre niente, in nessuna posizione.
        foreach (var position in Enum.GetValues<BarPosition>())
            Assert.False(FullScreenBarLayout.IsOnRevealEdge(new Point(900, 0), area, position));
        Assert.False(FullScreenBarLayout.IsOnRevealEdge(new Point(-5, 500), area, BarPosition.Left)); // fuori dal monitor
    }

    // ---------- pulsanti rapidi = scorciatoie ----------

    [Fact]
    public void QuickButtons_AreTheSameActionsAsTheShortcuts()
    {
        var expected = ShortcutMap.Defaults.Keys.Where(a => ShortcutMap.SessionNumber(a) is null).OrderBy(a => a).ToList();
        Assert.Equal(expected, FullScreenBarLayout.QuickActions.OrderBy(a => a).ToList());
        Assert.Contains(ShortcutAction.ToggleFullScreen, FullScreenBarLayout.QuickActions);
        Assert.Contains(ShortcutAction.Lock, FullScreenBarLayout.QuickActions);
        Assert.All(FullScreenBarLayout.QuickActions, a => Assert.NotEqual(a.ToString(), ShortcutMap.Describe(a)));
    }

    // ---------- azioni sulla sessione (Ctrl+Alt+Canc, foto, monitor) ----------

    [Fact]
    public void SessionActions_HaveTheThree11FunctionsAndAreShownByDefault()
    {
        Assert.Equal([SessionBarAction.SendCtrlAltDel, SessionBarAction.Snapshot, SessionBarAction.NextMonitor], FullScreenBarLayout.SessionActions);
        Assert.Equal(["Invia Ctrl+Alt+Canc", "Foto della sessione", "Monitor successivo"],
            FullScreenBarLayout.SessionActions.Select(FullScreenBarSettings.Describe));
        Assert.Contains(BarSection.SessionActions, new AppSettings().Bar.VisibleSections);
        Assert.Contains(BarSection.SessionActions, FullScreenBarSettings.FromLegacy(SidebarMode.AutoHide).VisibleSections);
    }

    [Fact]
    public void Settings150_GetTheSessionActionsWithoutLosingTheirSections()
    {
        // Barra salvata dalla 1.5.0 (senza la sezione nuova), con ordine e sezioni nascoste scelti dall'utente.
        var json = """
            { "version": 6, "settings": { "fullScreenBar": { "position": "Right", "mode": "AutoReveal", "width": 310,
              "revealDelayMs": 450, "sessionStyle": "Thumbnails",
              "sections": [ { "kind": "QuickTexts", "visible": true }, { "kind": "ConnectedSessions", "visible": true },
                            { "kind": "Favorites", "visible": false }, { "kind": "QuickButtons", "visible": true },
                            { "kind": "AllConnections", "visible": true }, { "kind": "Workspaces", "visible": false } ] } } }
            """;
        var store = new ConnectionStore(WriteJson(json));
        var bar = store.Load().Settings.Bar;
        Assert.Equal((BarPosition.Right, BarMode.AutoReveal, 310, 450, SessionListStyle.Thumbnails),
            (bar.Position, bar.Mode, bar.Width, bar.RevealDelayMs, bar.SessionStyle));
        Assert.Equal(
            [(BarSection.QuickTexts, true), (BarSection.ConnectedSessions, true), (BarSection.Favorites, false),
             (BarSection.QuickButtons, true), (BarSection.SessionActions, true), (BarSection.AllConnections, true),
             (BarSection.Workspaces, false)],
            Sections(bar));

        // Chi poi la nasconde la ritrova nascosta.
        var doc = store.Load();
        doc.Settings.Bar.Sections.Single(s => s.Kind == BarSection.SessionActions).Visible = false;
        store.Save(doc);
        Assert.DoesNotContain(BarSection.SessionActions, store.Load().Settings.Bar.VisibleSections);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(160)]
    [InlineData(200)]
    [InlineData(260)]
    [InlineData(600)]
    public void ActionButtonLayout_NameAndShortcutNeverOverlap(int width)
    {
        foreach (var (name, shortcut) in new[] { (190, 70), (60, 70), (120, 110), (300, 0), (40, 300) })
        {
            var l = FullScreenBarLayout.LayoutActionButton(width, name, shortcut, 15);
            Assert.False(l.Name.IntersectsWith(l.Shortcut), $"larghezza {width}, nome {name}, scorciatoia {shortcut}");
            Assert.True(l.Name.Left >= 0 && l.Name.Right <= width);
            Assert.True(l.Shortcut.IsEmpty || (l.Shortcut.Left >= 0 && l.Shortcut.Right <= width));
            Assert.True(l.Name.Bottom <= l.Height && l.Shortcut.Bottom <= l.Height);
            if (shortcut > 0 && !l.TwoLines)
                Assert.True(l.Name.Width >= name, "su una riga il nome deve starci per intero");
        }
        // Su una riga quando c'è posto, su due quando non c'è.
        Assert.False(FullScreenBarLayout.LayoutActionButton(400, 150, 80, 15).TwoLines);
        Assert.True(FullScreenBarLayout.LayoutActionButton(200, 180, 80, 15).TwoLines);
    }

    [Fact]
    public void AllConnections_SearchFiltersByNameHostAndLabel()
    {
        RdpConnection C(string name, string host, string label = "") => new() { Name = name, Host = host, Label = label };
        var list = new[] { C("Magazzino", "mag.example.test"), C("Ufficio", "uff.example.test", "PROD"), C("Laboratorio", "lab.example.test") };
        Assert.Equal(["Magazzino", "Ufficio", "Laboratorio"], FullScreenBarLayout.FilterConnections("  ", list).Select(c => c.Name));
        Assert.Equal(["Magazzino"], FullScreenBarLayout.FilterConnections("magaz", list).Select(c => c.Name));
        Assert.Equal(["Ufficio"], FullScreenBarLayout.FilterConnections("prod", list).Select(c => c.Name));
        Assert.Equal(["Laboratorio"], FullScreenBarLayout.FilterConnections("lab.example", list).Select(c => c.Name));
        Assert.Empty(FullScreenBarLayout.FilterConnections("zzzz", list));
    }

    [Fact]
    public void Describe_HasItalianNamesForEverySectionPositionAndMode()
    {
        Assert.All(Enum.GetValues<BarSection>(), s => Assert.NotEqual(s.ToString(), FullScreenBarSettings.Describe(s)));
        Assert.All(Enum.GetValues<BarPosition>(), p => Assert.NotEqual(p.ToString(), FullScreenBarSettings.Describe(p)));
        Assert.All(Enum.GetValues<BarMode>(), m => Assert.NotEqual(m.ToString(), FullScreenBarSettings.Describe(m)));
    }
}
