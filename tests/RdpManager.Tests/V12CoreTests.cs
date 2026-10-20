using System.Drawing;
using System.Windows.Forms;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Gateway nei .rdp, ricerca approssimata, testi rapidi, scorciatoie 1.2, Panoramica, vista affiancata, mstsc.</summary>
public sealed class V12CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV12-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---------- Gateway ----------

    [Fact]
    public void RdpFile_Gateway_RoundTrip()
    {
        var c = new RdpConnection { Host = "pc.interno.test", UseGateway = true, GatewayHost = "gw.example.test:8443" };
        var text = RdpFile.Generate(c);
        Assert.Contains("gatewayhostname:s:gw.example.test:8443", text);
        Assert.Contains("gatewayusagemethod:i:1", text);
        Assert.Contains("promptcredentialonce:i:1", text);

        var parsed = RdpFile.Parse(text);
        Assert.True(parsed.UseGateway);
        Assert.Equal("gw.example.test:8443", parsed.GatewayHost);
        Assert.Equal(GatewayCredentialsSource.SameAsConnection, parsed.GatewayCredentials);

        c.GatewayCredentials = GatewayCredentialsSource.Profile;
        Assert.Contains("promptcredentialonce:i:0", RdpFile.Generate(c));
        Assert.Equal(GatewayCredentialsSource.Profile, RdpFile.Parse(RdpFile.Generate(c)).GatewayCredentials);
    }

    [Fact]
    public void RdpFile_WithoutGateway_WritesNoGatewayLines()
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "pc", GatewayHost = "gw", UseGateway = false });
        Assert.DoesNotContain("gateway", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(RdpFile.Parse(text).UseGateway);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("0", false)]
    [InlineData("4", false)]
    public void RdpFile_ParsesMstscGatewayUsage(string usage, bool expected)
    {
        var text = $"full address:s:pc\r\ngatewayhostname:s:gw.example.test\r\ngatewayusagemethod:i:{usage}\r\n";
        var c = RdpFile.Parse(text);
        Assert.Equal(expected, c.UseGateway);
        Assert.Equal("gw.example.test", c.GatewayHost);
    }

    // ---------- Ricerca approssimata ----------

    [Fact]
    public void Fuzzy_OrdersExactPrefixContainsSubsequenceTypo()
    {
        int exact = FuzzySearch.Score("server", "server");
        int prefix = FuzzySearch.Score("server", "server-prod");
        int word = FuzzySearch.Score("prod", "server-prod");
        int contains = FuzzySearch.Score("rver", "server");
        int subsequence = FuzzySearch.Score("srvpr", "server-prod");
        int typo = FuzzySearch.Score("sevrer", "server");
        Assert.True(exact > prefix && prefix > word && word > contains && contains > subsequence && subsequence > typo && typo > 0,
            $"{exact} {prefix} {word} {contains} {subsequence} {typo}");
    }

    [Theory]
    [InlineData("citta", "Città")]
    [InlineData("SERVER", "server01")]
    [InlineData("srv01", "srv-01")]
    [InlineData("sevrer", "server-prod")]
    [InlineData("magazzno", "magazzino")]
    public void Fuzzy_Matches(string query, string text) => Assert.True(FuzzySearch.Score(query, text) > 0);

    [Theory]
    [InlineData("xyz", "server")]
    [InlineData("abc", "")]
    [InlineData("", "server")]
    [InlineData("sxrvxr", "server")]
    public void Fuzzy_DoesNotMatch(string query, string text) => Assert.Equal(0, FuzzySearch.Score(query, text));

    [Fact]
    public void Fuzzy_MultipleWords_EachMustMatchSomeField()
    {
        Assert.True(FuzzySearch.Score("prod magaz", ["Magazzino", "srv-prod.example.test", "PROD"]) > 0);
        Assert.Equal(0, FuzzySearch.Score("prod ufficio", ["Magazzino", "srv-prod.example.test", "PROD"]));
    }

    [Fact]
    public void QuickSearch_FindsByNameHostLabel_OpenSessionsFirst()
    {
        var a = new RdpConnection { Name = "Magazzino", Host = "srv-mag.example.test" };
        var b = new RdpConnection { Name = "Magazzino 2", Host = "srv-mag2.example.test" };
        var c = new RdpConnection { Name = "Ufficio", Host = "pc-uff.example.test", Label = "PROD" };

        var byLabel = QuickSearch.Find("prod", [a, b, c], []);
        Assert.Equal(c, Assert.Single(byLabel).Connection);

        var byHost = QuickSearch.Find("pc-uff", [a, b, c], []);
        Assert.Equal(c, byHost[0].Connection);

        var withOpen = QuickSearch.Find("magaz", [a, b, c], [b.Id]);
        Assert.Equal(b, withOpen[0].Connection);
        Assert.Equal(QuickSearchKind.OpenSession, withOpen[0].Kind);
        Assert.Equal(QuickSearchKind.Connection, withOpen[1].Kind);

        var empty = QuickSearch.Find("", [a, b, c], [c.Id]);
        Assert.Equal([c, a, b], empty.Select(r => r.Connection));
        Assert.Empty(QuickSearch.Find("nessunrisultato", [a, b, c], []));
    }

    // ---------- Testi rapidi ----------

    [Fact]
    public void QuickText_ValidatePreviewAndTyping()
    {
        Assert.NotNull(QuickText.Validate("x", ""));
        Assert.NotNull(QuickText.Validate("x", new string('a', QuickText.MaxLength + 1)));
        Assert.Null(QuickText.Validate("", "ipconfig /all"));

        var t = new QuickText { Text = "\r\n  ipconfig /all\r\nexit" };
        Assert.Equal("ipconfig /all", t.DisplayName);
        Assert.Equal("\n  ipconfig /all\nexit", t.TextToType());
        Assert.Equal("Firma", new QuickText { Name = " Firma ", Text = "x" }.DisplayName);
        Assert.Equal("abcd…", QuickText.Preview("abcdefgh", 5));
    }

    [Fact]
    public void QuickTexts_MoveSaveAndLoad()
    {
        var a = new QuickText { Name = "A", Text = "uno" };
        var b = new QuickText { Name = "B", Text = "due\r\ntre" };
        var doc = new ConnectionsDocument { QuickTexts = [a, b] };
        Assert.True(doc.MoveQuickText(b.Id, -1));
        Assert.Equal([b, a], doc.QuickTexts);
        Assert.False(doc.MoveQuickText(b.Id, -1));

        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        var loaded = store.Load();
        Assert.Equal(["B", "A"], loaded.QuickTexts.Select(t => t.Name));
        Assert.Equal("due\r\ntre", loaded.QuickTexts[0].Text);
    }

    [Fact]
    public void QuickTexts_TravelInRdpm_WithoutDuplicates()
    {
        var source = new ConnectionsDocument { QuickTexts = [new QuickText { Name = "A", Text = "uno" }] };
        var target = new ConnectionsDocument { QuickTexts = [new QuickText { Name = "Altro nome", Text = "uno" }, new QuickText { Text = "due" }] };
        var source2 = new ConnectionsDocument { QuickTexts = [new QuickText { Text = "tre" }] };

        Assert.Equal(0, RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(source)), ImportMode.Merge).QuickTextsAdded);
        Assert.Equal(1, RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(source2)), ImportMode.Merge).QuickTextsAdded);
        Assert.Equal(["uno", "due", "tre"], target.QuickTexts.Select(t => t.Text));
    }

    // ---------- Scorciatoie 1.2 e scorciatoia globale ----------

    [Fact]
    public void Shortcuts_NewDefaults_WithoutConflicts()
    {
        var map = new ShortcutMap();
        Assert.Equal("Ctrl+Alt+G", map[ShortcutAction.Overview].ToString());
        Assert.Equal("Ctrl+Alt+Spazio", map[ShortcutAction.QuickSearch].ToString());
        Assert.Equal("Ctrl+Alt+S", map[ShortcutAction.ToggleSplitView].ToString());
        Assert.Empty(map.Conflicts());
        Assert.True(map.TryGetAction(Keys.Control | Keys.Alt | Keys.G, out var a));
        Assert.Equal(ShortcutAction.Overview, a);
    }

    [Fact]
    public void Shortcuts_NewDefaultGivesWayToAComboTheUserAlreadyChose()
    {
        // Nella 1.1 l'utente aveva messo Ctrl+Alt+S su «Sessione successiva»: la vista affiancata (nuova) non lo ruba.
        var map = ShortcutMap.FromSettings(new Dictionary<string, string> { ["NextSession"] = "Ctrl+Alt+S" });
        Assert.Equal("Ctrl+Alt+S", map[ShortcutAction.NextSession].ToString());
        Assert.True(map[ShortcutAction.ToggleSplitView].IsEmpty);
        Assert.Empty(map.Conflicts());
        Assert.True(map.TryGetAction(KeyCombo.Parse("Ctrl+Alt+S"), out var a));
        Assert.Equal(ShortcutAction.NextSession, a);
        Assert.Equal("Ctrl+Alt+G", map[ShortcutAction.Overview].ToString());
    }

    [Fact]
    public void GlobalHotkey_ParsesAndConvertsForRegisterHotKey()
    {
        var combo = GlobalHotkey.Default;
        Assert.Equal("Ctrl+Alt+Maiusc+R", combo.ToString());
        var (mods, vk) = GlobalHotkey.ToNative(combo);
        Assert.Equal(GlobalHotkey.ModControl | GlobalHotkey.ModAlt | GlobalHotkey.ModShift | GlobalHotkey.ModNoRepeat, mods);
        Assert.Equal((uint)Keys.R, vk);

        Assert.False(GlobalHotkey.TryParse("R", out _));          // senza Ctrl/Alt
        Assert.False(GlobalHotkey.TryParse("Maiusc+F5", out _));
        Assert.True(GlobalHotkey.TryParse("Ctrl+Alt+F12", out _));
        Assert.True(GlobalHotkey.FromSettings("").IsEmpty);         // disattivata
        Assert.Equal(GlobalHotkey.Default, GlobalHotkey.FromSettings(null));
        Assert.Equal(GlobalHotkey.Default, GlobalHotkey.FromSettings("non valida"));
        Assert.Null(GlobalHotkey.ConflictWith(combo, new ShortcutMap()));
        Assert.Equal(ShortcutAction.Overview, GlobalHotkey.ConflictWith(KeyCombo.Parse("Ctrl+Alt+G"), new ShortcutMap()));
    }

    [Fact]
    public void Settings12_RoundTrip_AndInvalidValuesRepaired()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var doc = new ConnectionsDocument();
        doc.Settings.Theme = AppTheme.Dark;
        doc.Settings.ShowTrayIcon = false;
        doc.Settings.CloseToTray = true;
        doc.Settings.GlobalHotkey = "";
        doc.Settings.HistoryEnabled = false;
        doc.Settings.SplitRatio = 0.3;
        store.Save(doc);
        var s = store.Load().Settings;
        Assert.Equal((AppTheme.Dark, false, true, "", false, 0.3),
            (s.Theme, s.ShowTrayIcon, s.CloseToTray, s.GlobalHotkey, s.HistoryEnabled, s.SplitRatio));

        var bad = new AppSettings { GlobalHotkey = "X", SplitRatio = double.NaN, Theme = (AppTheme)42 };
        bad.Normalize();
        Assert.Equal(GlobalHotkey.DefaultText, bad.GlobalHotkey);
        Assert.Equal(SplitLayout.DefaultRatio, bad.SplitRatio);
        Assert.Equal(AppTheme.System, bad.Theme);
    }

    // ---------- Panoramica e vista affiancata ----------

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    public void Overview_GridOnAWideScreen(int count, int cols, int rows) =>
        Assert.Equal((cols, rows), OverviewLayout.Grid(count, new Size(1920, 1080)));

    [Fact]
    public void Overview_CellsFitInsideAndDoNotOverlap()
    {
        var area = new Size(1280, 720);
        var cells = OverviewLayout.Cells(7, area);
        Assert.Equal(7, cells.Count);
        foreach (var r in cells)
        {
            Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= area.Width && r.Bottom + OverviewLayout.CaptionHeight <= area.Height, r.ToString());
            Assert.InRange(r.Width / (double)r.Height, 1.70, 1.82);
        }
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
                Assert.False(cells[i].IntersectsWith(cells[j]));

        Assert.Equal(3, OverviewLayout.HitTest(cells, new Point(cells[3].X + 5, cells[3].Bottom + 5))); // didascalia
        Assert.Equal(-1, OverviewLayout.HitTest(cells, new Point(1, 1)));
        Assert.Empty(OverviewLayout.Cells(0, area));
    }

    [Fact]
    public void Overview_ArrowKeysStayInsideTheGrid()
    {
        // 5 elementi su 3 colonne: 0 1 2 / 3 4
        Assert.Equal(1, OverviewLayout.Move(0, 5, 3, 1, 0));
        Assert.Equal(2, OverviewLayout.Move(2, 5, 3, 1, 0));  // bordo destro
        Assert.Equal(3, OverviewLayout.Move(0, 5, 3, 0, 1));
        Assert.Equal(2, OverviewLayout.Move(2, 5, 3, 0, 1));  // sotto non c'è nulla
        Assert.Equal(1, OverviewLayout.Move(4, 5, 3, 0, -1));
    }

    [Fact]
    public void SplitLayout_ClampsAndConverts()
    {
        Assert.Equal(SplitLayout.MinRatio, SplitLayout.ClampRatio(0));
        Assert.Equal(SplitLayout.MaxRatio, SplitLayout.ClampRatio(2));
        Assert.Equal(497, SplitLayout.SplitterDistance(1000, 0.5));
        Assert.Equal(0.5, SplitLayout.RatioFromDistance(497, 1000), 2);
        Assert.Equal(SplitLayout.DefaultRatio, SplitLayout.RatioFromDistance(10, 3));
    }

    // ---------- Cronologia di mstsc ----------

    [Fact]
    public void MstscHistory_MergesMruAndServers_WithoutDuplicates()
    {
        var items = MstscHistory.Merge(
            ["pc-uno.example.test", "srv.example.test:3390", "", "PC-UNO.example.test"],
            new Dictionary<string, string> { ["pc-uno.example.test"] = "UFFICIO\\mario", ["altro.example.test"] = "luigi" });
        Assert.Equal(["pc-uno.example.test", "srv.example.test:3390", "altro.example.test"], items.Select(i => i.Address));
        Assert.Equal("UFFICIO\\mario", items[0].UsernameHint);
        Assert.Equal("", items[1].UsernameHint);

        var c = MstscHistory.ToConnection(items[0]);
        Assert.Equal(("pc-uno.example.test", 3389, "mario", "UFFICIO", MstscHistory.DefaultFolder), (c.Host, c.Port, c.Username, c.Domain, c.Group));
        Assert.Equal(3390, MstscHistory.ToConnection(items[1]).Port);
    }

    [Fact]
    public void MstscHistory_ImportInto_SkipsExistingConnections()
    {
        var doc = new ConnectionsDocument { Connections = [new RdpConnection { Host = "srv.example.test", Port = 3390, Username = "admin" }] };
        var items = new[]
        {
            new MstscHistoryItem("srv.example.test:3390", ""),   // stesso host e porta, senza utente → c'è già
            new MstscHistoryItem("pc.example.test", "mario"),
        };
        Assert.True(MstscHistory.IsAlreadyPresent(doc, items[0]));
        var added = MstscHistory.ImportInto(doc, items);
        Assert.Equal("pc.example.test", Assert.Single(added).Host);
        Assert.Contains(MstscHistory.DefaultFolder, doc.AllGroups());
        Assert.Empty(MstscHistory.ImportInto(doc, items));
    }
}
