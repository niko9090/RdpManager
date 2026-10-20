using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Un connections.json scritto dalla 1.1.0 (dati fittizi in Fixtures) deve caricarsi e risalvarsi senza perdite.</summary>
public sealed class Compatibility11Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerCompat11-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string CopyFixture(string version)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"connections-{version}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json"), path);
        return path;
    }

    private static readonly Guid IdA = Guid.Parse("7a2b0c11-0000-4000-8000-000000000001");
    private static readonly Guid IdB = Guid.Parse("7a2b0c11-0000-4000-8000-000000000002");

    private static void AssertSameAs11(ConnectionsDocument doc)
    {
        Assert.Equal(["Clienti", "Clienti/Esempio Srl", "Laboratorio"], doc.Groups);
        Assert.Equal(2, doc.Connections.Count);

        var a = doc.Connections[0];
        Assert.Equal(IdA, a.Id);
        Assert.Equal(("Server di prova", "srv01.example.test", 3390, "utente.prova", "DOMINIO"), (a.Name, a.Host, a.Port, a.Username, a.Domain));
        Assert.Null(a.Password);
        Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEA==", a.ProtectedPassword); // DPAPI di un altro utente: conservata
        Assert.Equal((ResolutionMode.Fixed, 1600, 900), (a.ResolutionMode, a.DesktopWidth, a.DesktopHeight));
        Assert.False(a.RedirectClipboard);
        Assert.True(a.RedirectDrives);
        Assert.Equal(AudioMode.Remote, a.AudioMode);
        Assert.True(a.RedirectPrinters);
        Assert.True(a.RedirectMicrophone);
        Assert.True(a.UseAllMonitors);
        Assert.True(a.AutoReconnect);
        Assert.Equal(3, a.AutoReconnectMaxAttempts);
        Assert.True(a.IsFavorite);
        Assert.Equal(new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc), a.LastConnectedUtc!.Value.ToUniversalTime());
        Assert.Equal("#2E86DE", a.Color);
        Assert.Equal("PROD", a.Label);
        Assert.Equal("02:00:5E:10:00:01", a.MacAddress);
        Assert.Equal("Clienti/Esempio Srl", a.Group);

        var b = doc.Connections[1];
        Assert.Equal(IdB, b.Id);
        Assert.Equal("192.0.2.20", b.DisplayName);
        Assert.Equal(5, b.AutoReconnectMaxAttempts);
        Assert.Equal("Laboratorio", b.Group);

        var s = doc.Settings;
        Assert.Equal(@"\\.\DISPLAY2", s.PreferredMonitor);
        Assert.Equal(SidebarMode.AutoHide, s.SidebarMode);
        Assert.True(s.ReopenSessionsOnStartup);
        Assert.Equal([IdB, IdA], s.OpenSessions);
        var map = s.GetShortcuts();
        Assert.Equal("Ctrl+Alt+PagGiù", map[ShortcutAction.NextSession].ToString());
        Assert.True(map[ShortcutAction.ToggleSidebar].IsEmpty); // disattivata nella 1.1
    }

    [Fact]
    public void Load_Version11File_KeepsEveryField()
    {
        var doc = new ConnectionStore(CopyFixture("1.1.0")).Load();
        Assert.Equal(2, doc.Version);
        AssertSameAs11(doc);
    }

    [Fact]
    public void Load_Version11File_UsesDefaultsForNewFields()
    {
        var doc = new ConnectionStore(CopyFixture("1.1.0")).Load();
        AssertNewFieldsAreDefaults(doc);
        // Le scorciatoie nuove hanno i predefiniti anche se il file ne personalizzava altre.
        Assert.Equal("Ctrl+Alt+G", doc.Settings.GetShortcuts()[ShortcutAction.Overview].ToString());
    }

    [Fact]
    public void Load_Version10File_UsesDefaultsForNewFields() =>
        AssertNewFieldsAreDefaults(new ConnectionStore(CopyFixture("1.0.0")).Load());

    private static void AssertNewFieldsAreDefaults(ConnectionsDocument doc)
    {
        Assert.Empty(doc.CredentialProfiles);
        Assert.Empty(doc.QuickTexts);
        foreach (var c in doc.Connections)
        {
            Assert.Null(c.CredentialProfileId);
            Assert.False(c.UseGateway);
            Assert.Equal("", c.GatewayHost);
            Assert.Equal(GatewayCredentialsSource.SameAsConnection, c.GatewayCredentials);
            Assert.Null(c.GatewayProfileId);
            // Senza profilo le credenziali effettive restano quelle della connessione.
            Assert.Equal(c.Username, doc.ResolveCredentials(c).Username);
        }
        var s = doc.Settings;
        Assert.Equal(AppTheme.System, s.Theme);
        Assert.True(s.ShowTrayIcon);
        Assert.False(s.CloseToTray);
        Assert.Equal(GlobalHotkey.DefaultText, s.GlobalHotkey);
        Assert.True(s.HistoryEnabled);
        Assert.Equal(SplitLayout.DefaultRatio, s.SplitRatio);
    }

    [Fact]
    public void Version11File_SavedAgain_LosesNothing()
    {
        var store = new ConnectionStore(CopyFixture("1.1.0"));
        store.Save(store.Load());

        var reloaded = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, reloaded.Version);
        AssertSameAs11(reloaded);
    }

    [Fact]
    public void Version11File_ExportedToRdpm_KeepsFieldsExceptUnreadablePasswords()
    {
        var doc = new ConnectionStore(CopyFixture("1.1.0")).Load();
        var imported = RdpmFile.Import(RdpmFile.Export(doc));
        Assert.Equal(["Clienti", "Clienti/Esempio Srl", "Laboratorio"], imported.Groups);
        var a = imported.Find(IdA)!;
        Assert.Equal(("PROD", "#2E86DE", true), (a.Label, a.Color, a.UseAllMonitors));
        Assert.Null(a.ProtectedPassword); // la DPAPI non si esporta mai
    }
}
