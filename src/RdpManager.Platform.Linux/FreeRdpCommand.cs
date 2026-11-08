using System.Net;
using System.Net.Sockets;
using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>Un client FreeRDP trovato sul sistema.</summary>
/// <param name="Executable">Nome del programma (es. «xfreerdp3»).</param>
/// <param name="IsVersion3">FreeRDP 3.x (sdl-freerdp3, xfreerdp3): ha «/args-from:stdin» e «/cert:tofu».</param>
/// <param name="IsSdl">Client SDL: non sa incorporarsi in una finestra altrui (niente /parent-window).</param>
public sealed record FreeRdpClient(string Executable, bool IsVersion3, bool IsSdl)
{
    public bool CanEmbed => !IsSdl;
}

/// <summary>Cerca il client FreeRDP fra i programmi installati (pura: l'esistenza dei file si passa da fuori).</summary>
public static class FreeRdpLocator
{
    private static readonly FreeRdpClient[] Embeddable =
    [
        new("xfreerdp3", true, false),
        new("xfreerdp", false, false),
    ];

    private static readonly FreeRdpClient[] Detached =
    [
        new("sdl-freerdp3", true, true),
        new("sdl-freerdp", true, true),
        new("xfreerdp3", true, false),
        new("xfreerdp", false, false),
    ];

    /// <summary>
    /// Il client da usare. Per incorporare la sessione nella scheda servono xfreerdp3/xfreerdp (X11);
    /// altrimenti si preferisce sdl-freerdp. Null se non ce n'è nessuno.
    /// </summary>
    public static FreeRdpClient? Find(bool wantEmbedded, Func<string, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        var order = wantEmbedded ? Embeddable.Concat(Detached) : Detached;
        return order.FirstOrDefault(c => isInstalled(c.Executable));
    }

    /// <summary>Vero se il programma è in una delle cartelle di PATH.</summary>
    public static bool IsOnPath(string executable, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, executable)))
                    return true;
            }
            catch (ArgumentException) { }
        }
        return false;
    }

    /// <summary>Messaggio per chi non ha FreeRDP, con il comando di installazione per le distribuzioni più comuni.</summary>
    public static string NotInstalledMessage() =>
        "FreeRDP non è installato, e serve per aprire le sessioni di Desktop remoto. Installalo con il gestore pacchetti della tua distribuzione:\n"
        + "  Debian, Ubuntu, Mint:  sudo apt install freerdp3-x11  (oppure freerdp2-x11)\n"
        + "  Fedora, RHEL:  sudo dnf install freerdp\n"
        + "  openSUSE:  sudo zypper install freerdp\n"
        + "Poi riapri la connessione.";
}

/// <summary>Cosa serve al costruttore degli argomenti oltre alla connessione.</summary>
public sealed record FreeRdpLaunchContext
{
    /// <summary>Handle X11 della finestra della scheda (per /parent-window); null = finestra separata.</summary>
    public long? ParentWindow { get; init; }

    /// <summary>Dimensione disponibile (scheda o finestra), usata se la risoluzione segue la finestra.</summary>
    public int AvailableWidth { get; init; } = 1280;
    public int AvailableHeight { get; init; } = 720;

    /// <summary>Schermo intero (solo finestra separata).</summary>
    public bool Fullscreen { get; init; }

    /// <summary>Cartella locale condivisa quando le unità sono reindirizzate (di solito la home).</summary>
    public string? DrivePath { get; init; }

    /// <summary>Credenziali del gateway, se vanno dichiarate (null = nessuna).</summary>
    public ResolvedCredentials? Gateway { get; init; }
}

/// <summary>Il comando da avviare: argomenti senza segreti, e righe da scrivere sullo stdin del processo.</summary>
public sealed record FreeRdpCommand(string FileName, IReadOnlyList<string> Arguments, IReadOnlyList<string> StdinLines)
{
    /// <summary>Per i log: la riga di comando (non contiene password).</summary>
    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Costruisce la riga di comando di FreeRDP da una connessione. Pura: nessun processo, nessun accesso al disco.</summary>
public static class FreeRdpCommandBuilder
{
    public const int MinSize = 200;
    public const int MaxSize = 8192;

    /// <summary>Vero se la sessione verrà incorporata nella scheda con questi dati.</summary>
    public static bool WillEmbed(RdpConnection c, FreeRdpClient client, FreeRdpLaunchContext ctx) =>
        ctx.ParentWindow is not null && client.CanEmbed && !c.UseAllMonitors;

    public static FreeRdpCommand Build(RdpConnection c, FreeRdpClient client, FreeRdpLaunchContext? ctx = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(client);
        ctx ??= new FreeRdpLaunchContext();
        var embedded = WillEmbed(c, client, ctx);
        var args = new List<string> { "/v:" + c.Host.Trim() };
        var stdin = new List<string>();

        var port = c.Port is > 0 and <= 65535 ? c.Port : RdpConnection.DefaultPort;
        if (port != RdpConnection.DefaultPort)
            args.Add("/port:" + port);
        if (c.DisplayName.Length > 0)
            args.Add("/title:" + c.DisplayName);
        if (c.Username.Length > 0)
            args.Add("/u:" + c.Username);
        if (c.Domain.Length > 0)
            args.Add("/d:" + c.Domain);

        // Certificato: «fidati al primo uso», perché senza terminale FreeRDP non può chiedere nulla.
        args.Add(client.IsVersion3 ? "/cert:tofu" : "/cert-tofu");

        // Schermo.
        var resizable = c.ResolutionMode == ResolutionMode.FitToWindow;
        if (embedded)
        {
            args.Add("/parent-window:" + ctx.ParentWindow);
            AddSize(args, c, ctx, resizable);
            if (!resizable)
                args.Add("/smart-sizing");
        }
        else if (c.UseAllMonitors)
        {
            args.Add("/f");
            args.Add("/multimon");
        }
        else if (ctx.Fullscreen)
        {
            args.Add("/f");
        }
        else
        {
            AddSize(args, c, ctx, resizable);
        }

        // Dispositivi.
        args.Add(c.RedirectClipboard ? "+clipboard" : "-clipboard");
        if (c.RedirectDrives && !string.IsNullOrWhiteSpace(ctx.DrivePath))
            args.Add("/drive:home," + ctx.DrivePath);
        if (c.RedirectPrinters)
            args.Add("/printer");
        if (c.RedirectMicrophone)
            args.Add("/microphone");
        args.Add(c.AudioMode switch
        {
            AudioMode.Local => "/sound",
            AudioMode.Remote => "/audio-mode:1",
            _ => "/audio-mode:2",
        });

        args.Add(c.Quality switch
        {
            QualityProfile.LocalNetwork => "/network:lan",
            QualityProfile.SlowConnection => "/network:modem",
            _ => "/network:auto",
        });

        if (c.AutoReconnect)
        {
            args.Add("+auto-reconnect");
            args.Add("/auto-reconnect-max-retries:" + Math.Clamp(c.AutoReconnectMaxAttempts, 1, 20));
        }

        // Gateway.
        if (c.HasGateway)
        {
            args.Add("/g:" + c.GatewayHost.Trim());
            if (ctx.Gateway is { } gw)
            {
                if (gw.Username.Length > 0)
                    args.Add("/gu:" + gw.Username);
                if (gw.Domain.Length > 0)
                    args.Add("/gd:" + gw.Domain);
                // FreeRDP 2 non sa leggere la password del gateway da stdin: userà quella della connessione.
                if (client.IsVersion3 && gw.HasPassword)
                    stdin.Add("/gp:" + gw.Password);
            }
        }

        // Password: mai nella riga di comando (si vedrebbe con «ps»).
        if (client.IsVersion3)
        {
            if (!string.IsNullOrEmpty(c.Password))
                stdin.Insert(0, "/p:" + c.Password);
            if (stdin.Count > 0)
                args.Add("/args-from:stdin");
        }
        else if (!string.IsNullOrEmpty(c.Password))
        {
            args.Add("/from-stdin");
            stdin.Add(c.Password);
        }

        return new FreeRdpCommand(client.Executable, args, stdin);
    }

    private static void AddSize(List<string> args, RdpConnection c, FreeRdpLaunchContext ctx, bool resizable)
    {
        var (w, h) = c.ResolutionMode == ResolutionMode.Fixed
            ? (c.DesktopWidth, c.DesktopHeight)
            : (ctx.AvailableWidth, ctx.AvailableHeight);
        args.Add($"/size:{Math.Clamp(w, MinSize, MaxSize)}x{Math.Clamp(h, MinSize, MaxSize)}");
        if (resizable)
            args.Add("/dynamic-resolution");
    }

    /// <summary>Vero se il PC sta nella rete della VPN WireGuard (indirizzo IPv4 dentro <see cref="VpnDefaults.Network"/>): serve accendere la VPN.</summary>
    public static bool HostIsOnVpnNetwork(string? host, string network = VpnDefaults.Network)
    {
        if (!IPAddress.TryParse((host ?? "").Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var parts = network.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            return false;
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        var bits = prefix;
        for (var i = 0; i < 4 && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask))
                return false;
        }
        return true;
    }
}
