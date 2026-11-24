using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>
/// Recupero automatico (k-177, come su Windows): tunnel su ma senza handshake da <see cref="Grace"/> (e nessun handshake negli
/// ultimi <see cref="StaleAfter"/>), account attivo e PC già registrato → si rimanda UNA volta al server la chiave pubblica
/// attuale con <see cref="VpnKeySync.ReRegisterAsync"/>. Nessuna chiave nuova; mai chiavi o codici nel testo restituito.
/// </summary>
public sealed class LinuxVpnKeyRecovery(LinuxWireGuard vpn, Func<VpnProfile> loadProfile, Func<bool> signedIn, VpnRegistrar register,
    Func<DateTime>? utcNow = null, VpnKeyWatchdog? watchdog = null)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Quanto aspettare, con l'handshake illeggibile (niente root), prima di ri-registrare comunque.</summary>
    public static readonly TimeSpan UnreadablePatience = Grace + StaleAfter;

    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private readonly VpnKeyWatchdog _watchdog = watchdog ?? new VpnKeyWatchdog();
    private DateTime? _upSince;

    public int Attempts => _watchdog.Attempts;

    /// <summary>Un giro di controllo. Restituisce una riga per il registro ("" = niente da dire).</summary>
    public async Task<string> TickAsync(CancellationToken ct = default)
    {
        if (!signedIn())
            return "";
        var profile = loadProfile();
        if (!VpnKeySync.IsRegistered(profile) || !await vpn.IsConnectedAsync(ct).ConfigureAwait(false))
        {
            _upSince = null;
            return "";
        }

        var now = _now();
        _upSince ??= now;
        var handshake = await vpn.LatestHandshakeAsync(ct).ConfigureAwait(false);
        if (handshake is null)
        {
            // Senza root «wg show» non si legge mai: tunnel su da oltre la pazienza (Grace + StaleAfter) e handshake sempre
            // illeggibile = lo si tratta come «nessun handshake», altrimenti il recupero non partirebbe mai (un solo tentativo).
            if (now - _upSince.Value < UnreadablePatience || !_watchdog.TryBegin(true))
                return "";
            return "VPN senza handshake leggibile: " + await RecoverAsync(profile, ct).ConfigureAwait(false);
        }
        var last = handshake.Value;
        var lastUtc = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).UtcDateTime : (DateTime?)null;
        if (lastUtc is { } l && now - l < StaleAfter)
        {
            _upSince = now; // il tunnel parla col server: il conto della pazienza riparte
            return "";
        }
        if (now - _upSince.Value < Grace || !_watchdog.TryBegin(true))
            return "";

        return "VPN senza handshake: " + await RecoverAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>Ri-registra la chiave; se il server ha risposto con dati diversi il profilo è già aggiornato e il tunnel va riavviato.</summary>
    private async Task<string> RecoverAsync(VpnProfile profile, CancellationToken ct)
    {
        var result = await VpnKeySync.ReRegisterAsync(profile, register, ct).ConfigureAwait(false);
        if (!result.ProfileChanged)
            return result.Message;
        var r = await vpn.DisconnectAsync(ct).ConfigureAwait(false);
        if (r.Success)
            r = await vpn.ConnectAsync(profile.ToConfig(), ct).ConfigureAwait(false);
        return result.Message + (r.Success ? " VPN ricollegata." : " Ricollegamento non riuscito: " + r.Message);
    }
}
