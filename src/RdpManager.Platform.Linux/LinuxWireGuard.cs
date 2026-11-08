using RdpManager.Core;

namespace RdpManager.Platform.Linux;

public enum LinuxVpnBackend { NetworkManager, WgQuick }

/// <summary>Un comando esterno da eseguire (programma + argomenti, senza shell).</summary>
public sealed record LinuxCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Costruzione dei comandi (pura, provabile ovunque). Il nome del tunnel è anche il nome del file .conf e dell'interfaccia (max 15 caratteri).</summary>
public static class LinuxWireGuardCommands
{
    public static string DefaultConfPath(string? dataFolder = null, string tunnel = VpnDefaults.TunnelName) =>
        Path.Combine(dataFolder ?? AppPaths.DataFolder, "vpn", tunnel + ".conf");

    /// <summary>Il .conf per Linux: stesso contenuto del Core, righe con LF.</summary>
    public static string ConfText(WireGuardConfig config) =>
        config.ToConf().Replace("\r\n", "\n");

    // NetworkManager: l'importazione dà alla connessione il nome del file.
    public static LinuxCommand NmImport(string confPath) => new("nmcli", ["connection", "import", "type", "wireguard", "file", confPath]);
    public static LinuxCommand NmDelete(string tunnel = VpnDefaults.TunnelName) => new("nmcli", ["connection", "delete", tunnel]);
    public static LinuxCommand NmUp(string tunnel = VpnDefaults.TunnelName) => new("nmcli", ["connection", "up", tunnel]);
    public static LinuxCommand NmDown(string tunnel = VpnDefaults.TunnelName) => new("nmcli", ["connection", "down", tunnel]);
    public static LinuxCommand NmActive() => new("nmcli", ["-t", "-f", "NAME,TYPE,DEVICE", "connection", "show", "--active"]);
    public static LinuxCommand NmVersion() => new("nmcli", ["--version"]);

    // wg-quick: serve root, quindi pkexec (richiesta grafica della password).
    public static LinuxCommand WgQuickUp(string confPath) => new("pkexec", ["wg-quick", "up", confPath]);
    public static LinuxCommand WgQuickDown(string confPath) => new("pkexec", ["wg-quick", "down", confPath]);
    public static LinuxCommand LinkStatus(string tunnel = VpnDefaults.TunnelName) => new("ip", ["-o", "link", "show", "dev", tunnel]);

    /// <summary>True se l'elenco di «nmcli -t -f NAME,TYPE,DEVICE connection show --active» contiene il tunnel WireGuard.</summary>
    public static bool IsActiveInNmcli(string output, string tunnel = VpnDefaults.TunnelName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = SplitTerse(line);
            if (fields.Count >= 2 && fields[0] == tunnel && fields[1] == "wireguard")
                return true;
        }
        return false;
    }

    // Modo "terse" di nmcli: campi separati da ':' con "\:" e "\\" come escape.
    internal static List<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
                cur.Append(line[++i]);
            else if (c == ':')
            {
                fields.Add(cur.ToString());
                cur.Clear();
            }
            else
                cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }
}

public sealed record LinuxVpnResult(bool Success, string Message);

/// <summary>Avvia, ferma e interroga la VPN WireGuard su Linux a partire dalla <see cref="WireGuardConfig"/> del Core.</summary>
public sealed class LinuxWireGuard
{
    private readonly IProcessRunner _runner;
    private readonly string _confPath;
    private LinuxVpnBackend? _backend;

    public LinuxWireGuard(IProcessRunner? runner = null, string? confPath = null, LinuxVpnBackend? backend = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _confPath = confPath ?? LinuxWireGuardCommands.DefaultConfPath();
        _backend = backend;
    }

    public string ConfPath => _confPath;

    /// <summary>NetworkManager se nmcli risponde, altrimenti wg-quick.</summary>
    public async Task<LinuxVpnBackend> GetBackendAsync(CancellationToken ct = default)
    {
        if (_backend is { } known)
            return known;
        var nm = LinuxWireGuardCommands.NmVersion();
        var r = await _runner.RunAsync(nm.FileName, nm.Arguments, null, ct).ConfigureAwait(false);
        _backend = r.Success ? LinuxVpnBackend.NetworkManager : LinuxVpnBackend.WgQuick;
        return _backend.Value;
    }

    public async Task<LinuxVpnResult> ConnectAsync(WireGuardConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
            return new LinuxVpnResult(false, string.Join(" ", errors));

        WriteConf(config);
        if (await GetBackendAsync(ct).ConfigureAwait(false) == LinuxVpnBackend.NetworkManager)
        {
            // Si reimporta ogni volta: la configurazione può essere cambiata. Se non esiste, la delete fallisce e va bene.
            await RunAsync(LinuxWireGuardCommands.NmDelete(), ct).ConfigureAwait(false);
            var import = await RunAsync(LinuxWireGuardCommands.NmImport(_confPath), ct).ConfigureAwait(false);
            if (!import.Success)
                return Fail("Importazione in NetworkManager non riuscita", import);
            var up = await RunAsync(LinuxWireGuardCommands.NmUp(), ct).ConfigureAwait(false);
            return up.Success ? new LinuxVpnResult(true, "VPN connessa.") : Fail("Connessione non riuscita", up);
        }
        var wg = await RunAsync(LinuxWireGuardCommands.WgQuickUp(_confPath), ct).ConfigureAwait(false);
        return wg.Success ? new LinuxVpnResult(true, "VPN connessa.") : Fail("wg-quick up non riuscito", wg);
    }

    public async Task<LinuxVpnResult> DisconnectAsync(CancellationToken ct = default)
    {
        var cmd = await GetBackendAsync(ct).ConfigureAwait(false) == LinuxVpnBackend.NetworkManager
            ? LinuxWireGuardCommands.NmDown()
            : LinuxWireGuardCommands.WgQuickDown(_confPath);
        var r = await RunAsync(cmd, ct).ConfigureAwait(false);
        return r.Success ? new LinuxVpnResult(true, "VPN disconnessa.") : Fail("Disconnessione non riuscita", r);
    }

    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (await GetBackendAsync(ct).ConfigureAwait(false) == LinuxVpnBackend.NetworkManager)
        {
            var r = await RunAsync(LinuxWireGuardCommands.NmActive(), ct).ConfigureAwait(false);
            return r.Success && LinuxWireGuardCommands.IsActiveInNmcli(r.StdOut);
        }
        return (await RunAsync(LinuxWireGuardCommands.LinkStatus(), ct).ConfigureAwait(false)).Success;
    }

    /// <summary>Scrive il .conf con permessi 0600 (contiene la chiave privata).</summary>
    public void WriteConf(WireGuardConfig config)
    {
        var dir = Path.GetDirectoryName(_confPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(_confPath, options);
        using var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
        w.Write(LinuxWireGuardCommands.ConfText(config));
    }

    private Task<ProcessResult> RunAsync(LinuxCommand c, CancellationToken ct) => _runner.RunAsync(c.FileName, c.Arguments, null, ct);

    private static LinuxVpnResult Fail(string what, ProcessResult r)
    {
        var detail = (r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim();
        return new LinuxVpnResult(false, detail.Length > 0 ? $"{what}: {detail}" : $"{what} (codice {r.ExitCode}).");
    }
}
