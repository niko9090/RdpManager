using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Prove della copia di riserva dell'aggiornamento (k-90): «Access to the path 'Accessibility.dll' is denied» arrivava dalla
/// vecchia cartella di riserva in uso (non cancellabile) e bloccava tutto. Ora si ferma la VPN prima, si ritenta e, se serve,
/// si usa una cartella nuova.
/// </summary>
public sealed class UpdateBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-upd-" + Guid.NewGuid().ToString("N"));

    public UpdateBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string MakeInstall()
    {
        var install = Path.Combine(_dir, "installata");
        Directory.CreateDirectory(Path.Combine(install, "runtimes"));
        File.WriteAllText(Path.Combine(install, UpdateJob.ExeName), "MZ app");
        File.WriteAllText(Path.Combine(install, "Accessibility.dll"), "dll");
        File.WriteAllText(Path.Combine(install, "runtimes", "x.dll"), "x");
        return install;
    }

    [Fact]
    public void Update_Backup_StopsTheVpnServiceBeforeCopyingAnything()
    {
        var install = MakeInstall();
        var backup = Path.Combine(_dir, "riserva");
        bool backupExistedWhenStopped = true;
        var used = FolderBackup.Prepare(install, backup, log =>
        {
            backupExistedWhenStopped = Directory.Exists(backup);
            log("stop");
            return true;
        }, _ => { });
        Assert.False(backupExistedWhenStopped);
        Assert.Equal(backup, used);
        Assert.True(File.Exists(Path.Combine(backup, "Accessibility.dll")));
        Assert.True(File.Exists(Path.Combine(backup, "runtimes", "x.dll")));
    }

    [Fact]
    public void Update_Backup_VpnThatDoesNotStopIsLoggedAndTheBackupStillRuns()
    {
        var install = MakeInstall();
        var log = new List<string>();
        var used = FolderBackup.Prepare(install, Path.Combine(_dir, "riserva"), _ => false, log.Add);
        Assert.True(File.Exists(Path.Combine(used, UpdateJob.ExeName)));
        Assert.Contains(log, l => l.Contains("VPN") && l.Contains("comunque"));
    }

    [Fact]
    public void Update_Backup_ReadOnlyFilesInTheOldBackupDoNotBlock()
    {
        var install = MakeInstall();
        var backup = Path.Combine(_dir, "riserva");
        FolderBackup.Prepare(install, backup, null, _ => { });
        File.SetAttributes(Path.Combine(backup, "Accessibility.dll"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(install, "Accessibility.dll"), "nuova");
        var used = FolderBackup.Prepare(install, backup, null, _ => { });
        Assert.Equal(backup, used);
        Assert.Equal("nuova", File.ReadAllText(Path.Combine(backup, "Accessibility.dll")));
    }

    [Fact]
    public void Update_Backup_OldBackupInUse_FallsBackToANewFolderInsteadOfFailing()
    {
        if (!OperatingSystem.IsWindows())
            return; // su Linux un file aperto si cancella lo stesso
        var install = MakeInstall();
        var backup = Path.Combine(_dir, "riserva");
        FolderBackup.Prepare(install, backup, null, _ => { });
        using var locked = new FileStream(Path.Combine(backup, "Accessibility.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var log = new List<string>();
        var sleeps = 0;
        var used = FolderBackup.Prepare(install, backup, null, log.Add, attempts: 2, sleep: _ => sleeps++, stamp: "t1");
        Assert.Equal(backup + "-t1", used);
        Assert.Equal(1, sleeps);
        Assert.True(File.Exists(Path.Combine(used, "Accessibility.dll")));
        Assert.Contains(log, l => l.Contains("non è utilizzabile"));
    }

    [Fact]
    public void Update_Backup_RunningFromTheBackupFolder_UsesANewFolderAndNeverDeletesTheSource()
    {
        var backup = Path.Combine(_dir, "riserva");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, UpdateJob.ExeName), "MZ app");
        File.WriteAllText(Path.Combine(backup, "Accessibility.dll"), "dll");
        var used = FolderBackup.Prepare(backup, backup, null, _ => { }, stamp: "t2");
        Assert.Equal(backup + "-t2", used);
        Assert.True(File.Exists(Path.Combine(backup, UpdateJob.ExeName)));
        Assert.True(File.Exists(Path.Combine(used, UpdateJob.ExeName)));
    }

    [Fact]
    public void Update_Backup_SourceFileInUseByAnotherProcess_IsStillCopied()
    {
        var install = MakeInstall();
        using var inUse = new FileStream(Path.Combine(install, "Accessibility.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var used = FolderBackup.Prepare(install, Path.Combine(_dir, "riserva"), null, _ => { });
        Assert.Equal("dll", File.ReadAllText(Path.Combine(used, "Accessibility.dll")));
    }

    [Fact]
    public void Update_Backup_OldAlternativeFoldersAreCleanedUp()
    {
        var install = MakeInstall();
        var old = Path.Combine(_dir, "riserva-vecchia");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.dll"), "a");
        var used = FolderBackup.Prepare(install, Path.Combine(_dir, "riserva"), null, _ => { });
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(used));
    }

    [Fact]
    public void Update_Backup_MissingSourceStillFailsClearly()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FolderBackup.Prepare(Path.Combine(_dir, "nulla"), Path.Combine(_dir, "riserva"), null, _ => { }));
    }
}
