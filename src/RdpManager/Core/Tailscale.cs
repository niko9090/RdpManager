using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Un dispositivo della rete Tailscale (tailnet), come lo descrive <c>tailscale status --json</c>.</summary>
public sealed record TailscalePeer(
    string HostName, string DnsName, IPAddress? IPv4, IPAddress? IPv6, string OS, bool Online, bool IsSelf)
{
    /// <summary>Nome da mostrare: il nome MagicDNS breve, altrimenti il nome del dispositivo, altrimenti l'IP.</summary>
    public string DisplayName
    {
        get
        {
            if (DnsName.Length > 0)
                return DnsName.Split('.')[0];
            if (HostName.Length > 0)
                return HostName;
            return (IPv4 ?? IPv6)?.ToString() ?? "";
        }
    }

    /// <summary>
    /// Indirizzo da usare nella connessione: il nome MagicDNS completo se c'è (resta valido anche se l'IP cambia),
    /// altrimenti l'IP 100.x, altrimenti l'IPv6.
    /// </summary>
    public string ConnectAddress => DnsName.Length > 0 ? DnsName : (IPv4 ?? IPv6)?.ToString() ?? "";
}

/// <summary>Stato letto da <c>tailscale status --json</c>.</summary>
public sealed record TailscaleStatus(string BackendState, string MagicDnsSuffix, TailscalePeer? Self, IReadOnlyList<TailscalePeer> Peers)
{
    /// <summary>Tailscale è connesso alla tailnet ("Running").</summary>
    public bool IsRunning => BackendState.Equals("Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legge l'uscita JSON di <c>tailscale status --json</c>: stato, dispositivo locale e dispositivi della tailnet
    /// (in linea prima, poi per nome). I campi mancanti diventano vuoti; un JSON non valido dà <see cref="FormatException"/>.
    /// </summary>
    public static TailscaleStatus Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException ex)
        {
            throw new FormatException("L'uscita di «tailscale status --json» non è un JSON valido.", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("L'uscita di «tailscale status --json» non è un oggetto JSON.");
            var state = Str(root, "BackendState");
            var suffix = Str(root, "MagicDNSSuffix");
            if (suffix.Length == 0 && root.TryGetProperty("CurrentTailnet", out var tn) && tn.ValueKind == JsonValueKind.Object)
                suffix = Str(tn, "MagicDNSSuffix");
            TailscalePeer? self = root.TryGetProperty("Self", out var s) && s.ValueKind == JsonValueKind.Object
                ? ReadPeer(s, isSelf: true) : null;
            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in p.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        peers.Add(ReadPeer(prop.Value, isSelf: false));
                }
            }
            peers = peers.OrderByDescending(x => x.Online).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new TailscaleStatus(state, suffix.TrimEnd('.'), self, peers);
        }
    }

    private static TailscalePeer ReadPeer(JsonElement e, bool isSelf)
    {
        IPAddress? v4 = null, v6 = null;
        if (e.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
        {
            foreach (var ip in ips.EnumerateArray())
            {
                if (ip.ValueKind != JsonValueKind.String || !IPAddress.TryParse(ip.GetString(), out var a))
                    continue;
                if (a.AddressFamily == AddressFamily.InterNetwork)
                    v4 ??= a;
                else if (a.AddressFamily == AddressFamily.InterNetworkV6)
                    v6 ??= a;
            }
        }
        bool online = e.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
        return new TailscalePeer(Str(e, "HostName"), Str(e, "DNSName").TrimEnd('.'), v4, v6, Str(e, "OS"), online, isSelf);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>Messaggio per l'utente se Tailscale non è connesso; vuoto se è connesso.</summary>
    public string StateMessage => BackendState switch
    {
        _ when IsRunning => "",
        "NeedsLogin" => "Tailscale è installato ma non hai ancora fatto l'accesso: aprilo e accedi al tuo account.",
        "Stopped" => "Tailscale è installato ma è spento: aprilo e scegli «Connect».",
        "Starting" => "Tailscale si sta avviando: riprova fra qualche secondo.",
        "NeedsMachineAuth" => "Questo PC aspetta l'approvazione dell'amministratore della rete Tailscale.",
        _ => $"Tailscale non è connesso (stato «{(BackendState.Length > 0 ? BackendState : "sconosciuto")}»).",
    };
}

public enum TailscaleLookupOutcome
{
    Ok,
    /// <summary>tailscale.exe non trovato: Tailscale non è installato su questo PC.</summary>
    NotInstalled,
    /// <summary>Il comando è terminato con errore (servizio fermo, ecc.).</summary>
    Failed,
}

public sealed record TailscaleLookup(TailscaleLookupOutcome Outcome, TailscaleStatus? Status, string Message);

/// <summary>
/// Lettura in sola lettura della rete Tailscale con <c>tailscale status --json</c>: niente impostazioni cambiate,
/// niente altri comandi. Se Tailscale non c'è lo dice con un messaggio chiaro.
/// </summary>
public static class TailscaleCli
{
    public const string NotInstalledMessage =
        "Tailscale non è installato su questo PC (non trovo tailscale.exe). Per vedere qui i PC della tua rete " +
        "Tailscale installalo da tailscale.com e accedi con il tuo account; le altre schede funzionano anche senza.";

    /// <summary>Percorso di tailscale.exe: cartella di installazione standard o PATH; null se non c'è.</summary>
    public static string? FindExecutable(Func<string, bool>? fileExists = null, string? path = null, string? programFiles = null)
    {
        fileExists ??= File.Exists;
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "Tailscale", "tailscale.exe"));
        foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim().Trim('"'), "tailscale.exe"));
            }
            catch (ArgumentException)
            {
                // voce del PATH con caratteri non validi: si salta
            }
        }
        return candidates.FirstOrDefault(fileExists);
    }

    /// <summary>Esegue <c>tailscale status --json</c> e ne legge il risultato.</summary>
    public static async Task<TailscaleLookup> GetStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var exe = FindExecutable();
        if (exe is null)
            return new TailscaleLookup(TailscaleLookupOutcome.NotInstalled, null, NotInstalledMessage);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Impossibile avviare tailscale.exe: " + ex.Message);
        }
        if (process is null)
            return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Impossibile avviare tailscale.exe.");
        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return Interpret(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Tailscale non ha risposto in tempo.");
            }
        }
    }

    /// <summary>Interpreta codice d'uscita e testo del comando (separato per poterlo provare senza Tailscale).</summary>
    public static TailscaleLookup Interpret(int exitCode, string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                var status = TailscaleStatus.Parse(stdout);
                return new TailscaleLookup(TailscaleLookupOutcome.Ok, status, status.StateMessage);
            }
            catch (FormatException) when (exitCode != 0)
            {
                // si ricade sul messaggio d'errore qui sotto
            }
            catch (FormatException ex)
            {
                return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, ex.Message);
            }
        }
        var err = (stderr ?? "").Trim();
        var msg = err.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) || err.Contains("is Tailscale running", StringComparison.OrdinalIgnoreCase)
            ? "Il servizio Tailscale non è avviato su questo PC: aprilo e riprova."
            : "Tailscale ha risposto con un errore" + (err.Length > 0 ? ": " + err : $" (codice {exitCode}).");
        return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, msg);
    }
}
