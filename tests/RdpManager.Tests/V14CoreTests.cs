using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Core 1.4: spazi di lavoro, PIN di blocco, profili di qualità, CSV, azioni di gruppo, riepilogo delle
/// scorciatoie, compatibilità con i file 1.0-1.3.</summary>
public sealed class V14CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV14-" + Guid.NewGuid().ToString("N"));

    public V14CoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static RdpConnection C(string name, string group = "") => new() { Name = name, Host = name.ToLowerInvariant() + ".example.test", Group = group };

    // ---------- spazi di lavoro ----------

    [Fact]
    public void Workspace_SaveAndPlan_KeepsOrderLayoutAndDropsMissingConnections()
    {
        var a = C("A");
        var b = C("B");
        var c = C("C");
        var doc = new ConnectionsDocument { Connections = [a, b, c] };
        var ghost = Guid.NewGuid();
        var ws = doc.SaveWorkspace("Mattina", new WorkspaceLayout([c.Id, ghost, a.Id], a.Id, @"\\.\DISPLAY2", true, c.Id, a.Id, 0.3));

        Assert.Equal([c.Id, a.Id], ws.Connections);    // la connessione inesistente si scarta
        Assert.Equal(a.Id, ws.Selected);
        var plan = doc.PlanWorkspace(ws);
        Assert.Equal([c, a], plan.Open);
        Assert.Same(a, plan.Selected);
        Assert.Equal((@"\\.\DISPLAY2", true, 0.3), (plan.Monitor, plan.FullScreen, plan.SplitRatio));
        Assert.Same(c, plan.SplitLeft);
        Assert.Same(a, plan.SplitRight);
        Assert.Equal(0, plan.Missing);

        // Salvando con lo stesso nome si aggiorna (senza perdere la scorciatoia).
        ws.Shortcut = "Ctrl+Alt+Maiusc+1";
        var again = doc.SaveWorkspace("mattina", new WorkspaceLayout([b.Id], null, null, false, null, null, 0.5));
        Assert.Same(ws, again);
        Assert.Single(doc.Workspaces);
        Assert.Equal("Ctrl+Alt+Maiusc+1", again.Shortcut);
        Assert.Null(again.SplitLeft);

        // Una connessione eliminata sparisce anche dagli spazi.
        doc.SaveWorkspace("Tutte", new WorkspaceLayout([a.Id, b.Id], b.Id, null, false, a.Id, b.Id, 0.5));
        doc.Connections.Remove(b);
        var plan2 = doc.PlanWorkspace(doc.Workspaces[1]);
        Assert.Equal([a], plan2.Open);
        Assert.Equal(1, plan2.Missing);
        Assert.Null(plan2.SplitLeft); // l'affiancata richiede entrambe
        doc.RemoveFromWorkspaces(b.Id);
        Assert.Equal([a.Id], doc.Workspaces[1].Connections);
    }

    [Fact]
    public void Workspace_SurvivesSaveAndLoad()
    {
        var a = C("A");
        var doc = new ConnectionsDocument { Connections = [a] };
        var ws = doc.SaveWorkspace("Sera", new WorkspaceLayout([a.Id], a.Id, null, true, null, null, 0.5));
        ws.Shortcut = "Ctrl+Alt+Maiusc+2";
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        var loaded = Assert.Single(store.Load().Workspaces);
        Assert.Equal(("Sera", true, "Ctrl+Alt+Maiusc+2"), (loaded.Name, loaded.FullScreen, loaded.Shortcut));
        Assert.Equal([a.Id], loaded.Connections);
    }

    // ---------- PIN ----------

    [Fact]
    public void Pin_VerifiesTheRightOne_RejectsWrongOnes_AndIsNeverStoredInClear()
    {
        var hash = PinLock.Create("482913", iterations: 10_000);
        Assert.True(PinLock.Verify("482913", hash));
        Assert.False(PinLock.Verify("482914", hash));
        Assert.False(PinLock.Verify("", hash));
        Assert.False(PinLock.Verify(null, hash));
        Assert.False(PinLock.Verify("482913", null));
        Assert.NotEqual(hash.Salt, PinLock.Create("482913", 10_000).Salt); // salt nuovo ogni volta

        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var doc = new ConnectionsDocument();
        doc.Settings.LockPin = hash;
        store.Save(doc);
        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("482913", json);
        Assert.Contains("\"lockPin\"", json); // solo l'impronta (salt, hash, iterazioni)
        Assert.True(PinLock.Verify("482913", store.Load().Settings.LockPin));

        // Il PIN non esce mai nei .rdpm, neanche con le impostazioni.
        Assert.DoesNotContain("lockPin", RdpmFile.Export(doc, includeSettings: true));
    }

    [Theory]
    [InlineData("123", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12a4", false)]
    [InlineData("1111", false)]
    [InlineData("2580", true)]
    public void Pin_Validation(string pin, bool ok) => Assert.Equal(ok, PinLock.Validate(pin) is null);

    [Fact]
    public void Lock_PasswordsStaySealedUntilUnlock_AndAForgottenPinKeepsTheConnections()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var c = new RdpConnection { Host = "pc", Password = "Segreta-Conn" };
        var p = new CredentialProfile { Username = "u", Password = "Segreta-Prof" };
        store.Save(new ConnectionsDocument { Connections = [c], CredentialProfiles = [p] });

        // Avvio bloccato: le password restano cifrate.
        var locked = store.Load(decryptPasswords: false);
        Assert.Null(locked.Connections[0].Password);
        Assert.NotNull(locked.Connections[0].ProtectedPassword);
        Assert.Null(locked.CredentialProfiles[0].Password);
        Assert.Equal(2, locked.UnsealPasswords());
        Assert.Equal("Segreta-Conn", locked.Connections[0].Password);
        Assert.Equal("Segreta-Prof", locked.CredentialProfiles[0].Password);

        // Blocco durante l'uso: in memoria non restano password in chiaro; il salvataggio non perde nulla.
        Assert.Equal(2, locked.SealPasswords());
        Assert.Null(locked.Connections[0].Password);
        store.Save(locked);
        Assert.Equal("Segreta-Conn", store.Load().Connections[0].Password);

        // PIN dimenticato: connessioni e profili restano, le password vanno reinserite.
        locked.Settings.LockPin = PinLock.Create("2580", 10_000);
        Assert.Equal(2, locked.ForgetPinAndPasswords());
        Assert.Null(locked.Settings.LockPin);
        Assert.Single(locked.Connections);
        Assert.False(locked.Connections[0].HasPassword);
        Assert.False(locked.CredentialProfiles[0].HasPassword);
    }

    [Fact]
    public void LockPolicy_OnlyWithAPinAndAfterTheIdleTime()
    {
        var s = new AppSettings { LockAfterMinutes = 5 };
        Assert.False(LockPolicy.ShouldLock(s, TimeSpan.FromHours(1)));   // senza PIN mai
        s.LockPin = PinLock.Create("2580", 10_000);
        Assert.False(LockPolicy.ShouldLock(s, TimeSpan.FromMinutes(4)));
        Assert.True(LockPolicy.ShouldLock(s, TimeSpan.FromMinutes(5)));
        s.LockAfterMinutes = 0;
        Assert.False(LockPolicy.ShouldLock(s, TimeSpan.FromHours(5)));   // solo a mano
    }

    // ---------- profili di qualità e sessione tenuta sveglia ----------

    [Theory]
    [InlineData(QualityProfile.Automatic, 32, 7)]
    [InlineData(QualityProfile.LocalNetwork, 32, 6)]
    [InlineData(QualityProfile.SlowConnection, 16, 2)]
    public void Quality_RoundTripsThroughRdpFiles(QualityProfile profile, int bpp, int connectionType)
    {
        var text = RdpFile.Generate(new RdpConnection { Host = "pc", Quality = profile });
        Assert.Contains($"session bpp:i:{bpp}", text);
        Assert.Contains($"connection type:i:{connectionType}", text);
        Assert.Equal(profile, RdpFile.Parse(text).Quality);
    }

    [Fact]
    public void Quality_SlowConnectionTurnsOffTheEffects_AndOldRdpFilesAreUnderstood()
    {
        var slow = QualitySettings.For(QualityProfile.SlowConnection);
        Assert.True(slow.DisableWallpaper && slow.DisableMenuAnimations && slow.DisableThemes && slow.Compression);
        Assert.Equal(QualitySettings.PerfDisableWallpaper | QualitySettings.PerfDisableFullWindowDrag
                     | QualitySettings.PerfDisableMenuAnimations | QualitySettings.PerfDisableTheming, slow.PerformanceFlags);
        var text = RdpFile.Generate(new RdpConnection { Host = "pc", Quality = QualityProfile.SlowConnection });
        Assert.Contains("disable wallpaper:i:1", text);
        Assert.Contains("compression:i:1", text);

        Assert.Equal(QualityProfile.SlowConnection, RdpFile.Parse("full address:s:pc\r\nsession bpp:i:16\r\n").Quality);
        Assert.Equal(QualityProfile.Automatic, RdpFile.Parse("full address:s:pc\r\n").Quality);
        Assert.Equal(QualityProfile.SlowConnection, RdpFile.Parse("full address:s:pc\r\nconnection type:i:1\r\n").Quality);
    }

    [Fact]
    public void KeepAlive_IntervalOnlyWhenEnabled()
    {
        Assert.Equal(0, KeepAlive.IntervalFor(new RdpConnection()));
        Assert.Equal(KeepAlive.IntervalMs, KeepAlive.IntervalFor(new RdpConnection { KeepAlive = true }));
        Assert.True(KeepAlive.IntervalMs >= 10_000);
    }

    // ---------- CSV ----------

    [Fact]
    public void Csv_SemicolonWithQuotes_AutoMappingAndBom()
    {
        var text = "\uFEFFNome;Indirizzo;Porta;Utente;Cartella;Etichetta\r\n" +
                   "\"Ufficio; piano 2\";uff.example.test;3390;SEDE\\mario;Clienti/Rossi;PROD\r\n" +
                   "Magazzino;mag.example.test:3391;;luigi;;\r\n";
        var table = CsvImport.Parse(text);
        Assert.Equal(';', table.Separator);
        var map = CsvImport.GuessMapping(table.Header);
        Assert.Equal([CsvField.Name, CsvField.Host, CsvField.Port, CsvField.Username, CsvField.Group, CsvField.Label], map);
        var rows = CsvImport.Preview(table, map, new ConnectionsDocument());
        Assert.All(rows, r => Assert.True(r.CanImport, r.Error));
        var uff = rows[0].Connection;
        Assert.Equal(("Ufficio; piano 2", "uff.example.test", 3390, "mario", "SEDE", "Clienti/Rossi", "PROD"),
            (uff.Name, uff.Host, uff.Port, uff.Username, uff.Domain, uff.Group, uff.Label));
        Assert.Equal(3391, rows[1].Connection.Port);
        Assert.Equal(3, rows[1].Line);
    }

    [Fact]
    public void Csv_CommaSeparator_MissingColumns_ErrorsAndDuplicates()
    {
        var existing = new RdpConnection { Host = "gia.example.test" };
        var doc = new ConnectionsDocument { Connections = [existing] };
        var text = "host,user\n" +
                   "nuovo.example.test,anna\n" +
                   "gia.example.test,\n" +          // c'è già (stesso host, porta e utente vuoto)
                   ",bruno\n" +                       // host mancante
                   "nuovo.example.test,anna\n" +      // ripetuta nel file
                   "\"multi\nriga.example.test\",carla\n";
        var table = CsvImport.Parse(text);
        Assert.Equal(',', table.Separator);
        var map = CsvImport.GuessMapping(table.Header);
        Assert.Equal([CsvField.Host, CsvField.Username], map);
        var rows = CsvImport.Preview(table, map, doc, folder: "Importate");
        Assert.True(rows[0].CanImport);
        Assert.Equal("nuovo.example.test", rows[0].Connection.Name); // senza colonna Nome: l'host
        Assert.Equal("Importate", rows[0].Connection.Group);
        Assert.True(rows[1].Duplicate);
        Assert.Equal("host mancante", rows[2].Error);
        Assert.True(rows[3].Duplicate);
        Assert.Equal("multi\nriga.example.test", rows[4].Connection.Host);

        var added = CsvImport.ImportInto(doc, rows);
        Assert.Equal(2, added.Count);
        Assert.Equal(3, doc.Connections.Count);
        Assert.Contains("Importate", doc.AllGroups());
        Assert.Empty(CsvImport.ImportInto(doc, CsvImport.Preview(table, map, doc))); // reimportando: nessun doppione
    }

    [Fact]
    public void Csv_InvalidPortAndMac_AndManualMapping()
    {
        var table = CsvImport.Parse("a;b;c\npc1.example.test;99999;\npc2.example.test;3389;zz:zz\n");
        var map = new[] { CsvField.Host, CsvField.Port, CsvField.MacAddress };
        Assert.Equal([CsvField.Ignore, CsvField.Ignore, CsvField.Ignore], CsvImport.GuessMapping(table.Header));
        var rows = CsvImport.Preview(table, map, new ConnectionsDocument());
        Assert.Contains("porta", rows[0].Error);
        Assert.Contains("MAC", rows[1].Error);
        Assert.Empty(CsvImport.Parse("").Rows);
    }

    // ---------- azioni di gruppo ----------

    [Fact]
    public void GroupActions_MoveAssignDelete()
    {
        var a = C("A", "X");
        var b = C("B", "X");
        var c = C("C", "Y");
        var profile = new CredentialProfile { Name = "Admin", Username = "admin" };
        var doc = new ConnectionsDocument { Connections = [a, b, c], CredentialProfiles = [profile] };
        doc.SaveWorkspace("W", new WorkspaceLayout([a.Id, c.Id], null, null, false, null, null, 0.5));
        doc.Settings.OpenSessions = [a.Id, c.Id];

        Assert.Equal(1, GroupActions.MoveToFolder(doc, [a.Id, c.Id], "X"));      // A era già in X
        Assert.Equal(2, GroupActions.MoveToFolder(doc, [a.Id, c.Id], "Nuova/Sotto"));
        Assert.Contains("Nuova/Sotto", doc.AllGroups());

        Assert.Equal(3, GroupActions.AssignProfile(doc, [a.Id, b.Id, c.Id], profile.Id));
        Assert.All(doc.Connections, x => Assert.Equal(profile.Id, x.CredentialProfileId));
        Assert.Equal(1, GroupActions.AssignProfile(doc, [b.Id], null));
        Assert.Throws<ArgumentException>(() => GroupActions.AssignProfile(doc, [a.Id], Guid.NewGuid()));

        Assert.Contains("2 connessioni: A, C", GroupActions.DescribeForConfirmation(GroupActions.Resolve(doc, [c.Id, a.Id])));
        Assert.Equal(2, GroupActions.Delete(doc, [a.Id, c.Id, Guid.NewGuid()]));
        Assert.Equal([b], doc.Connections);
        Assert.Empty(doc.Workspaces[0].Connections);
        Assert.Empty(doc.Settings.OpenSessions);
    }

    // ---------- F1 ----------

    [Fact]
    public void ShortcutSummary_ListsConfigurableFixedGlobalAndWorkspaceShortcuts()
    {
        var map = new ShortcutMap();
        var lines = ShortcutSummary.Build(map, GlobalHotkey.DefaultText, [new Workspace { Name = "Mattina", Shortcut = "Ctrl+Alt+Maiusc+1" }]);
        Assert.Contains(lines, l => l.Keys == "Ctrl+Alt+Destra" && l.Action == "Sessione successiva");
        Assert.Contains(lines, l => l.Keys == "Ctrl+Alt+L" && l.Action.Contains("Blocca"));
        Assert.Contains(lines, l => l.Keys == "F1");
        Assert.Contains(lines, l => l.Keys == GlobalHotkey.DefaultText);
        Assert.Contains(lines, l => l.Keys == "Ctrl+Alt+Maiusc+1" && l.Action.Contains("Mattina"));
        Assert.Equal(Enum.GetValues<ShortcutAction>().Length, lines.Count(l => l.Section.StartsWith("Anche dentro")));
        Assert.Empty(map.Conflicts());
    }

    // ---------- compatibilità ----------

    private string Fixture(string version)
    {
        var path = Path.Combine(_dir, $"connections-{version}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json"), path);
        return path;
    }

    [Fact]
    public void Version13File_LoadsAndResavesWithoutLosses()
    {
        var store = new ConnectionStore(Fixture("1.3.0"));
        void Check(ConnectionsDocument d)
        {
            Assert.Equal(["Uffici"], d.Groups);
            Assert.Equal(["Zeta (prima nell'ordine manuale)", "Alfa"], d.Connections.Select(c => c.Name));
            Assert.Equal(["Zeta (prima nell'ordine manuale)", "Alfa"], d.InDisplayOrder(d.Connections).Select(c => c.Name));
            var alfa = d.Connections[1];
            Assert.Equal((3391, "operatore", "SEDE", "AQIDBAUGBwgJCgsMDQ4PEg==", true, "gw.example.test", "#16A085", "TEST"),
                (alfa.Port, alfa.Username, alfa.Domain, alfa.ProtectedPassword, alfa.UseGateway, alfa.GatewayHost, alfa.Color, alfa.Label));
            Assert.Equal(Guid.Parse("4d2e1f33-0000-4000-8000-0000000000a1"), d.Connections[0].CredentialProfileId);
            Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEw==", Assert.Single(d.CredentialProfiles).ProtectedPassword);
            var s = d.Settings;
            Assert.Equal((AppTheme.Light, UpdateMode.NotifyOnly, true, ConnectionOrder.Manual, 0.6, true),
                (s.Theme, s.UpdateMode, s.VerboseLog, s.ConnectionOrder, s.SplitRatio, s.ReopenSessionsOnStartup));
            Assert.Equal("Ctrl+Alt+K", s.GetShortcuts()[ShortcutAction.QuickSearch].ToString());
            // Campi nuovi della 1.4: predefiniti.
            Assert.Null(s.LockPin);
            Assert.Equal(10, s.LockAfterMinutes);
            Assert.Empty(d.Workspaces);
            Assert.All(d.Connections, c => Assert.Equal((QualityProfile.Automatic, false), (c.Quality, c.KeepAlive)));
            Assert.Equal("Ctrl+Alt+L", s.GetShortcuts()[ShortcutAction.Lock].ToString());
        }
        var doc = store.Load();
        Assert.Equal(4, doc.Version);
        Check(doc);
        store.Save(doc);
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Check(again);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public void OlderFiles_LoadWithThe14Defaults(string version)
    {
        var store = new ConnectionStore(Fixture(version));
        var doc = store.Load();
        Assert.Equal(2, doc.Connections.Count);
        Assert.Null(doc.Settings.LockPin);
        Assert.Empty(doc.Workspaces);
        Assert.All(doc.Connections, c => Assert.Equal(QualityProfile.Automatic, c.Quality));
        store.Save(doc);
        Assert.Equal(2, store.Load().Connections.Count);
    }
}
