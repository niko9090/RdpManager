using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Core 1.3: errori di connessione spiegati, log a rotazione, impostazioni nei .rdpm, ordine manuale,
/// aggiornamento automatico con conferma, compatibilità con i file 1.0, 1.1 e 1.2.</summary>
public sealed class V13CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerV13-" + Guid.NewGuid().ToString("N"));

    public V13CoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---------- errori di connessione ----------

    [Theory]
    [InlineData(260, ConnectionErrorKind.Network, "DNS")]
    [InlineData(516, ConnectionErrorKind.Network, "3389")]
    [InlineData(2055, ConnectionErrorKind.Credentials, "password")]
    [InlineData(3335, ConnectionErrorKind.Credentials, "bloccato")]
    [InlineData(263, ConnectionErrorKind.Security, "certificato")]
    [InlineData(1, ConnectionErrorKind.UserInitiated, "Riconnetti")]
    public void ConnectionErrors_GiveCauseAndSolution(int code, ConnectionErrorKind kind, string expectedWord)
    {
        var info = ConnectionErrors.Explain(code);
        Assert.Equal(kind, info.Kind);
        Assert.Equal(DisconnectReasons.Describe(code), info.Title);
        Assert.False(string.IsNullOrWhiteSpace(info.Cause));
        Assert.False(string.IsNullOrWhiteSpace(info.Solution));
        Assert.Contains(expectedWord, info.Cause + " " + info.Solution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectionErrors_ExtendedCodeExplainsServerDisconnects_UnknownCodesStillHelp()
    {
        var replaced = ConnectionErrors.Explain(3, extended: 5);
        Assert.Contains("stesso utente", replaced.Cause);
        var idle = ConnectionErrors.Explain(2308, extended: 3);
        Assert.Contains("inattiva", idle.Cause);
        // Un codice esteso non prevale su un errore del client già chiaro.
        Assert.Equal(ConnectionErrorKind.Credentials, ConnectionErrors.Explain(2055, extended: 3).Kind);

        var unknown = ConnectionErrors.Explain(99999);
        Assert.Equal(ConnectionErrorKind.Unknown, unknown.Kind);
        Assert.Contains("99999", unknown.Cause);
        Assert.Contains("mstsc", unknown.Solution);
        Assert.Contains("Causa probabile:", unknown.ToMessage());
        Assert.DoesNotContain("Causa probabile:", ConnectionErrors.Explain(1).ToMessage());
    }

    [Fact]
    public void ConnectionErrors_MentionTheGatewayForNetworkErrorsOnly()
    {
        Assert.Contains("Gateway", ConnectionErrors.Explain(516, usesGateway: true).Solution);
        Assert.DoesNotContain("Gateway", ConnectionErrors.Explain(516).Solution);
        Assert.DoesNotContain("Gateway", ConnectionErrors.Explain(2055, usesGateway: true).Solution);
        Assert.True(ConnectionErrors.NeedsUserAction(2055));
        Assert.True(ConnectionErrors.NeedsUserAction(263));
        Assert.False(ConnectionErrors.NeedsUserAction(2308));
    }

    // ---------- log ----------

    [Fact]
    public void Log_InfoHasNeitherPasswordsNorHosts_DebugHasHostsOnlyWhenVerbose()
    {
        var log = new AppLog(_dir);
        var c = new RdpConnection { Name = "Server segreto", Host = "srv-riservato.example.test", Username = "mario", Password = "Pwd-Segreta-1!" };
        log.Connection(LogLevel.Info, "Connessione aperta", c);
        log.Info($"Prova password=Pwd-Segreta-1! e pwd: \"altra segreta\"");
        log.Error("Errore di prova", new InvalidOperationException("impossibile raggiungere srv-riservato.example.test"));

        var text = File.ReadAllText(log.CurrentPath);
        Assert.Contains(AppLog.Ref(c), text);
        Assert.DoesNotContain("Pwd-Segreta", text);
        Assert.DoesNotContain("altra segreta", text);
        Assert.DoesNotContain("srv-riservato", text);
        Assert.DoesNotContain("mario", text);
        Assert.DoesNotContain("Server segreto", text);
        Assert.Contains("InvalidOperationException", text);

        log.Verbose = true;
        log.Connection(LogLevel.Info, "Connessione aperta", c);
        text = File.ReadAllText(log.CurrentPath);
        Assert.Contains("[DEBUG] Connessione aperta " + AppLog.Ref(c) + " = srv-riservato.example.test", text);
        Assert.DoesNotContain("Pwd-Segreta", text);
    }

    [Fact]
    public void Log_RotatesKeepingAtMostMaxFiles()
    {
        var log = new AppLog(_dir, maxBytes: 2048, maxFiles: 3);
        for (int i = 0; i < 200; i++)
            log.Info($"riga {i:000} " + new string('x', 60));
        var files = log.Files();
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2048 + 200, f));
        Assert.Contains("riga 199", File.ReadAllText(files[0]));
        Assert.DoesNotContain("riga 000", string.Concat(files.Select(File.ReadAllText)));
        Assert.Equal(3, Directory.GetFiles(_dir, "rdpmanager*.log").Length);
    }

    [Fact]
    public void Log_NeverThrows_WhenTheFolderCannotBeWritten()
    {
        var blocker = Path.Combine(_dir, "file-al-posto-della-cartella");
        File.WriteAllText(blocker, "");
        var log = new AppLog(blocker);
        log.Info("niente eccezioni");
        Assert.Empty(log.Files());
    }

    // ---------- impostazioni nei .rdpm ----------

    [Fact]
    public void Rdpm_CanCarryAppSettings_WithoutThisPcOnlyParts()
    {
        var c = new RdpConnection { Host = "pc" };
        var source = new ConnectionsDocument { Connections = [c] };
        source.Settings.Theme = AppTheme.Dark;
        source.Settings.UpdateMode = UpdateMode.NotifyOnly;
        source.Settings.VerboseLog = true;
        source.Settings.PreferredMonitor = @"\\.\DISPLAY3";
        source.Settings.OpenSessions = [c.Id];
        var map = source.Settings.GetShortcuts();
        map[ShortcutAction.Overview] = KeyCombo.Parse("Ctrl+Alt+O");
        source.Settings.SetShortcuts(map);

        var without = RdpmFile.Import(RdpmFile.Export(source));
        Assert.False(without.SettingsIncluded);

        var json = RdpmFile.Export(source, includeSettings: true);
        Assert.DoesNotContain("DISPLAY3", json);
        var imported = RdpmFile.Import(json);
        Assert.True(imported.SettingsIncluded);

        var target = new ConnectionsDocument();
        target.Settings.PreferredMonitor = @"\\.\DISPLAY1";
        var keep = RdpmFile.ApplyImport(target, imported, ImportMode.Merge);
        Assert.False(keep.SettingsApplied);
        Assert.Equal(AppTheme.System, target.Settings.Theme);

        var target2 = new ConnectionsDocument();
        target2.Settings.PreferredMonitor = @"\\.\DISPLAY1";
        var applied = RdpmFile.ApplyImport(target2, RdpmFile.Import(json), ImportMode.Merge, applySettings: true);
        Assert.True(applied.SettingsApplied);
        Assert.Equal(AppTheme.Dark, target2.Settings.Theme);
        Assert.Equal(UpdateMode.NotifyOnly, target2.Settings.UpdateMode);
        Assert.True(target2.Settings.VerboseLog);
        Assert.Equal("Ctrl+Alt+O", target2.Settings.GetShortcuts()[ShortcutAction.Overview].ToString());
        Assert.Equal(@"\\.\DISPLAY1", target2.Settings.PreferredMonitor); // vale per questo PC
        Assert.Empty(target2.Settings.OpenSessions);
    }

    [Fact]
    public void Rdpm_SettingsTravelEncryptedFiles_AndNoPasswordsAppear()
    {
        var source = new ConnectionsDocument { Connections = [new RdpConnection { Host = "pc", Password = "Segreta-123!" }] };
        source.Settings.Theme = AppTheme.Light;
        var json = RdpmFile.Export(source, "password-del-file", 1000, includeSettings: true);
        Assert.DoesNotContain("Segreta-123!", json);
        var imported = RdpmFile.Import(json, "password-del-file");
        Assert.True(imported.SettingsIncluded);
        Assert.Equal(AppTheme.Light, imported.Settings.Theme);
    }

    // ---------- ordine manuale ----------

    [Fact]
    public void Reorder_ByDragging_SwitchesToManualOrderStartingFromWhatWasVisible()
    {
        var b = new RdpConnection { Name = "Bravo", Host = "b", Group = "G" };
        var a = new RdpConnection { Name = "Alfa", Host = "a", Group = "G" };
        var c = new RdpConnection { Name = "Charlie", Host = "c", Group = "G" };
        var x = new RdpConnection { Name = "Xray", Host = "x", Group = "Altra" };
        var doc = new ConnectionsDocument { Connections = [b, x, a, c] };
        Assert.Equal([a, b, c], doc.InDisplayOrder(doc.ConnectionsInFolder("G")));

        Assert.True(doc.MoveConnection(c.Id, a.Id, "G")); // Charlie prima di Alfa
        Assert.Equal(ConnectionOrder.Manual, doc.Settings.ConnectionOrder);
        Assert.Equal([c, a, b], doc.InDisplayOrder(doc.ConnectionsInFolder("G")));

        Assert.True(doc.MoveConnection(a.Id, null, "Altra")); // in fondo a un'altra cartella
        Assert.Equal("Altra", a.Group);
        Assert.Equal([x, a], doc.InDisplayOrder(doc.ConnectionsInFolder("Altra")));
        Assert.False(doc.MoveConnection(a.Id, a.Id, "Altra"));
        Assert.False(doc.MoveConnection(Guid.NewGuid(), null, ""));

        doc.SortAlphabetically();
        Assert.Equal(ConnectionOrder.Alphabetical, doc.Settings.ConnectionOrder);
        Assert.Equal([a, b, c, x], doc.Connections);
    }

    [Fact]
    public void Reorder_ManualOrderSurvivesSaveAndLoad()
    {
        var a = new RdpConnection { Name = "Alfa", Host = "a" };
        var b = new RdpConnection { Name = "Bravo", Host = "b" };
        var doc = new ConnectionsDocument { Connections = [a, b] };
        doc.MoveConnection(b.Id, a.Id, "");
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);
        var loaded = store.Load();
        Assert.Equal(ConnectionOrder.Manual, loaded.Settings.ConnectionOrder);
        Assert.Equal(["Bravo", "Alfa"], loaded.InDisplayOrder(loaded.Connections).Select(c => c.Name));
    }

    // ---------- aggiornamento automatico con conferma ----------

    private static ReleaseInfo Release(string v) =>
        new($"v{v}", UpdateChecker.ParseVersion(v)!, $"https://github.com/niko9090/RdpManager/releases/tag/v{v}", null);

    private (string Path, string Hash) FakeInstaller(string content = "MZ installer finto")
    {
        var path = System.IO.Path.Combine(_dir, $"RdpManager-Setup-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return (path, Sha256Sums.ComputeHex(path));
    }

    [Fact]
    public void AutoUpdate_ChecksAtStartupThenEvery24Hours()
    {
        var p = new AutoUpdatePlanner();
        var t0 = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(p.IsCheckDue(t0));
        p.MarkChecked(t0);
        Assert.False(p.IsCheckDue(t0.AddHours(23)));
        Assert.True(p.IsCheckDue(t0.AddHours(24)));
    }

    [Fact]
    public void AutoUpdate_DownloadsOnlyNewerVersions()
    {
        var p = new AutoUpdatePlanner();
        var current = new Version(1, 3, 0);
        Assert.False(p.ShouldDownload(null, current));
        Assert.False(p.ShouldDownload(Release("1.3.0"), current));
        Assert.False(p.ShouldDownload(Release("1.2.9"), current));
        Assert.True(p.ShouldDownload(Release("1.3.1"), current));

        var (path, hash) = FakeInstaller();
        Assert.True(p.Accept(Release("1.3.1"), new UpdateDownloadResult(UpdateDownloadStatus.Verified, path, "ok", hash)));
        Assert.False(p.ShouldDownload(Release("1.3.1"), current)); // già pronto
        Assert.True(p.ShouldDownload(Release("1.4.0"), current));  // ne è uscito un altro
    }

    [Fact]
    public void AutoUpdate_AnUnverifiedFileIsNeverRun()
    {
        var p = new AutoUpdatePlanner();
        var (path, _) = FakeInstaller();
        Assert.False(p.Accept(Release("2.0.0"), new UpdateDownloadResult(UpdateDownloadStatus.HashMismatch, null, "no")));
        Assert.False(p.Accept(Release("2.0.0"), new UpdateDownloadResult(UpdateDownloadStatus.Verified, path, "senza hash")));
        Assert.Null(p.Ready);
        Assert.Null(p.VerifiedInstallerToRun());
        Assert.False(p.ShouldOffer(DateTime.UtcNow));

        // Verificato, poi cambiato sul disco prima dell'avvio: non si esegue e viene cancellato.
        var (good, hash) = FakeInstaller("installer buono");
        Assert.True(p.Accept(Release("2.0.0"), new UpdateDownloadResult(UpdateDownloadStatus.Verified, good, "ok", hash)));
        Assert.Equal(good, p.VerifiedInstallerToRun());
        File.AppendAllText(good, "manomesso");
        Assert.Null(p.VerifiedInstallerToRun());
        Assert.False(File.Exists(good));
        Assert.Null(p.Ready);
    }

    [Fact]
    public void AutoUpdate_LaterPostponesFor24Hours_OnExitStopsTheOffer()
    {
        var p = new AutoUpdatePlanner();
        var t0 = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
        var (path, hash) = FakeInstaller();
        p.Accept(Release("1.4.0"), new UpdateDownloadResult(UpdateDownloadStatus.Verified, path, "ok", hash));
        Assert.True(p.ShouldOffer(t0));

        p.Choose(UpdateChoice.Later, t0);
        Assert.False(p.ShouldOffer(t0.AddHours(23)));
        Assert.True(p.ShouldOffer(t0.AddHours(24)));
        Assert.True(new AutoUpdatePlanner().IsCheckDue(t0)); // al riavvio dell'app si ricomincia

        p.Choose(UpdateChoice.OnExit, t0.AddHours(24));
        Assert.True(p.InstallOnExit);
        Assert.False(p.ShouldOffer(t0.AddHours(30)));

        // Una versione ancora più nuova pronta: l'avviso torna (il rinvio valeva per la versione precedente).
        var (path2, hash2) = FakeInstaller("altro");
        p.Accept(Release("1.5.0"), new UpdateDownloadResult(UpdateDownloadStatus.Verified, path2, "ok", hash2));
        Assert.True(p.ShouldOffer(t0.AddHours(25)));
    }

    [Fact]
    public void AutoUpdate_PortableExeIsDetected_AndSilentArgumentsAreRight()
    {
        var installed = Path.Combine(_dir, "Programs", "RdpManager");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "RdpManager.exe"), "");
        Assert.True(InstallLocation.IsPortable(Path.Combine(installed, "RdpManager.exe")));
        File.WriteAllText(Path.Combine(installed, InstallLocation.UninstallerName), "");
        Assert.False(InstallLocation.IsPortable(Path.Combine(installed, "RdpManager.exe")));

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1", InstallLocation.SilentArguments(relaunch: true));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER", InstallLocation.SilentArguments(relaunch: false));
    }

    [Fact]
    public void AutoUpdate_CleanFolderRemovesEverythingButTheVerifiedFile()
    {
        var keep = Path.Combine(_dir, "RdpManager-Setup-9.9.9.exe");
        File.WriteAllText(keep, "buono");
        File.WriteAllText(Path.Combine(_dir, "vecchio.exe"), "x");
        File.WriteAllText(Path.Combine(_dir, "interrotto.tmp"), "x");
        Assert.Equal(2, UpdateDownloader.CleanFolder(_dir, keep));
        Assert.Equal([keep], Directory.GetFiles(_dir));
        Assert.Equal(0, UpdateDownloader.CleanFolder(Path.Combine(_dir, "manca")));
    }

    [Fact]
    public void ReleaseNotesAreReadFromTheApi()
    {
        var r = UpdateChecker.ParseLatestRelease("""
            { "tag_name": "v1.3.0", "html_url": "https://github.com/niko9090/RdpManager/releases/tag/v1.3.0", "body": "## Novità\n- prova" }
            """)!;
        Assert.Contains("prova", r.Notes);
    }

    // ---------- compatibilità ----------

    private string Fixture(string version)
    {
        var path = Path.Combine(_dir, $"connections-{version}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"connections-{version}.json"), path);
        return path;
    }

    [Fact]
    public void Version12File_LoadsAndResavesWithoutLosses()
    {
        var store = new ConnectionStore(Fixture("1.2.0"));
        void Check(ConnectionsDocument d)
        {
            Assert.Equal(["Sedi", "Sedi/Nord"], d.Groups);
            var gw = d.Connections[0];
            Assert.Equal(("Server con gateway", true, "gw.example.test:8443", GatewayCredentialsSource.Profile, "GW"),
                (gw.Name, gw.UseGateway, gw.GatewayHost, gw.GatewayCredentials, gw.Label));
            Assert.Equal(Guid.Parse("9c3d1e22-0000-4000-8000-0000000000a1"), gw.CredentialProfileId);
            Assert.Equal(Guid.Parse("9c3d1e22-0000-4000-8000-0000000000a2"), gw.GatewayProfileId);
            Assert.Equal(4, gw.AutoReconnectMaxAttempts);
            var pc = d.Connections[1];
            Assert.Equal(("192.0.2.30", 3390, "AQIDBAUGBwgJCgsMDQ4PEA==", AudioMode.None, true), (pc.Host, pc.Port, pc.ProtectedPassword, pc.AudioMode, pc.UseAllMonitors));
            Assert.Equal(2, d.CredentialProfiles.Count);
            Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEQ==", d.CredentialProfiles[0].ProtectedPassword); // DPAPI di un altro utente: conservata
            Assert.Equal("Configurazione IP", Assert.Single(d.QuickTexts).Name);
            var s = d.Settings;
            Assert.Equal((AppTheme.Dark, true, "Ctrl+Alt+Maiusc+F12", false, 0.35), (s.Theme, s.CloseToTray, s.GlobalHotkey, s.HistoryEnabled, s.SplitRatio));
            Assert.Equal("Ctrl+Alt+O", s.GetShortcuts()[ShortcutAction.Overview].ToString());
            // Campi nuovi della 1.3: valori predefiniti.
            Assert.Equal(UpdateMode.AutomaticWithConfirmation, s.UpdateMode);
            Assert.False(s.VerboseLog);
            Assert.Equal(ConnectionOrder.Alphabetical, s.ConnectionOrder);
        }
        var doc = store.Load();
        Assert.Equal(3, doc.Version);
        Check(doc);
        store.Save(doc);
        var again = store.Load();
        Assert.Equal(ConnectionsDocument.CurrentVersion, again.Version);
        Check(again);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    public void OlderFiles_GetThe13Defaults(string version)
    {
        var doc = new ConnectionStore(Fixture(version)).Load();
        Assert.Equal(2, doc.Connections.Count);
        Assert.False(doc.Settings.VerboseLog);
        Assert.Equal(UpdateMode.AutomaticWithConfirmation, doc.Settings.UpdateMode);
        Assert.Equal(ConnectionOrder.Alphabetical, doc.Settings.ConnectionOrder);
    }
}
