using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManagerVpn;

/// <summary>Regole del servizio: quando accendere all'avvio e quando il tunnel va considerato caduto.</summary>
public static class ServicePolicy
{
    /// <summary>Argomento con cui l'icona avvia il servizio su richiesta dell'utente («Connetti»).</summary>
    public const string ManualArgument = "--manuale";

    /// <summary>Senza nessun handshake dopo l'accensione, oltre questo tempo il server non risponde.</summary>
    public static readonly TimeSpan FirstHandshakeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Con keepalive di 25 s WireGuard rinnova l'handshake circa ogni 2 minuti: oltre 5 il tunnel è morto.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Senza handshake dopo questo tempo lo stato mostrato diventa «server non raggiungibile» invece di «connessione in corso».</summary>
    public static readonly TimeSpan NoContactGrace = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Il servizio parte con Windows ma accende il tunnel solo se «Avvia con Windows» è sì, o se l'utente l'ha chiesto.</summary>
    public static bool ShouldConnectAtStart(ClientSettings settings, bool manualStart) => manualStart || settings.AvviaConWindows;

    public static bool NeedsReconnect(ClientSettings settings, VpnStatus? status, DateTime startedUtc, DateTime nowUtc)
    {
        if (!settings.RiconnettiDaSolo)
            return false;
        if (status is null) // l'adattatore non risponde più
            return true;
        return status.LastHandshakeUtc is { } hs
            ? nowUtc - hs > HandshakeTimeout
            : nowUtc - startedUtc > FirstHandshakeTimeout;
    }
}

/// <summary>
/// Il ciclo del servizio, un passo alla volta (<see cref="Tick"/> ogni due secondi): accende il tunnel, ne rilegge lo stato
/// e, se «Riconnetti da solo» è attivo, lo ricrea quando cade o quando l'accensione fallisce (rete non ancora pronta all'avvio).
/// </summary>
public sealed class ClientSupervisor(VpnTunnelRunner runner, Func<ClientProfileResult> loadProfile,
    Func<ClientSettings> loadSettings, Func<DateTime> now, Action<string> log,
    Func<IReadOnlyList<System.Net.IPAddress>>? localAddresses = null)
{
    private DateTime _startedUtc;
    private bool? _useLan;      // endpoint in uso (null = ancora da scegliere): ricordato finché il servizio gira
    private bool _lanKnown;     // l'endpoint in uso ha già dato un handshake
    private bool _hasAlternative;
    private DateTime _nextAttemptUtc = DateTime.MinValue;

    public VpnStatus? LastStatus { get; private set; }
    public bool Connected => runner.IsRunning;

    /// <summary>Un passo. Restituisce false se il servizio deve arrendersi (errore e riconnessione disattivata).</summary>
    public bool Tick()
    {
        var settings = loadSettings();
        var t = now();
        if (!runner.IsRunning)
        {
            if (t < _nextAttemptUtc)
                return true;
            var profile = loadProfile();
            if (!profile.Ok)
            {
                log(profile.ErrorText);
                _nextAttemptUtc = t + ServicePolicy.RetryDelay;
                return settings.RiconnettiDaSolo;
            }
            try
            {
                var cfg = profile.Config!;
                _hasAlternative = cfg.LanEndpoint.Length > 0;
                _useLan ??= _hasAlternative && VpnEndpointChoice.OnServerLan(cfg.LanEndpoint, (localAddresses ?? VpnEndpointChoice.LocalAddresses)());
                runner.Start(VpnEndpointChoice.Apply(cfg, _useLan.Value), ClientPaths.AdapterName);
                _startedUtc = t;
                LastStatus = null;
                log(_hasAlternative ? $"Tunnel acceso (endpoint {(_useLan.Value ? "LAN" : "pubblico")})." : "Tunnel acceso.");
            }
            catch (Exception ex)
            {
                log($"Accensione non riuscita: {ex.Message}");
                _nextAttemptUtc = t + ServicePolicy.RetryDelay;
                return settings.RiconnettiDaSolo;
            }
            return true;
        }

        LastStatus = runner.ReadStatus() is { } read ? read with { StartedUtc = _startedUtc } : null;
        if (LastStatus?.LastHandshakeUtc is not null)
            _lanKnown = true;
        else if (_hasAlternative && !_lanKnown && settings.RiconnettiDaSolo && t - _startedUtc > VpnEndpointChoice.SwitchTimeout)
        {
            // senza handshake: tipico dell'hairpin NAT mancante (dalla LAN il nome pubblico non risponde); si prova l'altro endpoint
            var lan = !(_useLan ?? false);
            _useLan = lan;
            log($"Nessun contatto col server: provo l'endpoint {(lan ? "LAN" : "pubblico")}.");
            runner.Stop();
            LastStatus = null;
            _nextAttemptUtc = t;
            return true;
        }
        if (ServicePolicy.NeedsReconnect(settings, LastStatus, _startedUtc, t))
        {
            log("Il tunnel non risponde: riconnessione.");
            _useLan = null; // la rete può essere cambiata (PC portatile): si sceglie di nuovo l'endpoint
            _lanKnown = false;
            runner.Stop();
            LastStatus = null;
            _nextAttemptUtc = t;
        }
        return true;
    }

    public void Shutdown()
    {
        runner.Stop();
        LastStatus = null;
    }
}
