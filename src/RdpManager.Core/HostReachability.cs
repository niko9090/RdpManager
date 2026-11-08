using System.Diagnostics;
using System.Net.Sockets;

namespace RdpManager.Core;

public enum ReachabilityStatus
{
    /// <summary>Controllo non ancora fatto o in corso.</summary>
    Unknown,
    /// <summary>La porta accetta connessioni TCP.</summary>
    Reachable,
    /// <summary>Connessione rifiutata, host non trovato o tempo scaduto.</summary>
    Unreachable,
}

public sealed record ReachabilityResult(ReachabilityStatus Status, TimeSpan Elapsed, string? Error = null)
{
    public bool IsReachable => Status == ReachabilityStatus.Reachable;
}

/// <summary>Raggiungibilità di una connessione: apertura di una connessione TCP sulla porta RDP, con timeout.</summary>
public static class HostReachability
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public static Task<ReachabilityResult> CheckAsync(RdpConnection c, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        CheckAsync(c.Host, c.Port, timeout, cancellationToken);

    /// <summary>Non lancia eccezioni per errori di rete: li riporta come <see cref="ReachabilityStatus.Unreachable"/>.</summary>
    /// <exception cref="OperationCanceledException">Annullato da <paramref name="cancellationToken"/>.</exception>
    public static async Task<ReachabilityResult> CheckAsync(string host, int port, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            return new ReachabilityResult(ReachabilityStatus.Unreachable, sw.Elapsed, "Host o porta non validi");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host.Trim(), port, timeoutCts.Token).ConfigureAwait(false);
            return new ReachabilityResult(ReachabilityStatus.Reachable, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReachabilityResult(ReachabilityStatus.Unreachable, sw.Elapsed, "Tempo scaduto");
        }
        catch (SocketException ex)
        {
            return new ReachabilityResult(ReachabilityStatus.Unreachable, sw.Elapsed, ex.SocketErrorCode.ToString());
        }
    }
}
