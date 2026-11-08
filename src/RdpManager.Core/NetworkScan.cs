using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RdpManager.Core;

/// <summary>
/// Intervallo di indirizzi IPv4 (estremi compresi), da una sottorete (indirizzo e maschera, CIDR) o scritto a mano
/// («da-a»). Per una sottorete fino a /30 si escludono l'indirizzo di rete e quello di broadcast.
/// </summary>
public readonly record struct IpRange(uint First, uint Last)
{
    public long Count => (long)Last - First + 1;

    public IPAddress FirstAddress => ToAddress(First);
    public IPAddress LastAddress => ToAddress(Last);

    public IEnumerable<IPAddress> Addresses()
    {
        for (long a = First; a <= Last; a++)
            yield return ToAddress((uint)a);
    }

    public bool Contains(IPAddress address) =>
        TryToUInt(address, out var v) && v >= First && v <= Last;

    public override string ToString() => First == Last ? FirstAddress.ToString() : $"{FirstAddress} - {LastAddress}";

    /// <summary>Lunghezza del prefisso più corto che contiene tutto l'intervallo (32 = un indirizzo).</summary>
    public int CoveringPrefix
    {
        get
        {
            int prefix = 32;
            while (prefix > 0 && (First >> (32 - prefix)) != (Last >> (32 - prefix)))
                prefix--;
            return prefix;
        }
    }

    /// <summary>Sottorete <paramref name="address"/>/<paramref name="prefix"/>, senza rete e broadcast se prefix ≤ 30.</summary>
    public static IpRange FromPrefix(IPAddress address, int prefix)
    {
        if (prefix is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefix), "Il prefisso va da 0 a 32.");
        if (!TryToUInt(address, out var ip))
            throw new ArgumentException("Serve un indirizzo IPv4.", nameof(address));
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint network = ip & mask, broadcast = network | ~mask;
        return prefix <= 30 ? new IpRange(network + 1, broadcast - 1) : new IpRange(network, broadcast);
    }

    /// <summary>Sottorete di un indirizzo con la sua maschera (es. 255.255.255.0).</summary>
    public static IpRange FromMask(IPAddress address, IPAddress mask)
    {
        if (!TryMaskToPrefix(mask, out var prefix))
            throw new ArgumentException("Maschera di sottorete non valida.", nameof(mask));
        return FromPrefix(address, prefix);
    }

    /// <summary>Prefisso di una maschera contigua (255.255.252.0 → 22); falso per maschere non valide.</summary>
    public static bool TryMaskToPrefix(IPAddress? mask, out int prefix)
    {
        prefix = 0;
        if (mask is null || !TryToUInt(mask, out var m))
            return false;
        while (prefix < 32 && (m & (0x80000000u >> prefix)) != 0)
            prefix++;
        uint expected = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return m == expected;
    }

    /// <summary>
    /// Legge un intervallo scritto a mano: «192.168.1.0/24», «192.168.1.10-192.168.1.50», «192.168.1.10-50»
    /// (solo l'ultimo numero), oppure un solo indirizzo. Gli spazi sono ammessi.
    /// </summary>
    public static bool TryParse(string? text, out IpRange range, out string error)
    {
        range = default;
        error = "";
        var t = (text ?? "").Replace(" ", "").Trim();
        if (t.Length == 0)
        {
            error = "Scrivi un intervallo, ad esempio 192.168.1.0/24 oppure 192.168.1.10-192.168.1.50.";
            return false;
        }
        int slash = t.IndexOf('/');
        if (slash >= 0)
        {
            if (!TryParseIPv4(t[..slash], out var ip))
            {
                error = $"«{t[..slash]}» non è un indirizzo IPv4.";
                return false;
            }
            var p = t[(slash + 1)..];
            if (int.TryParse(p, out var prefix) && prefix is >= 0 and <= 32)
            {
                range = FromPrefix(ip, prefix);
                return true;
            }
            if (TryParseIPv4(p, out var mask) && TryMaskToPrefix(mask, out prefix))
            {
                range = FromPrefix(ip, prefix);
                return true;
            }
            error = $"«/{p}» non è un prefisso (0-32) né una maschera valida.";
            return false;
        }
        int dash = t.IndexOf('-');
        if (dash >= 0)
        {
            var from = t[..dash];
            var to = t[(dash + 1)..];
            if (!TryParseIPv4(from, out var a))
            {
                error = $"«{from}» non è un indirizzo IPv4.";
                return false;
            }
            IPAddress? b;
            if (byte.TryParse(to, out var last) && !to.Contains('.'))
            {
                var bytes = a.GetAddressBytes();
                bytes[3] = last;
                b = new IPAddress(bytes);
            }
            else if (!TryParseIPv4(to, out b))
            {
                error = $"«{to}» non è un indirizzo IPv4.";
                return false;
            }
            uint x = ToUInt(a), y = ToUInt(b!);
            if (y < x)
            {
                error = "L'indirizzo finale viene prima di quello iniziale.";
                return false;
            }
            range = new IpRange(x, y);
            return true;
        }
        if (TryParseIPv4(t, out var single))
        {
            range = new IpRange(ToUInt(single), ToUInt(single));
            return true;
        }
        error = $"«{t}» non è un intervallo valido (es. 192.168.1.0/24 oppure 192.168.1.10-192.168.1.50).";
        return false;
    }

    /// <summary>Solo IPv4 scritti per intero con quattro numeri (IPAddress.TryParse accetterebbe anche «10.1»).</summary>
    public static bool TryParseIPv4(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = (text ?? "").Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit)))
            return false;
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out bytes[i]))
                return false;
        }
        address = new IPAddress(bytes);
        return true;
    }

    public static bool TryToUInt(IPAddress address, out uint value)
    {
        value = 0;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        value = ToUInt(address);
        return true;
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    public static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

/// <summary>Tipo di indirizzo IPv4, per decidere se uno scanner può interrogarlo senza chiedere.</summary>
public enum AddressScope
{
    /// <summary>Reti private RFC 1918: 10/8, 172.16/12, 192.168/16.</summary>
    Private,
    /// <summary>Spazio condiviso RFC 6598 (100.64/10), quello di Tailscale e di molti operatori.</summary>
    Cgnat,
    /// <summary>Indirizzi automatici 169.254/16.</summary>
    LinkLocal,
    /// <summary>127/8: questo PC.</summary>
    Loopback,
    /// <summary>Tutto il resto: indirizzi pubblici di Internet (o riservati).</summary>
    Public,
}

public static class AddressScopes
{
    public static AddressScope Of(IPAddress address)
    {
        if (!IpRange.TryToUInt(address, out var v))
            return AddressScope.Public;
        byte a = (byte)(v >> 24), b = (byte)(v >> 16);
        return a switch
        {
            10 => AddressScope.Private,
            172 when b is >= 16 and <= 31 => AddressScope.Private,
            192 when b == 168 => AddressScope.Private,
            100 when b is >= 64 and <= 127 => AddressScope.Cgnat,
            169 when b == 254 => AddressScope.LinkLocal,
            127 => AddressScope.Loopback,
            _ => AddressScope.Public,
        };
    }

    /// <summary>Indirizzi che si possono interrogare senza conferma: privati, CGNAT, link-local e questo PC.</summary>
    public static bool IsScannableWithoutConfirmation(IPAddress address) => Of(address) != AddressScope.Public;

    /// <summary>Quanti indirizzi dell'intervallo non sono privati, CGNAT, link-local o di questo PC.</summary>
    public static long CountOutsidePrivate(IpRange range)
    {
        // Ogni blocco ammesso è allineato a /8 o più piccolo: si contano le intersezioni con i blocchi noti.
        (uint First, uint Last)[] allowed =
        [
            (0x0A000000, 0x0AFFFFFF), (0xAC100000, 0xAC1FFFFF), (0xC0A80000, 0xC0A8FFFF),
            (0x64400000, 0x647FFFFF), (0xA9FE0000, 0xA9FEFFFF), (0x7F000000, 0x7FFFFFFF),
        ];
        long inside = 0;
        foreach (var (f, l) in allowed)
        {
            long lo = Math.Max(f, range.First), hi = Math.Min(l, range.Last);
            if (hi >= lo)
                inside += hi - lo + 1;
        }
        return range.Count - inside;
    }
}

/// <summary>Cosa manca perché una scansione possa partire.</summary>
public enum ScanCheck
{
    Ok,
    /// <summary>Intervallo più grande di una /22: serve la conferma dell'utente.</summary>
    NeedsSizeConfirmation,
    /// <summary>L'intervallo comprende indirizzi pubblici: serve la conferma dell'utente.</summary>
    NeedsPublicConfirmation,
    /// <summary>Oltre <see cref="ScanPolicy.HardLimit"/>: non si fa nemmeno con la conferma.</summary>
    TooLarge,
}

/// <summary>Regole della scansione: dimensione massima senza conferma (/22) e indirizzi ammessi.</summary>
public static class ScanPolicy
{
    /// <summary>Prefisso più largo ammesso senza conferma.</summary>
    public const int MaxPrefixWithoutConfirmation = 22;

    /// <summary>Indirizzi di una /22 (1024): oltre si chiede conferma.</summary>
    public const long MaxAddressesWithoutConfirmation = 1L << (32 - MaxPrefixWithoutConfirmation);

    /// <summary>Limite assoluto (una /16): una scansione più grande non ha senso da un'app desktop.</summary>
    public const long HardLimit = 1L << 16;

    public static ScanCheck Check(IpRange range, bool sizeConfirmed = false, bool publicConfirmed = false)
    {
        if (range.Count > HardLimit)
            return ScanCheck.TooLarge;
        if (range.Count > MaxAddressesWithoutConfirmation && !sizeConfirmed)
            return ScanCheck.NeedsSizeConfirmation;
        if (AddressScopes.CountOutsidePrivate(range) > 0 && !publicConfirmed)
            return ScanCheck.NeedsPublicConfirmation;
        return ScanCheck.Ok;
    }

    /// <summary>Messaggio per l'utente (domanda di conferma o motivo del rifiuto); vuoto se va bene.</summary>
    public static string Describe(ScanCheck check, IpRange range) => check switch
    {
        ScanCheck.NeedsSizeConfirmation =>
            $"L'intervallo {range} contiene {range.Count:N0} indirizzi, più di una rete /{MaxPrefixWithoutConfirmation} " +
            $"({MaxAddressesWithoutConfirmation:N0}). La ricerca durerà di più e genera più traffico. Continuare?",
        ScanCheck.NeedsPublicConfirmation =>
            $"L'intervallo {range} comprende indirizzi pubblici di Internet ({AddressScopes.CountOutsidePrivate(range):N0}). " +
            "Controlla solo reti che ti appartengono o che hai il permesso di controllare. Continuare?",
        ScanCheck.TooLarge =>
            $"L'intervallo {range} contiene {range.Count:N0} indirizzi: il massimo è {HardLimit:N0} (una rete /16).",
        _ => "",
    };
}

/// <summary>Un PC che ha risposto sulla porta RDP.</summary>
public sealed record ScanHit(IPAddress Address, int Port, string? HostName, TimeSpan Elapsed);

public sealed record ScanProgress(int Done, int Total, int Found);

/// <summary>Opzioni della scansione: concorrenza limitata, timeout per indirizzo, nomi con il DNS inverso.</summary>
public sealed record ScanOptions
{
    public int Port { get; init; } = RdpConnection.DefaultPort;
    public int MaxConcurrency { get; init; } = 64;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(600);
    public bool ResolveNames { get; init; } = true;
    public TimeSpan NameTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Scanner TCP della porta RDP. Lo avvia solo l'utente (mai in automatico): per ogni indirizzo apre una connessione
/// TCP con timeout e la chiude subito, senza inviare dati; al massimo <see cref="ScanOptions.MaxConcurrency"/>
/// tentativi insieme. Le regole su dimensione e indirizzi ammessi stanno in <see cref="ScanPolicy"/>.
/// </summary>
public sealed class NetworkScanner
{
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _reverseDns;
    private readonly Func<IPAddress, int, TimeSpan, CancellationToken, Task<bool>> _probe;

    /// <param name="reverseDns">Nome di un indirizzo (predefinito: DNS inverso del sistema).</param>
    /// <param name="probe">Prova di una porta (predefinito: connessione TCP con <see cref="HostReachability"/>).</param>
    public NetworkScanner(Func<IPAddress, CancellationToken, Task<string?>>? reverseDns = null,
        Func<IPAddress, int, TimeSpan, CancellationToken, Task<bool>>? probe = null)
    {
        _reverseDns = reverseDns ?? ReverseLookupAsync;
        _probe = probe ?? TcpProbeAsync;
    }

    private static async Task<bool> TcpProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await HostReachability.CheckAsync(address.ToString(), port, timeout, cancellationToken).ConfigureAwait(false)).IsReachable;

    /// <summary>Trovati in ordine di indirizzo. Annullabile: <see cref="OperationCanceledException"/>.</summary>
    public async Task<IReadOnlyList<ScanHit>> ScanAsync(IEnumerable<IPAddress> addresses, ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        if (options.Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Porta non valida.");
        var list = addresses.Distinct().ToList();
        int concurrency = Math.Clamp(options.MaxConcurrency, 1, 256);
        using var gate = new SemaphoreSlim(concurrency);
        var hits = new List<ScanHit>();
        int done = 0;
        var tasks = list.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                if (await _probe(address, options.Port, options.Timeout, cancellationToken).ConfigureAwait(false))
                {
                    string? name = options.ResolveNames ? await ResolveAsync(address, options.NameTimeout, cancellationToken).ConfigureAwait(false) : null;
                    lock (hits)
                        hits.Add(new ScanHit(address, options.Port, name, sw.Elapsed));
                }
            }
            finally
            {
                gate.Release();
                int n = Interlocked.Increment(ref done);
                int found;
                lock (hits)
                    found = hits.Count;
                progress?.Report(new ScanProgress(n, list.Count, found));
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return hits.OrderBy(h => IpRange.TryToUInt(h.Address, out var v) ? v : uint.MaxValue).ToList();
    }

    public Task<IReadOnlyList<ScanHit>> ScanAsync(IpRange range, ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ScanAsync(range.Addresses(), options, progress, cancellationToken);

    private async Task<string?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var name = await _reverseDns(address, cts.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(name) || name == address.ToString() ? null : name.Trim().TrimEnd('.');
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
        return entry.HostName;
    }
}
