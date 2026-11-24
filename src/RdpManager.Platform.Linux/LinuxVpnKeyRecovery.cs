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
        if (await vpn.LatestHandshakeAsync(ct).ConfigureAwait(false) is not { } last)
            return ""; // non leggibile: non si può dire che manchi l'handshake
        var lastUtc = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).UtcDateTime : (DateTime?)null;
        if (lastUtc is { } l && now - l < StaleAfter)
        {
            _upSince = now; // il tunnel parla col server: il conto della pazienza riparte
            return "";
        }
        if (now - _upSince.Value < Grace || !_watchdog.TryBegin(true))
            return "";

        var result = await VpnKeySync.ReRegisterAsync(profile, register, ct).ConfigureAwait(false);
        return "VPN senza handshake: " + result.Message;
    }
}
