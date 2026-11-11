using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Features;

/// <summary>
/// Tasti e foto verso la sessione, con i programmi del desktop: <c>xdotool</c> su X11, <c>ydotool</c> su Wayland;
/// <c>import</c> (ImageMagick) o <c>grim</c> per la foto; <c>onboard</c> e simili per la tastiera su schermo.
/// Le costruzioni dei comandi sono pure (si provano senza desktop).
/// </summary>
public sealed class RemoteInput
{
    private static readonly string[] KeyboardPrograms = ["onboard", "florence", "matchbox-keyboard", "xvkbd"];

    private readonly IProcessRunner _runner;
    private readonly DisplayServerKind _kind;
    private readonly Func<string, bool> _isInstalled;

    public RemoteInput(IProcessRunner? runner = null, DisplayServerKind? kind = null, Func<string, bool>? isInstalled = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _kind = kind ?? DisplayServer.Current;
        _isInstalled = isInstalled ?? (name => FreeRdpLocator.IsOnPath(name));
    }

    /// <summary>Il programma che invia i tasti, o null se manca (con il messaggio per installarlo in <paramref name="missing"/>).</summary>
    public string? TypingTool(out string? missing)
    {
        missing = null;
        if (_kind == DisplayServerKind.Wayland)
        {
            if (_isInstalled("ydotool"))
                return "ydotool";
            missing = "Per inviare tasti alla sessione su Wayland serve «ydotool» (con il suo servizio ydotoold attivo): installalo con il gestore pacchetti della distribuzione.";
            return null;
        }
        if (_isInstalled("xdotool"))
            return "xdotool";
        missing = "Per inviare tasti alla sessione serve «xdotool»: installalo con il gestore pacchetti (per esempio sudo apt install xdotool).";
        return null;
    }

    public static LinuxCommand CtrlAltDelCommand(string tool) => tool == "ydotool"
        ? new LinuxCommand("ydotool", ["key", "29:1", "56:1", "111:1", "111:0", "56:0", "29:0"])
        : new LinuxCommand("xdotool", ["key", "--clearmodifiers", "ctrl+alt+Delete"]);

    public static LinuxCommand TypeCommand(string tool, string text) => tool == "ydotool"
        ? new LinuxCommand("ydotool", ["type", "--", text])
        : new LinuxCommand("xdotool", ["type", "--clearmodifiers", "--delay", "12", "--", text]);

    /// <summary>Combinazione di tasti nel formato di xdotool («alt+Tab», «super+d») o clic del mouse («click:3»); solo X11.</summary>
    public static LinuxCommand KeyComboCommand(string combo) => combo.StartsWith("click:", StringComparison.Ordinal)
        ? new LinuxCommand("xdotool", ["click", combo[6..]])
        : new LinuxCommand("xdotool", ["key", "--clearmodifiers", combo]);

    public async Task<string?> SendKeysAsync(string combo, CancellationToken ct = default)
    {
        if (_kind == DisplayServerKind.Wayland)
            return "Questo gesto invia tasti al PC remoto, cosa che su Wayland non è possibile senza strumenti aggiuntivi.";
        if (!_isInstalled("xdotool"))
            return "Per inviare tasti alla sessione serve «xdotool»: installalo con il gestore pacchetti (per esempio sudo apt install xdotool).";
        return await Run(KeyComboCommand(combo), ct);
    }

    /// <summary>Limite per non digitare per minuti una cosa copiata per sbaglio.</summary>
    public const int MaxTypedLength = 4000;

    /// <summary>Messaggio di errore, oppure null se il comando è partito.</summary>
    public async Task<string?> SendCtrlAltDelAsync(CancellationToken ct = default)
    {
        var tool = TypingTool(out var missing);
        return tool is null ? missing : await Run(CtrlAltDelCommand(tool), ct);
    }

    public async Task<string?> TypeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return "Negli appunti non c'è testo da digitare.";
        if (text.Length > MaxTypedLength)
            return $"Il testo negli appunti è troppo lungo ({text.Length} caratteri, al massimo {MaxTypedLength}).";
        var tool = TypingTool(out var missing);
        return tool is null ? missing : await Run(TypeCommand(tool, text), ct);
    }

    private async Task<string?> Run(LinuxCommand c, CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(c.FileName, c.Arguments, null, ct).ConfigureAwait(false);
            if (r.Success)
                return null;
            var detail = (r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim();
            return $"{c.FileName} non è riuscito" + (detail.Length > 0 ? ": " + detail : $" (codice {r.ExitCode}).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Non riesco a eseguire {c.FileName}: {ex.Message}";
        }
    }

    // --- Tastiera su schermo ---

    /// <summary>Il primo programma di tastiera su schermo installato; null se non ce n'è.</summary>
    public string? ScreenKeyboardProgram() => KeyboardPrograms.FirstOrDefault(_isInstalled);

    public async Task<string?> OpenScreenKeyboardAsync(CancellationToken ct = default)
    {
        var program = ScreenKeyboardProgram();
        if (program is null)
            return "Non trovo una tastiera su schermo: installa «onboard» (sudo apt install onboard) e riprova.";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi)?.Dispose();
            await Task.CompletedTask;
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Non riesco ad aprire {program}: {ex.Message}";
        }
    }

    // --- Foto della sessione ---

    public static string SnapshotFileName(string title, DateTime now)
    {
        var safe = new string(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return $"RdpManager-{(safe.Length == 0 ? "sessione" : safe)}-{now:yyyyMMdd-HHmmss}.png";
    }

    /// <summary>Il comando che salva la foto, o null se non c'è un modo (messaggio in <paramref name="missing"/>).</summary>
    public LinuxCommand? SnapshotCommand(long? windowId, string path, out string? missing)
    {
        missing = null;
        if (_kind != DisplayServerKind.Wayland && windowId is { } id)
        {
            if (_isInstalled("import"))
                return new LinuxCommand("import", ["-window", "0x" + id.ToString("x"), path]);
            missing = "Per la foto della sessione serve «import» (ImageMagick): sudo apt install imagemagick.";
            return null;
        }
        if (_isInstalled("grim"))
            return new LinuxCommand("grim", [path]);
        missing = _kind == DisplayServerKind.Wayland
            ? "Su Wayland la foto della sessione si fa con «grim» (cattura tutto lo schermo): installalo con il gestore pacchetti."
            : "La sessione è in una finestra separata: non posso fotografarla. Usa lo strumento di cattura del desktop.";
        return null;
    }

    /// <summary>Salva la foto; restituisce (percorso, errore): uno dei due è null.</summary>
    public async Task<(string? Path, string? Error)> SnapshotAsync(long? windowId, string title, string folder, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(folder, SnapshotFileName(title, DateTime.Now));
        var command = SnapshotCommand(windowId, path, out var missing);
        if (command is null)
            return (null, missing);
        Directory.CreateDirectory(folder);
        var error = await Run(command, ct);
        return error is null ? (path, null) : (null, error);
    }
}
