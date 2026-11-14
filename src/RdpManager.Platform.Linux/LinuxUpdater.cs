using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>Come è installata l'app su Linux.</summary>
public enum LinuxInstallKind
{
    /// <summary>Non riconosciuto (eseguibile sciolto, compilato a mano): non si aggiorna da solo.</summary>
    Unknown,
    AppImage,
    Deb,
    Rpm,
}

/// <param name="AppImagePath">Solo per <see cref="LinuxInstallKind.AppImage"/>: il file .AppImage in uso.</param>
/// <param name="LaunchPath">Cosa riavviare dopo l'aggiornamento.</param>
public sealed record LinuxInstallInfo(LinuxInstallKind Kind, string? AppImagePath = null, string? LaunchPath = null);

/// <summary>Il poco che l'aggiornamento chiede al sistema (variabili, file, programmi, avvio): si sostituisce nelle prove.</summary>
public interface IUpdateSystem
{
    string? GetEnvironmentVariable(string name);

    /// <summary>Percorso dell'eseguibile in corso (Environment.ProcessPath).</summary>
    string? ProcessPath { get; }

    bool FileExists(string path);

    /// <summary>Vero se il programma si trova nel PATH.</summary>
    bool CommandExists(string name);

    /// <summary>Avvia <paramref name="path"/> staccato da questo processo, dopo qualche secondo (l'app attuale sta chiudendo).</summary>
    void RelaunchDetached(string path);
}

public sealed class SystemUpdateSystem : IUpdateSystem
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? ProcessPath => Environment.ProcessPath;

    public bool FileExists(string path) => File.Exists(path);

    public bool CommandExists(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name)))
                    return true;
            }
            catch (ArgumentException)
            {
            }
        }
        return false;
    }

    public void RelaunchDetached(string path)
    {
        // sh aspetta che l'app attuale sia uscita e poi riapre; il percorso è un argomento, mai testo di shell.
        var psi = new System.Diagnostics.ProcessStartInfo("sh") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 2; exec \"$0\"");
        psi.ArgumentList.Add(path);
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }
}

public enum LinuxInstallStatus
{
    /// <summary>Installato: si può riavviare.</summary>
    Installed,
    /// <summary>L'utente ha annullato la richiesta di autorizzazione (o non è stato autorizzato).</summary>
    Cancelled,
    /// <summary>Manca pkexec (o il gestore dei pacchetti): serve l'installazione a mano.</summary>
    NoPrivilegeTool,
    Failed,
}

public sealed record LinuxInstallResult(LinuxInstallStatus Status, string Message)
{
    public bool IsInstalled => Status == LinuxInstallStatus.Installed;
}

/// <summary>Un comando da eseguire: programma e argomenti (mai una stringa di shell).</summary>
public sealed record UpdateCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Aggiornamento automatico su Linux, senza interfaccia: riconosce come è installata l'app, sceglie l'allegato della
/// release, costruisce i comandi (pkexec + apt/dnf/zypper con ripiego su dpkg/rpm) e sostituisce l'AppImage in modo atomico.
/// Scaricare e verificare l'hash è di <see cref="UpdateDownloader"/>.
/// </summary>
public sealed class LinuxUpdater
{
    /// <summary>Cartella dei pacchetti installati: vi sta l'app di .deb e .rpm.</summary>
    public const string InstallRoot = "/opt/rdpmanager";

    /// <summary>Codici di pkexec: 126 = finestra chiusa senza autorizzare, 127 = non autorizzato.</summary>
    private const int PkexecDismissed = 126, PkexecNotAuthorized = 127;

    private readonly IProcessRunner _runner;
    private readonly IUpdateSystem _system;

    public LinuxUpdater(IProcessRunner? runner = null, IUpdateSystem? system = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _system = system ?? new SystemUpdateSystem();
    }

    // --- Rilevamento ---

    public async Task<LinuxInstallInfo> DetectAsync(CancellationToken ct = default)
    {
        var appImage = _system.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && _system.FileExists(appImage))
            return new LinuxInstallInfo(LinuxInstallKind.AppImage, appImage, appImage);

        var exe = _system.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !exe.StartsWith(InstallRoot + "/", StringComparison.Ordinal))
            return new LinuxInstallInfo(LinuxInstallKind.Unknown);

        const string launcher = "/usr/bin/rdpmanager";
        if (_system.CommandExists("dpkg") && (await _runner.RunAsync("dpkg", ["-S", exe], ct: ct).ConfigureAwait(false)).Success)
            return new LinuxInstallInfo(LinuxInstallKind.Deb, LaunchPath: launcher);
        if (_system.CommandExists("rpm") && (await _runner.RunAsync("rpm", ["-qf", exe], ct: ct).ConfigureAwait(false)).Success)
            return new LinuxInstallInfo(LinuxInstallKind.Rpm, LaunchPath: launcher);
        return new LinuxInstallInfo(LinuxInstallKind.Unknown);
    }

    // --- Scelta del file ---

    public static string AssetName(LinuxInstallKind kind, Version version)
    {
        var v = version.ToString(3);
        return kind switch
        {
            LinuxInstallKind.Deb => $"rdpmanager_{v}_amd64.deb",
            LinuxInstallKind.Rpm => $"rdpmanager-{v}-1.x86_64.rpm",
            LinuxInstallKind.AppImage => $"RdpManager-{v}-x86_64.AppImage",
            _ => "",
        };
    }

    /// <summary>L'allegato giusto della release per questo tipo di installazione; null se non c'è (o tipo sconosciuto).</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, LinuxInstallKind kind)
    {
        var name = AssetName(kind, release.Version);
        return name.Length == 0 ? null : release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // --- Comandi ---

    /// <summary>
    /// Comandi da provare in ordine per un .deb/.rpm, già con pkexec; ogni gestore c'è solo se è installato.
    /// Il primo che riesce basta. Vuota se manca pkexec o ogni gestore.
    /// </summary>
    public IReadOnlyList<UpdateCommand> BuildInstallCommands(LinuxInstallKind kind, string file)
    {
        if (!_system.CommandExists("pkexec"))
            return [];
        var list = new List<UpdateCommand>();
        void Add(string tool, params string[] args)
        {
            if (_system.CommandExists(tool))
                list.Add(new UpdateCommand("pkexec", [tool, .. args]));
        }
        switch (kind)
        {
            case LinuxInstallKind.Deb:
                Add("apt-get", "install", "-y", file);
                Add("dpkg", "-i", file);
                break;
            case LinuxInstallKind.Rpm:
                Add("dnf", "install", "-y", file);
                Add("zypper", "--non-interactive", "--no-gpg-checks", "install", file);
                Add("rpm", "-U", "--quiet", file);
                break;
        }
        return list;
    }

    // --- Installazione ---

    /// <summary>Installa il file già scaricato e verificato. Per l'AppImage lo sostituisce, per deb/rpm passa da pkexec.</summary>
    public async Task<LinuxInstallResult> InstallAsync(LinuxInstallInfo info, string downloadedFile, CancellationToken ct = default)
    {
        if (info.Kind == LinuxInstallKind.AppImage)
            return ReplaceAppImage(info.AppImagePath, downloadedFile);
        if (info.Kind is not (LinuxInstallKind.Deb or LinuxInstallKind.Rpm))
            return new(LinuxInstallStatus.Failed, "Questo tipo di installazione non si aggiorna da solo.");
        if (!_system.CommandExists("pkexec"))
            return new(LinuxInstallStatus.NoPrivilegeTool, "Manca pkexec (polkit): non posso chiedere i permessi per installare.");

        var commands = BuildInstallCommands(info.Kind, downloadedFile);
        if (commands.Count == 0)
            return new(LinuxInstallStatus.NoPrivilegeTool, "Non trovo un gestore di pacchetti (apt, dnf, zypper, dpkg, rpm).");

        string lastError = "";
        foreach (var c in commands)
        {
            ProcessResult r;
            try
            {
                r = await _runner.RunAsync(c.FileName, c.Arguments, ct: ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex.Message;
                continue;
            }
            if (r.Success)
                return new(LinuxInstallStatus.Installed, "Aggiornamento installato.");
            if (r.ExitCode is PkexecDismissed or PkexecNotAuthorized)
                return new(LinuxInstallStatus.Cancelled, "Autorizzazione non concessa: l'aggiornamento non è stato installato.");
            lastError = FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? $"codice {r.ExitCode}";
        }
        return new(LinuxInstallStatus.Failed, "Installazione non riuscita: " + lastError);
    }

    /// <summary>Scrive il nuovo file accanto all'AppImage, lo rende eseguibile e lo mette al posto del vecchio con un rename.</summary>
    public LinuxInstallResult ReplaceAppImage(string? appImagePath, string downloadedFile)
    {
        if (string.IsNullOrWhiteSpace(appImagePath))
            return new(LinuxInstallStatus.Failed, "Non so dove si trova il file AppImage.");
        var temp = appImagePath + ".new-" + Environment.ProcessId;
        try
        {
            File.Copy(downloadedFile, temp, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                           | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temp, appImagePath, overwrite: true);
            return new(LinuxInstallStatus.Installed, "AppImage sostituita.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new(LinuxInstallStatus.Failed, "Non riesco a sostituire l'AppImage (cartella in sola lettura?): " + ex.Message);
        }
    }

    /// <summary>Riapre l'app aggiornata (l'attuale deve chiudersi subito dopo).</summary>
    public void Relaunch(LinuxInstallInfo info)
    {
        if (info.LaunchPath is { Length: > 0 } path)
            _system.RelaunchDetached(path);
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
