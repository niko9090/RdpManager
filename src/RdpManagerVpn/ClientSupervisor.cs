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
    Func<ClientSettings> loadSettings, Func<DateTime> now, Action<string> log)
{
    private DateTime _startedUtc;
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
                runner.Start(profile.Config!, ClientPaths.AdapterName);
                _startedUtc = t;
                LastStatus = null;
                log("Tunnel acceso.");
            }
            catch (Exception ex)
            {
                log($"Accensione non riuscita: {ex.Message}");
                _nextAttemptUtc = t + ServicePolicy.RetryDelay;
                return settings.RiconnettiDaSolo;
            }
            return true;
        }

        LastStatus = runner.ReadStatus();
        if (ServicePolicy.NeedsReconnect(settings, LastStatus, _startedUtc, t))
        {
            log("Il tunnel non risponde: riconnessione.");
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
