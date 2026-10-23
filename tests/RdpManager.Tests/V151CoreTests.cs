using System.Drawing;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Core 1.5.1: password nei .rdpm (connessioni e profili di credenziali), riepilogo dell'importazione,
/// misure della barra trascinandone il bordo, compatibilità con i connections.json 1.0-1.5.</summary>
public sealed class V151CoreTests : IDisposable
{
    private const int FastIterations = 1000;
    private const string FilePassword = "password del file";
    private const string ConnectionSecret = "Segreta-Connessione-àè€";
    private const string ProfileSecret = "Segreta-Profilo#42";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV151-" + Guid.NewGuid().ToString("N"));

    public V151CoreTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>Una connessione con la sua password, una che usa il profilo condiviso (anche per il gateway), il profilo.</summary>
    private static ConnectionsDocument Source()
    {
        var profile = new CredentialProfile { Name = "Amministratore", Username = "admin", Domain = "ACME", Password = ProfileSecret };
        var doc = new ConnectionsDocument();
        doc.CredentialProfiles.Add(profile);
        doc.Connections.Add(new RdpConnection { Name = "Propria", Host = "a.example.test", Username = "mario", Password = ConnectionSecret });
        doc.Connections.Add(new RdpConnection
        {
            Name = "Col profilo", Host = "b.example.test", CredentialProfileId = profile.Id,
            UseGateway = true, GatewayHost = "gw.example.test", GatewayCredentials = GatewayCredentialsSource.Profile, GatewayProfileId = profile.Id,
        });
        return doc;
    }

    private static ConnectionsDocument RoundTrip(ConnectionsDocument doc, string? password = FilePassword) =>
        RdpmFile.Import(RdpmFile.Export(doc, password, FastIterations), password);

    // ---------- .rdpm: password nella connessione e nel profilo ----------

    [Fact]
    public void RoundTrip_CarriesConnectionAndProfilePasswords_AndKeepsTheLinkToTheProfile()
    {
        var source = Source();
        var json = RdpmFile.Export(source, FilePassword, FastIterations);
        Assert.DoesNotContain(ConnectionSecret, json);
        Assert.DoesNotContain(ProfileSecret, json);

        var target = new ConnectionsDocument();
        var result = RdpmFile.ApplyImport(target, RdpmFile.Import(json, FilePassword), ImportMode.Merge);

        Assert.Equal((2, 0, 1, 2), (result.Added, result.Skipped, result.ProfilesAdded, result.PasswordsImported));
        var profile = Assert.Single(target.CredentialProfiles);
        Assert.Equal((source.CredentialProfiles[0].Id, ProfileSecret), (profile.Id, profile.Password));
        Assert.Equal(ConnectionSecret, target.Connections[0].Password);
        var linked = target.Connections[1];
        Assert.Equal((profile.Id, profile.Id, GatewayCredentialsSource.Profile),
            (linked.CredentialProfileId, linked.GatewayProfileId, linked.GatewayCredentials));
        Assert.Equal(("admin", "ACME", ProfileSecret), (target.ResolveCredentials(linked).Username,
            target.ResolveCredentials(linked).Domain, target.ResolveCredentials(linked).Password));
        Assert.Equal(ProfileSecret, target.ResolveGatewayCredentials(linked)!.Password);
    }

    [Fact]
    public void RoundTrip_PasswordsSurviveSavingToConnectionsJson()
    {
        var target = new ConnectionsDocument();
        RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(target);
        var text = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(ConnectionSecret, text);
        Assert.DoesNotContain(ProfileSecret, text);

        var again = store.Load();
        Assert.Equal(ConnectionSecret, again.Connections[0].Password);
        Assert.Equal(ProfileSecret, again.CredentialProfiles[0].Password);
        Assert.Equal(ProfileSecret, again.ResolveCredentials(again.Connections[1]).Password);
    }

    [Fact]
    public void Merge_ExistingConnectionWithoutPassword_GetsThePasswordFromTheFile()
    {
        var target = new ConnectionsDocument();
        var existing = new RdpConnection { Name = "Già qui", Host = "A.example.test", Username = "MARIO" };
        target.Connections.Add(existing);

        var result = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge);

        Assert.Equal((1, 1), (result.Added, result.Skipped));
        Assert.Equal(2, target.Connections.Count);
        Assert.Same(existing, target.Connections[0]);
        Assert.Equal("Già qui", existing.Name);        // il resto non cambia
        Assert.Equal(ConnectionSecret, existing.Password);
        Assert.Equal(2, result.PasswordsImported);      // quella data all'esistente e quella del profilo
    }

    [Fact]
    public void Merge_ExistingConnectionWithPassword_KeepsItsOwn()
    {
        var target = new ConnectionsDocument();
        target.Connections.Add(new RdpConnection { Host = "a.example.test", Username = "mario", Password = "la mia" });

        var result = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge);

        Assert.Equal("la mia", target.Connections[0].Password);
        Assert.Equal(1, result.PasswordsImported); // solo quella del profilo
    }

    [Fact]
    public void Merge_ExistingConnectionWithoutPassword_IsLinkedToTheImportedProfile()
    {
        var target = new ConnectionsDocument();
        var existing = new RdpConnection { Name = "Magazzino", Host = "b.example.test" };
        target.Connections.Add(existing);

        var result = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge);

        Assert.Equal(1, result.ConnectionsLinkedToProfile);
        Assert.Equal(target.CredentialProfiles[0].Id, existing.CredentialProfileId);
        Assert.Equal(ProfileSecret, target.ResolveCredentials(existing).Password);
    }

    [Fact]
    public void Merge_ExistingProfileWithoutPassword_GetsItAndConnectionsAreLinkedToIt()
    {
        var target = new ConnectionsDocument();
        var mine = new CredentialProfile { Name = "amministratore", Username = "ADMIN", Domain = "acme" }; // stesso profilo, altro Id
        target.CredentialProfiles.Add(mine);

        var result = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge);

        Assert.Equal((0, 2), (result.ProfilesAdded, result.PasswordsImported));
        Assert.Same(mine, Assert.Single(target.CredentialProfiles));
        Assert.Equal(ProfileSecret, mine.Password);
        var linked = target.Connections.Single(c => c.Host == "b.example.test");
        Assert.Equal((mine.Id, mine.Id), (linked.CredentialProfileId, linked.GatewayProfileId));
    }

    [Fact]
    public void Merge_ExistingProfileWithPassword_KeepsItsOwn()
    {
        var target = new ConnectionsDocument();
        var source = Source();
        target.CredentialProfiles.Add(new CredentialProfile { Id = source.CredentialProfiles[0].Id, Name = "x", Password = "la mia" });

        var result = RdpmFile.ApplyImport(target, RoundTrip(source), ImportMode.Merge);

        Assert.Equal("la mia", target.CredentialProfiles[0].Password);
        Assert.Equal(1, result.PasswordsImported); // solo quella della connessione
    }

    [Fact]
    public void Replace_KeepsTheProfilePasswordAndLinks()
    {
        var target = new ConnectionsDocument();
        target.Connections.Add(new RdpConnection { Host = "vecchia.example.test" });
        var result = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Replace);
        Assert.Equal((2, 1, 2), (result.Added, result.Removed, result.PasswordsImported));
        Assert.Equal(ProfileSecret, target.ResolveCredentials(target.Connections[1]).Password);
    }

    [Fact]
    public void ExportWithoutPassword_ImportsNoPasswords_ButKeepsTheProfileLink()
    {
        var target = new ConnectionsDocument();
        var result = RdpmFile.ApplyImport(target, RoundTrip(Source(), password: null), ImportMode.Merge);
        Assert.Equal((0, 1), (result.PasswordsImported, result.ProfilesAdded));
        Assert.All(target.Connections, c => Assert.False(c.HasPassword));
        Assert.False(target.CredentialProfiles[0].HasPassword);
        Assert.Equal(target.CredentialProfiles[0].Id, target.Connections[1].CredentialProfileId);
    }

    // ---------- scelta proposta, avviso e riepilogo ----------

    [Fact]
    public void Export_ProposesWithPasswordsOnlyWhenThereAreSome()
    {
        var doc = Source();
        Assert.Equal(2, RdpmFile.CountExportablePasswords(doc));
        Assert.True(RdpmFile.ProposeWithPasswords(doc));
        var warning = RdpmFile.WithoutPasswordsWarning(doc);
        Assert.NotNull(warning);
        Assert.Contains("2 password", warning);

        // Solo il profilo ha la password: si propone comunque «con le password».
        doc.Connections[0].Password = null;
        Assert.True(RdpmFile.ProposeWithPasswords(doc));
        Assert.Contains("1 password", RdpmFile.WithoutPasswordsWarning(doc));

        // Una password DPAPI illeggibile (altro utente) non si esporta e non conta.
        doc.CredentialProfiles[0].Password = null;
        doc.CredentialProfiles[0].ProtectedPassword = "QkxPQi1EUEFQSQ==";
        Assert.False(RdpmFile.ProposeWithPasswords(doc));
        Assert.Null(RdpmFile.WithoutPasswordsWarning(doc));
    }

    [Fact]
    public void Summary_SaysHowManyPasswordsAndProfilesWereImported()
    {
        var target = new ConnectionsDocument();
        target.Connections.Add(new RdpConnection { Host = "b.example.test" });
        var text = RdpmFile.ApplyImport(target, RoundTrip(Source()), ImportMode.Merge).Summary();
        Assert.Contains("Aggiunta 1 connessione", text);
        Assert.Contains("1 già presenti", text);
        Assert.Contains("2 password importate", text);
        Assert.Contains("1 profilo di credenziali importato", text);
        Assert.Contains("collegata al suo profilo", text);

        var none = new ImportResult(3, 0, 0).Summary();
        Assert.Contains("Aggiunte 3 connessioni", none);
        Assert.Contains("Nessuna password importata", none);
        Assert.Contains("Nessun profilo di credenziali nuovo", none);
        Assert.Contains("1 password importata", new ImportResult(1, 0, 0, PasswordsImported: 1).Summary());
        Assert.Contains("4 profili di credenziali importati", new ImportResult(1, 0, 0, ProfilesAdded: 4).Summary());
    }

    // ---------- barra a schermo intero: misura trascinando il bordo ----------

    private static readonly Rectangle Area = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(BarPosition.Left, 50, 0, 310)]    // il bordo destro va a destra: più larga
    [InlineData(BarPosition.Left, -50, 0, 210)]
    [InlineData(BarPosition.Right, -50, 0, 310)]  // il bordo sinistro va a sinistra: più larga
    [InlineData(BarPosition.Right, 50, 0, 210)]
    [InlineData(BarPosition.Bottom, 0, -50, 310)] // il bordo in alto sale: più alta
    [InlineData(BarPosition.Bottom, 0, 50, 210)]
    public void Drag_GrowsTowardsTheSession(BarPosition position, int dx, int dy, int expected)
    {
        var start = new Point(500, 500);
        Assert.Equal(expected, FullScreenBarLayout.DragThickness(position, 260, start, new Point(500 + dx, 500 + dy), Area));
    }

    [Theory]
    [InlineData(BarPosition.Left, -5000, 120)]
    [InlineData(BarPosition.Left, 5000, 600)]
    [InlineData(BarPosition.Right, 5000, 120)]
    [InlineData(BarPosition.Right, -5000, 600)]
    public void Drag_StaysWithinTheCoreLimits(BarPosition position, int dx, int expected)
    {
        Assert.Equal(expected, FullScreenBarLayout.DragThickness(position, 260, new Point(900, 0), new Point(900 + dx, 0), Area));
        Assert.Equal((120, 600), (FullScreenBarSettings.MinWidth, FullScreenBarSettings.MaxWidth));
    }

    [Fact]
    public void Drag_NeverTakesMoreThanHalfTheScreen()
    {
        Assert.Equal(540, FullScreenBarLayout.DragThickness(BarPosition.Bottom, 260, new Point(0, 900), new Point(0, 0), Area));
        var small = new Rectangle(0, 0, 800, 400);
        Assert.Equal(400, FullScreenBarLayout.DragThickness(BarPosition.Left, 260, new Point(0, 0), new Point(5000, 0), small));
        Assert.Equal(200, FullScreenBarLayout.DragThickness(BarPosition.Bottom, 150, new Point(0, 390), new Point(0, 0), small));
        Assert.Equal(120, FullScreenBarLayout.MaxThickness(new Rectangle(0, 0, 100, 100), BarPosition.Left)); // mai sotto il minimo
    }

    [Fact]
    public void Drag_TheSessionAdaptsToTheNewWidth()
    {
        var bar = new FullScreenBarSettings { Position = BarPosition.Right, Mode = BarMode.Fixed };
        bar.Width = FullScreenBarLayout.DragThickness(bar.Position, bar.Thickness, new Point(1600, 0), new Point(1500, 0), Area);
        var r = FullScreenBarLayout.Compute(Area, bar, barShown: true);
        Assert.Equal(new Rectangle(1560, 0, 360, 1080), r.Bar);
        Assert.Equal(new Rectangle(0, 0, 1560, 1080), r.Session);
        Assert.False(r.Session.IntersectsWith(r.Bar));
    }

    [Fact]
    public void ResizeGrip_IsOnTheEdgeTowardsTheSession()
    {
        var size = new Size(260, 1080);
        Assert.Equal(new Rectangle(254, 0, 6, 1080), FullScreenBarLayout.ResizeGrip(size, BarPosition.Left));
        Assert.Equal(new Rectangle(0, 0, 6, 1080), FullScreenBarLayout.ResizeGrip(size, BarPosition.Right));
        Assert.Equal(new Rectangle(0, 0, 1920, 6), FullScreenBarLayout.ResizeGrip(new Size(1920, 260), BarPosition.Bottom));
    }

    [Fact]
    public void BarWidth_IsSavedAndReloaded()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var doc = new ConnectionsDocument();
        doc.Settings.Bar.Position = BarPosition.Bottom;
        doc.Settings.Bar.Width = 333;
        doc.Settings.SidebarWidth = 411;
        store.Save(doc);
        var again = store.Load();
        Assert.Equal((BarPosition.Bottom, 333, 411), (again.Settings.Bar.Position, again.Settings.Bar.Width, again.Settings.SidebarWidth));
    }

    // ---------- barra laterale della finestra ----------

    [Fact]
    public void WindowSidebar_WidthWithinLimits()
    {
        Assert.Equal(280, new AppSettings().SidebarWidth);
        Assert.Equal(180, WindowSidebar.Fit(50, 1600));
        Assert.Equal(600, WindowSidebar.Fit(5000, 1600));
        Assert.Equal(350, WindowSidebar.Fit(350, 1600));
        Assert.Equal(300, WindowSidebar.Fit(500, 600));   // restano 300 px alle sessioni
        Assert.Equal(180, WindowSidebar.Fit(500, 300));   // finestra minuscola: mai sotto il minimo

        var s = new AppSettings { SidebarWidth = 5 };
        s.Normalize();
        Assert.Equal(180, s.SidebarWidth);
        s.SidebarWidth = 9999;
        s.Normalize();
        Assert.Equal(600, s.SidebarWidth);
    }

    [Fact]
    public void WindowSidebar_WidthTravelsWithTheExportedSettings()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Host = "x" });
        doc.Settings.SidebarWidth = 420;
        doc.Settings.Bar.Width = 380;
        var imported = RdpmFile.Import(RdpmFile.Export(doc, includeSettings: true));
        var target = new ConnectionsDocument();
        RdpmFile.ApplyImport(target, imported, ImportMode.Merge, applySettings: true);
        Assert.Equal((420, 380), (target.Settings.SidebarWidth, target.Settings.Bar.Width));
    }

    // ---------- compatibilità con i file 1.0-1.5 ----------

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    [InlineData("1.4.0")]
    [InlineData("1.5.0")]
    public void OlderFiles_LoadWithTheDefaultSidebarWidthAndResave(string version)
    {
        Assert.DoesNotContain("sidebarWidth", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json")));
        var store = new ConnectionStore(Fixture(version));
        var doc = store.Load();
        Assert.Equal(WindowSidebar.DefaultWidth, doc.Settings.SidebarWidth);
        int connections = doc.Connections.Count;
        Assert.True(connections > 0);
        store.Save(doc);
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Assert.Equal(connections, again.Connections.Count);
        Assert.Equal(WindowSidebar.DefaultWidth, again.Settings.SidebarWidth);
    }

    [Fact]
    public void Version15File_LoadsAndResavesWithoutLosses()
    {
        var store = new ConnectionStore(Fixture("1.5.0"));
        void Check(ConnectionsDocument d)
        {
            Assert.Equal(["Contabilità", "Magazzino"], d.Connections.Select(c => c.Name));
            Assert.Equal(("operatore", "SEDE", "AQIDBAUGBwgJCgsMDQ4PFA=="), (d.Connections[0].Username, d.Connections[0].Domain, d.Connections[0].ProtectedPassword));
            var profile = Assert.Single(d.CredentialProfiles);
            Assert.Equal(("Amministratore sede", "admin", "SEDE", "AQIDBAUGBwgJCgsMDQ4PEA=="),
                (profile.Name, profile.Username, profile.Domain, profile.ProtectedPassword)); // non decifrabile qui: resta com'era
            Assert.Equal(profile.Id, d.Connections[1].CredentialProfileId);
            var bar = d.Settings.Bar;
            Assert.Equal((BarPosition.Right, BarMode.Fixed, 340, 500, SessionListStyle.Thumbnails),
                (bar.Position, bar.Mode, bar.Width, bar.RevealDelayMs, bar.SessionStyle));
            Assert.Equal([BarSection.ConnectedSessions, BarSection.SessionActions, BarSection.QuickButtons, BarSection.AllConnections, BarSection.Workspaces],
                bar.VisibleSections);
            Assert.Equal((SidebarMode.AutoHide, AppTheme.Dark, 5), (d.Settings.SidebarMode, d.Settings.Theme, d.Settings.LockAfterMinutes));
            Assert.True(d.Settings.LockEnabled);
            Assert.Equal("Mattina", Assert.Single(d.Workspaces).Name);
            Assert.Equal("Data", Assert.Single(d.QuickTexts).Name);
        }
        var doc = store.Load();
        Assert.Equal(6, doc.Version);
        Check(doc);
        store.Save(doc);
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Check(again);
    }
}
