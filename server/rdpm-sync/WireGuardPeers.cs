using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RdpmSync;

/// <summary>Un PC della VPN: chiave pubblica, indirizzo (10.66.0.N/32) e un'etichetta per il commento in wg0.conf.</summary>
public sealed record WgPeer(string PublicKey, string Address, string Label);

/// <summary>
/// Come il server comanda WireGuard. Interfaccia astratta: i test usano una copia finta, in produzione
/// <see cref="WgCliPeers"/> riscrive wg0.conf e applica le modifiche al tunnel in esecuzione con <c>wg set</c> (senza riavvio).
/// </summary>
public interface IWireGuardPeers
{
    /// <summary>Indirizzi (es. «10.66.0.5») già in uso fuori dal controllo del server (peer aggiunti a mano).</summary>
    IReadOnlyCollection<string> ReservedAddresses();

    /// <summary>Porta il tunnel (e il file di configurazione) esattamente allo stato <paramref name="desired"/>.</summary>
    Task ApplyAsync(IReadOnlyCollection<WgPeer> desired, CancellationToken ct = default);
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

    [GeneratedRegex(@"^\s*PublicKey\s*=\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^\s*AllowedIPs\s*=.*?\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/32", RegexOptions.Multiline)]
    private static partial Regex AddressRegex();
}

/// <summary>
/// Implementazione di produzione: scrive il blocco gestito in wg0.conf (così i peer sopravvivono al riavvio del tunnel)
/// e li applica al tunnel in esecuzione con <c>wg set</c>. Non serve NET_ADMIN: <c>wg</c> parla col socket di wireguard-go.
/// </summary>
public sealed class WgCliPeers(string confPath, string iface = "wg0", string wgPath = "wg") : IWireGuardPeers
{
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
