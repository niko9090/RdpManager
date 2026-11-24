using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RdpmSync;

/// <summary>Un PC della VPN: chiave pubblica, indirizzo (10.66.0.N/32) e un'etichetta per il commento in wg0.conf.</summary>
public sealed record WgPeer(string PublicKey, string Address, string Label);

/// <summary>Un peer messo a mano in wg0.conf fuori dal blocco gestito: nome dal commento «# PC:» (altrimenti l'IP).</summary>
public sealed record WgManualPeer(string PublicKey, string Address, string Name);

/// <summary>
/// Come il server comanda WireGuard. Interfaccia astratta: i test usano una copia finta, in produzione
/// <see cref="WgCliPeers"/> riscrive wg0.conf e applica le modifiche al tunnel in esecuzione con <c>wg set</c> (senza riavvio).
/// </summary>
public interface IWireGuardPeers
{
    /// <summary>Indirizzi (es. «10.66.0.5») già in uso fuori dal controllo del server (peer aggiunti a mano).</summary>
    IReadOnlyCollection<string> ReservedAddresses();

    /// <summary>Peer aggiunti a mano fuori dal blocco gestito (con chiave, per leggerne l'handshake).</summary>
    IReadOnlyList<WgManualPeer> ManualPeers() => [];

    /// <summary>Porta il tunnel (e il file di configurazione) esattamente allo stato <paramref name="desired"/>.</summary>
    Task ApplyAsync(IReadOnlyCollection<WgPeer> desired, CancellationToken ct = default);

    /// <summary>Ultimo handshake per chiave pubblica (solo peer che ne hanno avuto uno). Vuoto se il server non può leggerlo.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> HandshakesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());
}

/// <summary>Lettura e scrittura del blocco gestito dal server dentro wg0.conf (il resto del file non si tocca).</summary>
public static partial class WgConfFile
{
    public const string BlockBegin = "# --- rdpm-sync: inizio blocco gestito dal server (non modificare a mano) ---";
    public const string BlockEnd = "# --- rdpm-sync: fine blocco ---";

    /// <summary>Sostituisce (o aggiunge in fondo) il blocco gestito con i peer indicati.</summary>
    public static string Rewrite(string conf, IEnumerable<WgPeer> peers)
    {
        var (before, _, after) = Split(conf);
        var sb = new StringBuilder(before.TrimEnd('\r', '\n'));
        sb.Append("\n\n").Append(BlockBegin).Append('\n');
        foreach (var p in peers)
        {
            sb.Append("\n# PC: ").Append(CleanLabel(p.Label)).Append('\n');
            sb.Append("[Peer]\n");
            sb.Append("PublicKey = ").Append(p.PublicKey).Append('\n');
            sb.Append("AllowedIPs = ").Append(p.Address).Append('\n');
        }
        sb.Append(BlockEnd).Append('\n');
        var rest = after.Trim('\r', '\n');
        if (rest.Length > 0)
            sb.Append('\n').Append(rest).Append('\n');
        return sb.ToString();
    }

    /// <summary>Chiavi pubbliche dei peer nel blocco gestito.</summary>
    public static List<string> ManagedKeys(string conf) =>
        KeyRegex().Matches(Split(conf).Block).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Indirizzi IPv4 (senza /32) dei peer fuori dal blocco gestito, cioè aggiunti a mano.</summary>
    public static List<string> UnmanagedAddresses(string conf)
    {
        var (before, _, after) = Split(conf);
        return AddressRegex().Matches(before + "\n" + after).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>I peer fuori dal blocco gestito, con chiave e indirizzo; il nome viene dal commento «# PC: x» che li precede.</summary>
    public static List<WgManualPeer> UnmanagedPeers(string conf)
    {
        var (before, _, after) = Split(conf);
        var result = new List<WgManualPeer>();
        foreach (var part in new[] { before, after })
        {
            string label = "", key = "", address = "";
            bool inPeer = false;
            void Flush()
            {
                if (inPeer && key.Length > 0 && address.Length > 0)
                    result.Add(new WgManualPeer(key, address, label.Length > 0 ? label : address));
                inPeer = false; key = ""; address = ""; label = "";
            }
            foreach (var raw in part.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('#'))
                {
                    var m = LabelRegex().Match(line);
                    if (m.Success)
                    {
                        Flush();
                        label = m.Groups[1].Value.Trim();
                    }
                }
                else if (line.StartsWith('['))
                {
                    var wasLabel = inPeer ? "" : label; // il commento vale solo per il [Peer] che segue
                    Flush();
                    label = wasLabel;
                    inPeer = line.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase);
                    if (!inPeer)
                        label = "";
                }
                else if (inPeer)
                {
                    var k = KeyRegex().Match(line);
                    if (k.Success)
                        key = k.Groups[1].Value;
                    var a = AddressRegex().Match(line);
                    if (a.Success && address.Length == 0)
                        address = a.Groups[1].Value;
                }
            }
            Flush();
        }
        return result;
    }

    private static (string Before, string Block, string After) Split(string conf)
    {
        conf = conf.Replace("\r\n", "\n");
        int b = conf.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (b < 0)
            return (conf, "", "");
        int e = conf.IndexOf(BlockEnd, b, StringComparison.Ordinal);
        if (e < 0)
            return (conf[..b], conf[b..], ""); // blocco senza fine: lo si considera fino alla fine del file
        int endOfLine = conf.IndexOf('\n', e);
        return (conf[..b], conf[b..e], endOfLine < 0 ? "" : conf[(endOfLine + 1)..]);
    }

    private static string CleanLabel(string label)
    {
        var s = new string(label.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "pc" : s;
    }

    [GeneratedRegex(@"^#\s*PC:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^\s*PublicKey\s*=\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^\s*AllowedIPs\s*=.*?\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/32", RegexOptions.Multiline)]
    private static partial Regex AddressRegex();
}

/// <summary>
/// Implementazione di produzione: scrive il blocco gestito in wg0.conf (così i peer sopravvivono al riavvio del tunnel)
/// e li applica al tunnel in esecuzione con <c>wg set</c>. Non serve NET_ADMIN: <c>wg</c> parla col socket di wireguard-go.
/// </summary>
public sealed class WgCliPeers(string confPath, string iface = "wg0", string wgPath = "wg", TimeSpan? handshakeTimeout = null) : IWireGuardPeers
{
    private readonly TimeSpan _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(5);

    public IReadOnlyList<WgManualPeer> ManualPeers() =>
        File.Exists(confPath) ? WgConfFile.UnmanagedPeers(File.ReadAllText(confPath)) : [];

    public IReadOnlyCollection<string> ReservedAddresses() =>
        File.Exists(confPath) ? WgConfFile.UnmanagedAddresses(File.ReadAllText(confPath)) : [];

    public async Task ApplyAsync(IReadOnlyCollection<WgPeer> desired, CancellationToken ct = default)
    {
        if (!File.Exists(confPath))
            throw new IOException($"Manca {confPath}: il server WireGuard non è ancora stato inizializzato (prima-generazione.sh).");
        var old = File.ReadAllText(confPath);
        var previous = WgConfFile.ManagedKeys(old);

        var tmp = confPath + ".rdpm-tmp";
        File.WriteAllText(tmp, WgConfFile.Rewrite(old, desired));
        File.Copy(confPath, confPath + ".bak", overwrite: true);
        File.Move(tmp, confPath, overwrite: true);

        var keep = desired.Select(p => p.PublicKey).ToHashSet();
        foreach (var gone in previous.Where(k => !keep.Contains(k)))
            await WgAsync(["set", iface, "peer", gone, "remove"], ct);
        foreach (var p in desired)
            await WgAsync(["set", iface, "peer", p.PublicKey, "allowed-ips", p.Address], ct);
    }

    /// <summary>Legge <c>wg show &lt;if&gt; latest-handshakes</c>: righe «chiave pubblica TAB secondi unix» (0 = mai).</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> HandshakesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, DateTimeOffset>();
        try
        {
            var psi = new ProcessStartInfo(wgPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "show", iface, "latest-handshakes" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null)
                return result;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_handshakeTimeout);
            string output;
            try
            {
                // stderr si svuota in parallelo: se si riempie, wg si blocca scrivendo.
                var errTask = p.StandardError.ReadToEndAsync(limit.Token);
                output = await p.StandardOutput.ReadToEndAsync(limit.Token);
                await p.WaitForExitAsync(limit.Token);
                await errTask;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // wg non risponde: si va avanti senza handshake (la pagina usa l'ultimo contatto registrato).
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return result;
            }
            if (p.ExitCode != 0)
                return result;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('\t', ' ');
                if (parts.Length >= 2 && long.TryParse(parts[^1], out var secs) && secs > 0)
                    result[parts[0]] = DateTimeOffset.FromUnixTimeSeconds(secs);
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // wg non disponibile: la pagina userà l'ultimo contatto registrato.
        }
        return result;
    }

    private async Task WgAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(wgPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("Impossibile avviare wg.");
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new IOException($"wg {string.Join(' ', args.Take(2))} è fallito ({p.ExitCode}): {err.Trim()}");
    }
}
