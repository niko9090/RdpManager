namespace RdpManager.Core;

/// <summary>Risposta all'avviso di aggiornamento.</summary>
public enum UpdateChoice
{
    /// <summary>Installa subito (le sessioni aperte si chiudono e si riaprono dopo).</summary>
    Now,
    /// <summary>Installa quando l'utente chiude l'app, senza riavviarla.</summary>
    OnExit,
    /// <summary>Ripropone dopo 24 ore o al prossimo avvio.</summary>
    Later,
}

/// <summary>Installer scaricato e verificato, pronto per l'installazione dopo la conferma.</summary>
public sealed record PendingUpdate(ReleaseInfo Release, string InstallerPath, string Sha256);

/// <summary>
/// Decisioni dell'aggiornamento automatico con conferma (la parte senza interfaccia): quando controllare (all'avvio
/// e ogni 24 ore), quando scaricare, quando riproporre l'avviso dopo «Più tardi», e soprattutto che si esegue solo
/// un installer verificato, ricontrollandone lo SHA256 subito prima dell'avvio.
/// Lo stato è in memoria: dopo un riavvio dell'app l'aggiornamento si ripropone.
/// </summary>
public sealed class AutoUpdatePlanner
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan PostponeInterval = TimeSpan.FromHours(24);

    public DateTime? LastCheckUtc { get; private set; }
    public PendingUpdate? Ready { get; private set; }
    public bool InstallOnExit { get; private set; }
    public Version? PostponedVersion { get; private set; }
    public DateTime? PostponedUntilUtc { get; private set; }

    /// <summary>Vero se è ora di controllare (mai controllato, o sono passate 24 ore).</summary>
    public bool IsCheckDue(DateTime utcNow) => LastCheckUtc is not { } last || utcNow - last >= CheckInterval;

    public void MarkChecked(DateTime utcNow) => LastCheckUtc = utcNow;

    /// <summary>
    /// Vero se conviene scaricare <paramref name="latest"/>: è più nuova della versione in uso e non è già pronta.
    /// </summary>
    public bool ShouldDownload(ReleaseInfo? latest, Version current) =>
        latest is not null && UpdateChecker.IsNewer(latest.Version, current)
        && (Ready is null || UpdateChecker.IsNewer(latest.Version, Ready.Release.Version));

    /// <summary>Registra l'esito dello scaricamento: solo un installer verificato diventa «pronto».</summary>
    public bool Accept(ReleaseInfo release, UpdateDownloadResult result)
    {
        if (!result.IsVerified || result.Sha256 is null || result.InstallerPath is null)
            return false;
        Ready = new PendingUpdate(release, result.InstallerPath, result.Sha256);
        InstallOnExit = false;
        return true;
    }

    /// <summary>Vero se l'avviso va mostrato ora: c'è un aggiornamento pronto, non rimandato e non già fissato per la chiusura.</summary>
    public bool ShouldOffer(DateTime utcNow) =>
        Ready is { } r && !InstallOnExit
        && !(PostponedVersion == r.Release.Version && PostponedUntilUtc is { } until && utcNow < until);

    public void Choose(UpdateChoice choice, DateTime utcNow)
    {
        if (Ready is null)
            return;
        switch (choice)
        {
            case UpdateChoice.Later:
                PostponedVersion = Ready.Release.Version;
                PostponedUntilUtc = utcNow + PostponeInterval;
                InstallOnExit = false;
                break;
            case UpdateChoice.OnExit:
                InstallOnExit = true;
                break;
            case UpdateChoice.Now:
                InstallOnExit = false;
                break;
        }
    }

    /// <summary>
    /// L'installer da avviare, solo se esiste ancora e ha esattamente lo SHA256 verificato; altrimenti null, il file
    /// viene cancellato e l'aggiornamento non è più pronto (un file non verificato non si esegue mai).
    /// </summary>
    public string? VerifiedInstallerToRun()
    {
        if (Ready is not { } r)
            return null;
        if (File.Exists(r.InstallerPath) && Sha256Sums.Verify(r.InstallerPath, r.Sha256))
            return r.InstallerPath;
        try
        {
            if (File.Exists(r.InstallerPath))
                File.Delete(r.InstallerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Ready = null;
        InstallOnExit = false;
        return null;
    }
}

/// <summary>Dove è l'app e come si avvia l'installer per un aggiornamento silenzioso.</summary>
public static class InstallLocation
{
    /// <summary>File del disinstallatore di Inno Setup accanto all'exe installato.</summary>
    public const string UninstallerName = "unins000.exe";

    /// <summary>
    /// Vero se l'exe è la versione portatile (fuori da una cartella di installazione): non si aggiorna da sola,
    /// apre la pagina della release.
    /// </summary>
    public static bool IsPortable(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        return dir is null || !File.Exists(Path.Combine(dir, UninstallerName));
    }

    /// <summary>Argomenti dell'installer per l'aggiornamento silenzioso; con <paramref name="relaunch"/> l'app si riapre.</summary>
    public static string SilentArguments(bool relaunch) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER" + (relaunch ? " /RELAUNCH=1" : "");
}
