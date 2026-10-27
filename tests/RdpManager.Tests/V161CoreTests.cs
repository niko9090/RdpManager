using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Prove della 1.6.1: programma di aggiornamento separato (attesa della chiusura, installer verificato, log,
/// verifica dell'esito e della versione, riapertura, ritorno alla versione precedente) e copia di riserva.
/// Nessun installer vero: le operazioni di sistema sono finte, le cartelle temporanee.
/// </summary>
public sealed class V161CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManager161-" + Guid.NewGuid().ToString("N"));

    public V161CoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeEnv : IUpdateEnvironment
    {
        public bool AppExits = true;
        public bool InstallerExists = true;
        public bool HashOk = true;
        public int? InstallerExit = 0;
        public Version? InstalledAfter = new(1, 7, 0);
        public Version? BackupVersion = new(1, 6, 1);
        public bool BackupExists = true;
        public bool RestoreThrows;
        public readonly List<string> Steps = [];
        public readonly List<(string Exe, IReadOnlyList<string> Args)> Launched = [];
        private bool _installed;

        public bool WaitForExit(int processId, TimeSpan timeout) { Steps.Add("wait"); return AppExits; }
        public bool FileExists(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && path.Contains("riserva")
            ? BackupExists : InstallerExists;
        public bool VerifySha256(string path, string sha256) { Steps.Add("verify"); return HashOk; }
        public int? RunInstaller(string path, string arguments, TimeSpan timeout)
        {
            Steps.Add("install " + arguments);
            _installed = true;
            return InstallerExit;
        }
        public Version? FileVersion(string exePath) => exePath.Contains("riserva") ? BackupVersion : _installed ? InstalledAfter : BackupVersion;
        public void Restore(string backup, string target)
        {
            Steps.Add("restore");
            if (RestoreThrows)
                throw new IOException("file in uso");
            _installed = false;
        }
        public void Launch(string exePath, IReadOnlyList<string> arguments) { Steps.Add("launch"); Launched.Add((exePath, arguments)); }
    }

    private UpdateJob Job(bool simulate = false, bool relaunch = true, string? data = null) => new(
        4242, Path.Combine(_dir, "updates", "RdpManager-Setup-1.7.0.exe"), new string('a', 64),
        Path.Combine(_dir, "Programs", "RdpManager"), Path.Combine(_dir, "update-runner", "riserva"),
        new Version(1, 7, 0), new Version(1, 6, 1), Path.Combine(_dir, "aggiornamento.log"), Path.Combine(_dir, "setup.log"),
        data, simulate, relaunch);

    private static UpdateRunResult Run(UpdateJob job, FakeEnv env, List<string>? log = null) =>
        UpdateRunner.Run(job, env, l => log?.Add(l));

    [Fact]
    public void Success_WaitsVerifiesInstallsSilentlyWithLogAndReopensTheNewVersion()
    {
        var env = new FakeEnv();
        var log = new List<string>();
        var job = Job(data: @"C:\dati\connections.json");
        var r = Run(job, env, log);

        Assert.True(r.Succeeded);
        Assert.False(r.RolledBack);
        Assert.Equal("wait", env.Steps[0]); // prima di tutto si aspetta la chiusura dell'app
        Assert.Equal("verify", env.Steps[1]); // poi si ricontrolla l'installer
        var install = Assert.Single(env.Steps, s => s.StartsWith("install"));
        Assert.Contains("/VERYSILENT", install);
        Assert.Contains("/SUPPRESSMSGBOXES", install);
        Assert.Contains($"/LOG=\"{job.SetupLogPath}\"", install);
        Assert.DoesNotContain("/RELAUNCH", install);
        Assert.DoesNotContain("restore", env.Steps);
        var (exe, args) = Assert.Single(env.Launched);
        Assert.Equal(Path.Combine(job.InstallDir, "RdpManager.exe"), exe);
        Assert.Equal(["--dati", @"C:\dati\connections.json"], args); // stesso elenco, nessun messaggio d'errore
        Assert.Contains(log, l => l.Contains("1.7.0") && l.Contains("verificata"));
    }

    [Fact]
    public void AppThatDoesNotClose_NothingIsInstalledOrLaunched()
    {
        var env = new FakeEnv { AppExits = false };
        var r = Run(Job(), env);
        Assert.Equal(UpdateRunOutcome.AppStillRunning, r.Outcome);
        Assert.Equal(new[] { "wait" }, env.Steps);
        Assert.Empty(env.Launched);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnverifiedOrMissingInstaller_IsNeverExecuted_AndThePreviousVersionReopensWithAMessage(bool exists, bool hashOk)
    {
        var env = new FakeEnv { InstallerExists = exists, HashOk = hashOk };
        var r = Run(Job(), env);
        Assert.Equal(UpdateRunOutcome.NotVerified, r.Outcome);
        Assert.DoesNotContain(env.Steps, s => s.StartsWith("install"));
        Assert.DoesNotContain("restore", env.Steps); // nulla da rimettere: non si è toccato niente
        var (_, args) = Assert.Single(env.Launched);
        Assert.Equal(UpdateJob.FailedArgument, args[^2]);
        Assert.Contains("SHA256", args[^1]);
    }

    [Fact]
    public void IncompleteBackup_DoesNotInstallWithoutAWayBack()
    {
        var env = new FakeEnv { BackupExists = false };
        var r = Run(Job(), env);
        Assert.Equal(UpdateRunOutcome.NoBackup, r.Outcome);
        Assert.DoesNotContain(env.Steps, s => s.StartsWith("install"));
        Assert.Single(env.Launched);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(null)] // non finito in tempo
    public void InstallerFailure_RestoresThePreviousVersionAndReopensItWithTheReason(int? exitCode)
    {
        var env = new FakeEnv { InstallerExit = exitCode };
        var job = Job();
        var r = Run(job, env);
        Assert.Equal(UpdateRunOutcome.InstallerFailed, r.Outcome);
        Assert.True(r.RolledBack);
        Assert.Equal("restore", env.Steps[^2]);
        Assert.Equal("launch", env.Steps[^1]);
        var (exe, args) = Assert.Single(env.Launched);
        Assert.Equal(Path.Combine(job.InstallDir, "RdpManager.exe"), exe);
        Assert.Equal(UpdateJob.FailedArgument, args[0]);
        Assert.Contains("versione precedente (1.6.1)", args[1]);
        Assert.Contains(job.LogPath, args[1]);
        if (exitCode is int c)
            Assert.Contains($"codice {c}", args[1]);
    }

    [Theory]
    [InlineData("1.6.1")]
    [InlineData(null)]
    public void WrongVersionAfterInstall_RollsBack(string? installed)
    {
        var env = new FakeEnv { InstalledAfter = installed is null ? null : Version.Parse(installed) };
        var r = Run(Job(), env);
        Assert.Equal(UpdateRunOutcome.WrongVersion, r.Outcome);
        Assert.True(r.RolledBack);
        Assert.Contains("restore", env.Steps);
    }

    [Fact]
    public void SimulatedFailure_InstallsThenRollsBack_ForTheRealTest()
    {
        var env = new FakeEnv();
        var log = new List<string>();
        var r = Run(Job(simulate: true), env, log);
        Assert.Equal(UpdateRunOutcome.WrongVersion, r.Outcome);
        Assert.Contains(env.Steps, s => s.StartsWith("install"));
        Assert.True(r.RolledBack);
        Assert.Contains(log, l => l.Contains("simulato"));
    }

    [Fact]
    public void FailedRestore_ReopensTheBackupCopyItself()
    {
        var env = new FakeEnv { InstallerExit = 2, RestoreThrows = true };
        var job = Job();
        var r = Run(job, env);
        Assert.False(r.RolledBack);
        Assert.Equal(Path.Combine(job.BackupDir, "RdpManager.exe"), r.Relaunched);
    }

    [Fact]
    public void WithoutRelaunch_SuccessOpensNothing_ButFailureStillWarns()
    {
        var ok = new FakeEnv();
        Assert.True(Run(Job(relaunch: false), ok).Succeeded);
        Assert.Empty(ok.Launched);

        var ko = new FakeEnv { InstallerExit = 3 };
        Run(Job(relaunch: false), ko);
        Assert.Single(ko.Launched);
    }

    [Fact]
    public void Arguments_RoundTrip_AndInvalidOnesAreRejected()
    {
        var job = Job(simulate: true, relaunch: false, data: @"C:\dati con spazi\c.json");
        var parsed = UpdateJob.TryParse(job.ToArguments());
        Assert.Equal(job, parsed);
        Assert.True(UpdateJob.IsUpdateMode(job.ToArguments()));

        var plain = UpdateJob.TryParse(Job().ToArguments());
        Assert.NotNull(plain);
        Assert.True(plain.Relaunch);
        Assert.False(plain.SimulateFailure);
        Assert.Null(plain.DataFile);

        Assert.Null(UpdateJob.TryParse(["--dati", "x"]));
        Assert.Null(UpdateJob.TryParse(["--aggiorna"]));
        var args = Job().ToArguments().ToList();
        args[args.IndexOf("--pid") + 1] = "abc";
        Assert.Null(UpdateJob.TryParse(args));
        args = Job().ToArguments().ToList();
        args[args.IndexOf("--installer") + 1] = "relativo.exe"; // solo percorsi completi
        Assert.Null(UpdateJob.TryParse(args));
    }

    [Fact]
    public void FolderBackup_CopyAndRestore_PutBackExactlyThePreviousFiles()
    {
        var install = Path.Combine(_dir, "inst");
        var backup = Path.Combine(_dir, "riserva");
        Directory.CreateDirectory(Path.Combine(install, "sub"));
        File.WriteAllText(Path.Combine(install, "RdpManager.exe"), "vecchio exe");
        File.WriteAllText(Path.Combine(install, "sub", "a.dll"), "vecchia dll");
        File.WriteAllText(Path.Combine(install, "unins000.dat"), "dati disinstallazione");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "residuo.txt"), "da togliere");

        FolderBackup.Copy(install, backup);
        Assert.False(File.Exists(Path.Combine(backup, "residuo.txt")));
        Assert.Equal("vecchia dll", File.ReadAllText(Path.Combine(backup, "sub", "a.dll")));

        // Installazione a metà: file cambiati, file nuovi, una cartella nuova, un file in sola lettura.
        File.WriteAllText(Path.Combine(install, "RdpManager.exe"), "nuovo exe");
        File.WriteAllText(Path.Combine(install, "nuovo.dll"), "nuovo");
        Directory.CreateDirectory(Path.Combine(install, "nuova"));
        File.WriteAllText(Path.Combine(install, "nuova", "b.dll"), "nuovo");
        File.SetAttributes(Path.Combine(install, "sub", "a.dll"), FileAttributes.ReadOnly);

        FolderBackup.Restore(backup, install);
        Assert.Equal("vecchio exe", File.ReadAllText(Path.Combine(install, "RdpManager.exe")));
        Assert.Equal("vecchia dll", File.ReadAllText(Path.Combine(install, "sub", "a.dll")));
        Assert.Equal("dati disinstallazione", File.ReadAllText(Path.Combine(install, "unins000.dat")));
        Assert.False(File.Exists(Path.Combine(install, "nuovo.dll")));
        Assert.False(Directory.Exists(Path.Combine(install, "nuova")));
        var names = Directory.GetFiles(install, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(install, f)).Order();
        Assert.Equal(Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(backup, f)).Order(), names);
    }

    [Fact]
    public void FolderBackup_RestoreRefusesAnIncompleteBackup()
    {
        var install = Path.Combine(_dir, "inst2");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "RdpManager.exe"), "exe");
        var empty = Path.Combine(_dir, "vuota");
        Directory.CreateDirectory(empty);
        Assert.Throws<IOException>(() => FolderBackup.Restore(empty, install));
        Assert.True(File.Exists(Path.Combine(install, "RdpManager.exe"))); // nulla cancellato
    }

    [Fact]
    public void RealEnvironment_ProcessAlreadyGoneCountsAsClosed_AndVersionOfAMissingFileIsNull()
    {
        var env = new SystemUpdateEnvironment();
        Assert.True(env.WaitForExit(int.MaxValue - 7, TimeSpan.FromMilliseconds(10)));
        Assert.Null(env.FileVersion(Path.Combine(_dir, "manca.exe")));
        var path = Path.Combine(_dir, "f.bin");
        File.WriteAllText(path, "contenuto");
        Assert.True(env.VerifySha256(path, Sha256Sums.ComputeHex(path)));
        Assert.False(env.VerifySha256(path, new string('0', 64)));
    }
}
