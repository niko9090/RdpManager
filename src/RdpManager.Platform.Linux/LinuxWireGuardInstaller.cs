namespace RdpManager.Platform.Linux;

public enum LinuxPackageManager { None, Apt, Dnf, Zypper, Pacman }

/// <summary>Cerca i programmi nelle cartelle del PATH (più quelle di sistema). Pura: l'esistenza dei file si passa da fuori.</summary>
public static class LinuxTools
{
    private static readonly string[] SystemDirs = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    public static bool Exists(string tool, string? pathEnv, Func<string, bool> fileExists)
    {
        var dirs = (pathEnv ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(SystemDirs);
        return dirs.Any(d => fileExists(d.TrimEnd('/') + "/" + tool));
    }

    public static bool Exists(string tool) =>
        Exists(tool, Environment.GetEnvironmentVariable("PATH"), File.Exists);
}

/// <summary>Rilevamento di wireguard-tools e costruzione del comando di installazione col gestore pacchetti della distribuzione.</summary>
public static class LinuxWireGuardInstall
{
    public const string PackageName = "wireguard-tools";

    /// <summary>wireguard-tools è presente se ci sono sia «wg» sia «wg-quick».</summary>
    public static bool ToolsPresent(Func<string, bool> toolExists) => toolExists("wg") && toolExists("wg-quick");

    public static LinuxPackageManager Detect(Func<string, bool> toolExists)
    {
        if (toolExists("apt-get")) return LinuxPackageManager.Apt;
        if (toolExists("dnf")) return LinuxPackageManager.Dnf;
        if (toolExists("zypper")) return LinuxPackageManager.Zypper;
        if (toolExists("pacman")) return LinuxPackageManager.Pacman;
        return LinuxPackageManager.None;
    }

    public static string Describe(LinuxPackageManager pm) => pm switch
    {
        LinuxPackageManager.Apt => "apt",
        LinuxPackageManager.Dnf => "dnf",
        LinuxPackageManager.Zypper => "zypper",
        LinuxPackageManager.Pacman => "pacman",
        _ => "nessuno",
    };

    /// <summary>Comando con pkexec (richiesta grafica della password di amministratore); null se non c'è un gestore noto.</summary>
    public static LinuxCommand? InstallCommand(LinuxPackageManager pm) => pm switch
    {
        // Un solo pkexec (una sola password): prima l'aggiornamento dell'elenco, poi l'installazione. Testo fisso, nessun input utente.
        LinuxPackageManager.Apt => new("pkexec", ["env", "DEBIAN_FRONTEND=noninteractive", "sh", "-c",
            "apt-get update; apt-get install -y " + PackageName]),
        LinuxPackageManager.Dnf => new("pkexec", ["dnf", "install", "-y", PackageName]),
        LinuxPackageManager.Zypper => new("pkexec", ["zypper", "--non-interactive", "install", PackageName]),
        LinuxPackageManager.Pacman => new("pkexec", ["pacman", "-S", "--noconfirm", PackageName]),
        _ => null,
    };
}

/// <summary>Installa wireguard-tools dal programma. Nelle prove il runner e la ricerca dei programmi sono sostituiti.</summary>
public sealed class LinuxWireGuardInstaller(IProcessRunner? runner = null, Func<string, bool>? toolExists = null)
{
    private readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();
    private readonly Func<string, bool> _exists = toolExists ?? LinuxTools.Exists;

    public bool ToolsPresent => LinuxWireGuardInstall.ToolsPresent(_exists);

    public async Task<LinuxVpnResult> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (ToolsPresent)
            return new LinuxVpnResult(true, "WireGuard è già installato.");
        var pm = LinuxWireGuardInstall.Detect(_exists);
        if (LinuxWireGuardInstall.InstallCommand(pm) is not { } cmd)
            return new LinuxVpnResult(false, "Non riconosco il gestore pacchetti di questa distribuzione (provo apt, dnf, zypper e pacman): installa il pacchetto «"
                                             + LinuxWireGuardInstall.PackageName + "» a mano.");
        if (!_exists("pkexec"))
            return new LinuxVpnResult(false, "Manca pkexec, che serve a chiedere la password di amministratore: installa «"
                                             + LinuxWireGuardInstall.PackageName + "» a mano con " + LinuxWireGuardInstall.Describe(pm) + ".");

        progress?.Report("Installazione di WireGuard con " + LinuxWireGuardInstall.Describe(pm) + ": inserisci la password di amministratore nella finestra che si apre…");
        var r = await _runner.RunAsync(cmd.FileName, cmd.Arguments, null, ct).ConfigureAwait(false);
        if (r.ExitCode == 126) // pkexec: finestra della password chiusa o non autorizzato
            return new LinuxVpnResult(false, "Installazione annullata: serviva la password di amministratore.");
        if (!r.Success)
        {
            var detail = LastLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
            return new LinuxVpnResult(false, "Installazione di WireGuard non riuscita (codice " + r.ExitCode + ")" + (detail.Length > 0 ? ": " + detail : "."));
        }
        progress?.Report("Verifica dell'installazione…");
        return ToolsPresent
            ? new LinuxVpnResult(true, "WireGuard installato.")
            : new LinuxVpnResult(false, "Il gestore pacchetti ha finito ma wg e wg-quick non si trovano ancora: riavvia il programma o controlla l'installazione.");
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
