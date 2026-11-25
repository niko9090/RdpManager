using System.Net;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Il ciclo del servizio VPN di RdpManager, un passo alla volta (<see cref="Tick"/> ogni due secondi): accende il tunnel,
/// ne rilegge lo stato e lo ricrea quando non risponde più.
/// <para>
/// Perché ricrearlo: WireGuard da solo ritenta l'handshake, ma sempre dalla stessa porta e verso lo stesso IP risolto
/// all'accensione. Se il router perde la strada di ritorno (riavvio, cambio di IP, hairpin NAT che si inceppa) le risposte
/// del server non arrivano più e il tunnel resta morto per sempre. Ricrearlo dà una porta nuova, risolve di nuovo il nome
/// e sceglie di nuovo l'endpoint (LAN o pubblico).
/// </para>
/// </summary>
public sealed class VpnServiceLoop(VpnTunnelRunner runner, Func<WireGuardConfig> loadConfig, string adapterName,
    Func<DateTime> now, Func<IReadOnlyList<IPAddress>>? localAddresses = null, Action<string>? log = null)
{
    /// <summary>Senza nessun handshake dall'accensione, oltre questo tempo si ricrea il tunnel (o si prova l'altro endpoint).</summary>
    public static readonly TimeSpan FirstHandshakeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Con keepalive WireGuard rinnova l'handshake circa ogni 2 minuti e dopo 3 le chiavi non valgono più:
    /// un handshake più vecchio di così vuol dire tunnel morto.
    /// </summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(200);

    /// <summary>Dopo un'accensione fallita (rete non pronta, nome non risolto) si riprova dopo questo tempo.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private DateTime _startedUtc;      // prima accensione della sessione (per lo stato mostrato)
    private DateTime _attemptUtc;      // accensione del tunnel attuale
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private bool? _useLan;
    private bool _answered;            // il tunnel attuale ha avuto almeno un handshake
    private bool _sessionStarted;

    public VpnStatus? LastStatus { get; private set; }

    /// <summary>Quante volte il tunnel è stato ricreato perché non rispondeva (per le prove e il registro).</summary>
    public int Restarts { get; private set; }

    public void Tick()
    {
        var t = now();
        if (!runner.IsRunning)
        {
            if (t >= _nextAttemptUtc)
                TryStart(t);
            return;
        }

        var status = runner.ReadStatus();
        LastStatus = status is null ? null : status with { StartedUtc = _startedUtc };
        if (status?.LastHandshakeUtc is { } hs)
        {
            _answered = true;
            if (t - hs <= HandshakeTimeout)
                return;
            Restart(t, $"Ultimo handshake {(int)(t - hs).TotalSeconds} s fa: tunnel ricreato.", chooseAgain: true);
            return;
        }
        if (status is null)
        {
            Restart(t, "Il tunnel non risponde: ricreato.", chooseAgain: true);
            return;
        }

        var cfg = SafeConfig();
        bool hasAlternative = cfg is not null && cfg.LanEndpoint.Length > 0;
        if (hasAlternative && !_answered && t - _attemptUtc > VpnEndpointChoice.SwitchTimeout)
        {
            // Nessun contatto: tipico dell'hairpin NAT mancante (dalla LAN il nome pubblico non risponde); si prova l'altro.
            _useLan = !(_useLan ?? false);
            Restart(t, $"Nessun contatto col server: provo l'endpoint {(_useLan.Value ? "LAN" : "pubblico")}.", chooseAgain: false);
            return;
        }
        if (!hasAlternative && t - _attemptUtc > FirstHandshakeTimeout)
            Restart(t, "Nessun handshake dall'accensione: tunnel ricreato.", chooseAgain: true);
    }

    public void Stop()
    {
        runner.Stop();
        LastStatus = null;
    }

    private WireGuardConfig? SafeConfig()
    {
        try
        {
            return loadConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private void TryStart(DateTime t)
    {
        try
        {
            var cfg = loadConfig();
            bool hasAlternative = cfg.LanEndpoint.Length > 0;
            _useLan ??= hasAlternative && VpnEndpointChoice.OnServerLan(cfg.LanEndpoint, (localAddresses ?? VpnEndpointChoice.LocalAddresses)());
            runner.Start(VpnEndpointChoice.Apply(cfg, _useLan.Value), adapterName);
            _attemptUtc = t;
            _answered = false;
            if (!_sessionStarted)
            {
                _startedUtc = t;
                _sessionStarted = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Rete non ancora pronta, nome non risolto, adattatore occupato: si riprova, il servizio non si ferma.
            log?.Invoke("Accensione non riuscita, riprovo fra " + (int)RetryDelay.TotalSeconds + " s: " + ex.Message);
            runner.Stop();
            _nextAttemptUtc = t + RetryDelay;
        }
    }

    private void Restart(DateTime t, string reason, bool chooseAgain)
    {
        log?.Invoke(reason);
        Restarts++;
        if (chooseAgain)
            _useLan = null; // la rete può essere cambiata (portatile): si sceglie di nuovo l'endpoint
        runner.Stop();
        LastStatus = null;
        TryStart(t);
    }
}
