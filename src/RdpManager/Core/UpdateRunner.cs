using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Lavoro del programma di aggiornamento separato (dalla 1.6.1). L'app, prima di chiudersi per «Aggiorna ora», copia
/// la cartella installata in <see cref="BackupDir"/> e avvia la copia con <c>--aggiorna</c>: la copia aspetta che l'app
/// sia chiusa, ricontrolla lo SHA256, installa in silenzio con un log, verifica l'esito e la versione e riapre l'app
/// (che riapre le sessioni salvate). Se qualcosa va storto rimette la versione precedente dalla copia e la riapre con
/// un messaggio. La copia serve quindi sia da programma di aggiornamento sia da riserva.
/// </summary>
/// <param name="AppProcessId">Processo dell'app da aspettare.</param>
/// <param name="InstallerPath">Installer già verificato dall'app; si ricontrolla prima di avviarlo.</param>
/// <param name="Sha256">SHA256 verificato dell'installer (esadecimale).</param>
/// <param name="InstallDir">Cartella installata (quella che l'installer aggiorna).</param>
/// <param name="BackupDir">Copia completa della versione in uso (riserva e programma di aggiornamento).</param>
/// <param name="ExpectedVersion">Versione che deve risultare installata alla fine.</param>
/// <param name="PreviousVersion">Versione in uso prima dell'aggiornamento (per i messaggi).</param>
/// <param name="LogPath">Log del programma di aggiornamento.</param>
/// <param name="SetupLogPath">Log dell'installer (/LOG=).</param>
/// <param name="DataFile">File dati passato con --dati all'app (null = quello predefinito).</param>
/// <param name="SimulateFailure">Solo per le prove: finge che la verifica della versione fallisca dopo l'installazione.</param>
/// <param name="Relaunch">Riapre l'app dopo un aggiornamento riuscito (falso con «Alla chiusura»; se fallisce si riapre comunque per avvisare).</param>
public sealed record UpdateJob(
    int AppProcessId,
    string InstallerPath,
    string Sha256,
    string InstallDir,
    string BackupDir,
    Version ExpectedVersion,
    Version PreviousVersion,
    string LogPath,
    string SetupLogPath,
    string? DataFile = null,
    bool SimulateFailure = false,
    bool Relaunch = true)
{
    /// <summary>Con questa variabile a 1 l'app chiede al programma di aggiornamento di fallire apposta (prova del ritorno indietro).</summary>
    public const string SimulateFailureVariable = "RDPMANAGER_PROVA_AGGIORNAMENTO_FALLITO";

    public const string ModeArgument = "--aggiorna";
    public const string FailedArgument = "--aggiornamento-fallito";
    public const string ExeName = "RdpManager.exe";

    /// <summary>Cartelle di lavoro predefinite: %LOCALAPPDATA%\RdpManager\update-runner (fuori da «updates», che si ripulisce).</summary>
    public static string DefaultRunnerFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "update-runner");

    /// <summary>Argomenti per avviare la copia in modalità aggiornamento (da passare uno per uno, senza virgolette).</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string>
        {
            ModeArgument,
            "--pid", AppProcessId.ToString(CultureInfo.InvariantCulture),
            "--installer", InstallerPath,
            "--sha256", Sha256,
            "--cartella", InstallDir,
            "--riserva", BackupDir,
            "--versione", ExpectedVersion.ToString(3),
            "--precedente", PreviousVersion.ToString(3),
            "--log", LogPath,
            "--log-setup", SetupLogPath,
        };
        if (DataFile is not null)
            args.AddRange(["--dati", DataFile]);
        if (SimulateFailure)
            args.Add("--simula-errore");
        if (!Relaunch)
            args.Add("--senza-riapertura");
        return args;
    }

    /// <summary>Vero se gli argomenti chiedono la modalità aggiornamento (anche se poi non sono validi).</summary>
    public static bool IsUpdateMode(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], ModeArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>Legge gli argomenti scritti da <see cref="ToArguments"/>; null se mancano o non sono validi.</summary>
    public static UpdateJob? TryParse(IReadOnlyList<string> args)
    {
        if (!IsUpdateMode(args))
            return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool simulate = false, relaunch = true;
        for (int i = 1; i < args.Count; i++)
        {
            var key = args[i];
            if (key.Equals("--simula-errore", StringComparison.OrdinalIgnoreCase))
            {
                simulate = true;
                continue;
            }
            if (key.Equals("--senza-riapertura", StringComparison.OrdinalIgnoreCase))
            {
                relaunch = false;
                continue;
            }
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
                return null;
            map[key] = args[++i];
        }
        string? Get(string k) => map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (!int.TryParse(Get("--pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            return null;
        if (Get("--installer") is not { } installer || Get("--sha256") is not { } sha || Get("--cartella") is not { } dir
            || Get("--riserva") is not { } backup || Get("--log") is not { } log || Get("--log-setup") is not { } setupLog)
            return null;
        if (!Version.TryParse(Get("--versione"), out var expected) || !Version.TryParse(Get("--precedente"), out var previous))
            return null;
        if (!Path.IsPathFullyQualified(installer) || !Path.IsPathFullyQualified(dir) || !Path.IsPathFullyQualified(backup))
            return null;
        return new UpdateJob(pid, installer, sha.Trim(), dir, backup, AppInfo.Normalize(expected), AppInfo.Normalize(previous),
            log, setupLog, Get("--dati"), simulate, relaunch);
    }

    /// <summary>Argomenti dell'installer: silenzioso, con il log, senza riapertura (la riapertura la fa il programma di aggiornamento).</summary>
    public string InstallerArguments() => InstallLocation.SilentArguments(relaunch: false) + $" /LOG=\"{SetupLogPath}\"";
}

/// <summary>Esito del programma di aggiornamento.</summary>
public enum UpdateRunOutcome
{
    /// <summary>Installata e verificata: riaperta la nuova versione.</summary>
    Installed,
    /// <summary>L'app non si è chiusa in tempo: non si è installato nulla (l'app è ancora aperta).</summary>
    AppStillRunning,
    /// <summary>L'installer manca o non ha più lo SHA256 verificato: non eseguito, riaperta la versione di prima.</summary>
    NotVerified,
    /// <summary>La copia di riserva non è completa: non si installa senza una via di ritorno.</summary>
    NoBackup,
    /// <summary>L'installer è fallito o non ha finito in tempo: rimessa la versione di prima.</summary>
    InstallerFailed,
    /// <summary>Dopo l'installazione la versione non è quella attesa: rimessa la versione di prima.</summary>
    WrongVersion,
}

/// <param name="Relaunched">Percorso dell'exe riaperto (null se non si è riaperto nulla).</param>
public sealed record UpdateRunResult(UpdateRunOutcome Outcome, string Message, bool RolledBack, string? Relaunched)
{
    public bool Succeeded => Outcome == UpdateRunOutcome.Installed;
}

/// <summary>Operazioni di sistema del programma di aggiornamento (le prove le sostituiscono).</summary>
public interface IUpdateEnvironment
{
    /// <summary>Aspetta la fine del processo; vero se è terminato (o non esiste più).</summary>
    bool WaitForExit(int processId, TimeSpan timeout);
    bool FileExists(string path);
    bool VerifySha256(string path, string sha256);
    /// <summary>Avvia l'installer e ne aspetta la fine; codice di uscita, null se non è finito in tempo (viene fermato).</summary>
    int? RunInstaller(string path, string arguments, TimeSpan timeout);
    /// <summary>Versione (FileVersion, tre cifre) dell'exe; null se manca o non si legge.</summary>
    Version? FileVersion(string exePath);
    /// <summary>Rimette la cartella <paramref name="target"/> uguale alla copia <paramref name="backup"/>.</summary>
    void Restore(string backup, string target);
    void Launch(string exePath, IReadOnlyList<string> arguments);
}

/// <summary>Il programma di aggiornamento vero e proprio: sequenza dei passi e ritorno alla versione di prima.</summary>
public static class UpdateRunner
{
    public static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);

    public static UpdateRunResult Run(UpdateJob job, IUpdateEnvironment env, Action<string> log)
    {
        var exe = Path.Combine(job.InstallDir, UpdateJob.ExeName);
        var target = job.ExpectedVersion.ToString(3);
        var previous = job.PreviousVersion.ToString(3);
        log($"Aggiornamento {previous} → {target}: aspetto la chiusura di RdpManager (processo {job.AppProcessId})");

        if (!env.WaitForExit(job.AppProcessId, AppExitTimeout))
        {
            log("RdpManager non si è chiuso in tempo: non installo nulla");
            return new UpdateRunResult(UpdateRunOutcome.AppStillRunning,
                "RdpManager non si è chiuso in tempo: l'aggiornamento non è stato installato.", false, null);
        }
        log("RdpManager chiuso");

        // Base per riaprire l'app: stesso file dati, sessioni riaperte dall'app stessa (segnale salvato prima di uscire).
        var appArgs = job.DataFile is null ? new List<string>() : ["--dati", job.DataFile];

        UpdateRunResult Fail(UpdateRunOutcome outcome, string reason, bool restore)
        {
            log(reason);
            bool rolledBack = false;
            var relaunch = exe;
            if (restore)
            {
                try
                {
                    log("Rimetto la versione " + previous + " dalla copia di riserva");
                    env.Restore(job.BackupDir, job.InstallDir);
                    rolledBack = true;
                    log("Versione " + previous + " rimessa");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Ultima risorsa: la copia di riserva è un'installazione completa e si avvia da sola.
                    log("Ripristino non riuscito: " + ex.Message + " — riapro la copia di riserva");
                    relaunch = Path.Combine(job.BackupDir, UpdateJob.ExeName);
                }
            }
            var message = $"L'aggiornamento a RdpManager {target} non è riuscito: {reason}\n\n" +
                          (restore ? $"È stata riaperta la versione precedente ({previous}). " : $"Resta in uso la versione {previous}. ") +
                          $"Dettagli nel log: {job.LogPath}";
            env.Launch(relaunch, [.. appArgs, UpdateJob.FailedArgument, message]);
            log("Riaperto " + relaunch);
            return new UpdateRunResult(outcome, message, rolledBack, relaunch);
        }

        // Un file non verificato non si esegue mai.
        if (!env.FileExists(job.InstallerPath) || !env.VerifySha256(job.InstallerPath, job.Sha256))
            return Fail(UpdateRunOutcome.NotVerified, "l'installer non corrisponde più allo SHA256 verificato e non è stato avviato.", restore: false);
        log("Installer verificato con SHA256");

        if (!env.FileExists(Path.Combine(job.BackupDir, UpdateJob.ExeName)) || env.FileVersion(Path.Combine(job.BackupDir, UpdateJob.ExeName)) is null)
            return Fail(UpdateRunOutcome.NoBackup, "la copia di riserva della versione in uso non è completa, quindi non ho installato nulla.", restore: false);

        log($"Avvio dell'installer: {Path.GetFileName(job.InstallerPath)} {job.InstallerArguments()}");
        int? code = env.RunInstaller(job.InstallerPath, job.InstallerArguments(), InstallerTimeout);
        if (code is null)
            return Fail(UpdateRunOutcome.InstallerFailed, $"l'installer non ha finito entro {InstallerTimeout.TotalMinutes:0} minuti.", restore: true);
        log("Installer terminato con codice " + code);
        if (code != 0)
            return Fail(UpdateRunOutcome.InstallerFailed, $"l'installer è terminato con il codice {code} (log dell'installazione: {job.SetupLogPath}).", restore: true);

        var installed = job.SimulateFailure ? null : env.FileVersion(exe);
        if (job.SimulateFailure)
            log("Prova: errore simulato nella verifica della versione");
        if (installed != job.ExpectedVersion)
            return Fail(UpdateRunOutcome.WrongVersion,
                $"dopo l'installazione la versione è {(installed is null ? "illeggibile" : installed.ToString(3))} invece di {target}.", restore: true);
        log("Versione installata verificata: " + target);

        if (!job.Relaunch)
            return new UpdateRunResult(UpdateRunOutcome.Installed, $"RdpManager aggiornato alla {target}.", false, null);
        env.Launch(exe, appArgs);
        log("Riaperto RdpManager " + target);
        return new UpdateRunResult(UpdateRunOutcome.Installed, $"RdpManager aggiornato alla {target}.", false, exe);
    }
}

/// <summary>Copia di riserva della cartella installata e ripristino (cartella identica alla copia).</summary>
public static class FolderBackup
{
    /// <summary>Copia completa di <paramref name="source"/> in <paramref name="destination"/>, svuotata prima.</summary>
    public static void Copy(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Cartella non trovata: " + source);
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        CopyInto(source, destination);
    }

    /// <summary>
    /// Rende <paramref name="target"/> uguale a <paramref name="backup"/>: toglie i file che nella copia non ci sono
    /// (aggiunti da un'installazione a metà) e rimette tutti gli altri.
    /// </summary>
    public static void Restore(string backup, string target)
    {
        if (!File.Exists(Path.Combine(backup, UpdateJob.ExeName)))
            throw new IOException("Copia di riserva incompleta: " + backup);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(target, file);
            if (!File.Exists(Path.Combine(backup, rel)))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
        }
        foreach (var dir in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.Exists(Path.Combine(backup, Path.GetRelativePath(target, dir))) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        CopyInto(backup, target);
    }

    private static void CopyInto(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(dest))
                File.SetAttributes(dest, FileAttributes.Normal);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

/// <summary>Le operazioni vere del programma di aggiornamento.</summary>
public sealed class SystemUpdateEnvironment : IUpdateEnvironment
{
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // già chiuso
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool VerifySha256(string path, string sha256)
    {
        try
        {
            return Sha256Sums.Verify(path, sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public int? RunInstaller(string path, string arguments, TimeSpan timeout)
    {
        using var p = Process.Start(new ProcessStartInfo(path)
        {
            Arguments = arguments,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        }) ?? throw new IOException("Installer non avviato");
        if (p.WaitForExit(timeout))
            return p.ExitCode;
        try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return null;
    }

    public Version? FileVersion(string exePath)
    {
        if (!File.Exists(exePath))
            return null;
        var info = FileVersionInfo.GetVersionInfo(exePath);
        return info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0
            ? null
            : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    public void Restore(string backup, string target) => FolderBackup.Restore(backup, target);

    public void Launch(string exePath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    /// <summary>Riga del log del programma di aggiornamento (UTF-8, con data e ora).</summary>
    public static void AppendLog(string logPath, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
