using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Un connections.json scritto dalla 1.0.0 (dati fittizi in Fixtures) deve caricarsi e risalvarsi senza perdite.</summary>
public sealed class CompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerCompat-" + Guid.NewGuid().ToString("N"));
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "connections-1.0.0.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string CopyFixture()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        File.Copy(FixturePath, path);
        return path;
    }

    private static void AssertSameAs10(ConnectionsDocument doc)
    {
        Assert.Equal(["Clienti", "Clienti/Esempio Srl", "Laboratorio vuoto"], doc.Groups);
        Assert.Equal(2, doc.Connections.Count);

        var a = doc.Connections[0];
        Assert.Equal(Guid.Parse("3f1c2d4e-0000-4000-8000-000000000001"), a.Id);
        Assert.Equal("Server di prova", a.Name);
        Assert.Equal("srv01.example.test", a.Host);
        Assert.Equal(3390, a.Port);
        Assert.Equal("utente.prova", a.Username);
        Assert.Equal("DOMINIO", a.Domain);
        // Password DPAPI di un altro utente: non decifrabile, ma conservata così com'è.
        Assert.Null(a.Password);
        Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEA==", a.ProtectedPassword);
        Assert.Equal(ResolutionMode.Fixed, a.ResolutionMode);
        Assert.Equal(1600, a.DesktopWidth);
        Assert.Equal(900, a.DesktopHeight);
        Assert.False(a.RedirectClipboard);
        Assert.True(a.RedirectDrives);
        Assert.Equal("Clienti/Esempio Srl", a.Group);

        var b = doc.Connections[1];
        Assert.Equal("192.0.2.10", b.Host);
        Assert.Equal("192.0.2.10", b.DisplayName);
        Assert.Equal(ResolutionMode.FitToWindow, b.ResolutionMode);
        Assert.True(b.RedirectClipboard);
        Assert.Equal("", b.Group);
    }

    [Fact]
    public void Load_Version10File_KeepsEveryField()
    {
        var doc = new ConnectionStore(CopyFixture()).Load();
        Assert.Equal(1, doc.Version);
        AssertSameAs10(doc);
    }

    [Fact]
    public void Load_Version10File_UsesDefaultsForNewFields()
    {
        var doc = new ConnectionStore(CopyFixture()).Load();
        foreach (var c in doc.Connections)
        {
            Assert.False(c.IsFavorite);
            Assert.Null(c.LastConnectedUtc);
            Assert.Null(c.Color);
            Assert.Equal("", c.Label);
            Assert.Equal("", c.MacAddress);
            Assert.Equal(AudioMode.Local, c.AudioMode);
            Assert.False(c.RedirectPrinters);
            Assert.False(c.RedirectMicrophone);
            Assert.False(c.UseAllMonitors);
            Assert.False(c.AutoReconnect);
            Assert.Equal(ReconnectPolicy.DefaultMaxAttempts, c.AutoReconnectMaxAttempts);
        }
        Assert.NotNull(doc.Settings);
        Assert.Null(doc.Settings.PreferredMonitor);
        Assert.Equal(SidebarMode.Pinned, doc.Settings.SidebarMode);
        Assert.False(doc.Settings.ReopenSessionsOnStartup);
        Assert.Empty(doc.Settings.OpenSessions);
        Assert.Equal(ShortcutMap.Defaults, doc.Settings.GetShortcuts().All);
    }

    [Fact]
    public void Version10File_SavedAgain_LosesNothingAndBecomesVersion2()
    {
        var store = new ConnectionStore(CopyFixture());
        store.Save(store.Load());

        var reloaded = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, reloaded.Version);
        AssertSameAs10(reloaded);
    }

    [Fact]
    public void NewFieldsAndSettings_RoundTrip()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        var when = new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc);
        var c = new RdpConnection
        {
            Host = "pc.example.test", IsFavorite = true, LastConnectedUtc = when, Color = "#1E90FF", Label = "PROD",
            MacAddress = "00:11:22:33:44:55", AudioMode = AudioMode.Remote, RedirectPrinters = true, RedirectMicrophone = true,
            UseAllMonitors = true, AutoReconnect = true, AutoReconnectMaxAttempts = 7,
        };
        var doc = new ConnectionsDocument { Connections = [c] };
        doc.Settings.PreferredMonitor = @"\\.\DISPLAY2";
        doc.Settings.SidebarMode = SidebarMode.AutoHide;
        doc.Settings.ReopenSessionsOnStartup = true;
        doc.Settings.OpenSessions = [c.Id];
        var shortcuts = doc.Settings.GetShortcuts();
        shortcuts[ShortcutAction.NextSession] = KeyCombo.Parse("Ctrl+Alt+PagGiù");
        doc.Settings.SetShortcuts(shortcuts);
        store.Save(doc);

        var loaded = store.Load();
        var l = Assert.Single(loaded.Connections);
        Assert.True(l.IsFavorite);
        Assert.Equal(when, l.LastConnectedUtc);
        Assert.Equal("#1E90FF", l.Color);
        Assert.Equal("PROD", l.Label);
        Assert.Equal("00:11:22:33:44:55", l.MacAddress);
        Assert.Equal(AudioMode.Remote, l.AudioMode);
        Assert.True(l.RedirectPrinters);
        Assert.True(l.RedirectMicrophone);
        Assert.True(l.UseAllMonitors);
        Assert.True(l.AutoReconnect);
        Assert.Equal(7, l.AutoReconnectMaxAttempts);
        Assert.Equal(@"\\.\DISPLAY2", loaded.Settings.PreferredMonitor);
        Assert.Equal(SidebarMode.AutoHide, loaded.Settings.SidebarMode);
        Assert.True(loaded.Settings.ReopenSessionsOnStartup);
        Assert.Equal([c.Id], loaded.Settings.OpenSessions);
        var map = loaded.Settings.GetShortcuts();
        Assert.Equal(new KeyCombo(true, true, false, System.Windows.Forms.Keys.PageDown), map[ShortcutAction.NextSession]);
        Assert.Equal(ShortcutMap.Defaults[ShortcutAction.PreviousSession], map[ShortcutAction.PreviousSession]);
    }

    [Fact]
    public void FavoritesRecentAndFolder_Queries()
    {
        var a = new RdpConnection { Name = "b", Host = "a", Group = "Clienti", IsFavorite = true };
        var b = new RdpConnection { Name = "a", Host = "b", Group = "Clienti/Sede", IsFavorite = true };
        var c = new RdpConnection { Host = "c", Group = "Altro" };
        var doc = new ConnectionsDocument { Connections = [a, b, c] };

        Assert.Equal([b, a], doc.Favorites());
        Assert.Equal([a, b], doc.ConnectionsInFolder("Clienti"));
        Assert.Equal([b], doc.ConnectionsInFolder("Clienti/Sede"));
        Assert.Equal([a, b, c], doc.ConnectionsInFolder(""));

        Assert.Empty(doc.Recent());
        doc.MarkConnected(c.Id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        doc.MarkConnected(a.Id, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal([a, c], doc.Recent());
        Assert.Equal([a], doc.Recent(1));
    }

    [Theory]
    [InlineData("#1e90ff", "#1E90FF")]
    [InlineData("1e90ff", "#1E90FF")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("", null)]
    [InlineData("rosso", null)]
    [InlineData("#12345", null)]
    public void NormalizeColor(string input, string? expected) =>
        Assert.Equal(expected, RdpConnection.NormalizeColor(input));

    [Fact]
    public void ReconnectPolicy_RespectsLimitAndReasons()
    {
        var c = new RdpConnection { AutoReconnect = true, AutoReconnectMaxAttempts = 2 };
        Assert.True(ReconnectPolicy.ShouldReconnect(c, 2308, 0));
        Assert.True(ReconnectPolicy.ShouldReconnect(c, 2308, 1));
        Assert.False(ReconnectPolicy.ShouldReconnect(c, 2308, 2));
        Assert.False(ReconnectPolicy.ShouldReconnect(c, 1, 0));    // chiusa dall'utente
        Assert.False(ReconnectPolicy.ShouldReconnect(c, 2055, 0)); // credenziali sbagliate
        Assert.False(ReconnectPolicy.ShouldReconnect(new RdpConnection { AutoReconnect = false }, 2308, 0));

        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectPolicy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(4), ReconnectPolicy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(16), ReconnectPolicy.DelayBefore(4));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectPolicy.DelayBefore(10));
    }

    [Fact]
    public void RdpFile_RoundTripsAudioPrintersMicrophoneAndMultimon()
    {
        var c = new RdpConnection
        {
            Host = "pc", AudioMode = AudioMode.None, RedirectPrinters = true, RedirectMicrophone = true, UseAllMonitors = true,
        };
        var parsed = RdpFile.Parse(RdpFile.Generate(c));
        Assert.Equal(AudioMode.None, parsed.AudioMode);
        Assert.True(parsed.RedirectPrinters);
        Assert.True(parsed.RedirectMicrophone);
        Assert.True(parsed.UseAllMonitors);
    }
}
