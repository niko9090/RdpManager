using System.Text.RegularExpressions;

namespace RdpmSync;

public sealed record VpnDeviceRequest(string? Name, string? PublicKey);

/// <summary>
/// Dispositivi VPN dell'account: un utente autenticato registra un suo PC (nome + chiave pubblica generata sul PC, la
/// privata non arriva mai qui) e riceve i dati per il profilo WireGuard. Il server assegna il primo 10.66.0.N libero,
/// salva il peer, lo scrive in wg0.conf e lo applica al tunnel in esecuzione.
/// </summary>
public sealed partial class VpnEndpoints(SyncOptions options, Store store, IWireGuardPeers? wg, string serverPublicKey, ILogger log)
{
    public const string Network = "10.66.0.0/24";
    public const int Keepalive = 25;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Enabled => wg is not null && serverPublicKey.Length > 0;

    public static bool ValidName(string? n) => n is { Length: >= 1 and <= 64 } && NameRegex().IsMatch(n);

    public static bool ValidKey(string? k)
    {
        if (k is not { Length: 44 } || !KeyRegex().IsMatch(k))
            return false;
        try { return Convert.FromBase64String(k).Length == 32; }
        catch (FormatException) { return false; }
    }

    /// <summary>Primo 10.66.0.N libero da .2 a .254 (il .1 è il server); null se la rete è piena.</summary>
    public static string? NextFree(IEnumerable<string> used)
    {
        var taken = used.ToHashSet();
        for (int n = 2; n <= 254; n++)
            if (!taken.Contains($"10.66.0.{n}"))
                return $"10.66.0.{n}";
        return null;
    }

    private object Profile(VpnDevice d) => new
    {
        name = d.Name,
        address = d.Address + "/32",
        serverPublicKey,
        endpoint = options.VpnEndpoint,
        allowedIPs = Network,
        dns = options.VpnDns,
        persistentKeepalive = Keepalive,
    };

    private IReadOnlyCollection<WgPeer> Desired() =>
        store.ListDevices().Select(d => new WgPeer(d.PublicKey, d.Address + "/32", $"{d.AccountId}-{d.Name}")).ToList();

    /// <summary>Riporta wg0.conf e il tunnel allo stato del database (all'avvio: peer di account eliminati spariscono).</summary>
    public async Task ReconcileAsync()
    {
        if (!Enabled)
            return;
        await _gate.WaitAsync();
        try { await wg!.ApplyAsync(Desired()); }
        finally { _gate.Release(); }
    }

    public void Map(WebApplication app, Func<HttpContext, TokenInfo?> authenticate, Func<HttpContext, IResult> unauthorized)
    {
        IResult? Gate(HttpContext ctx, out TokenInfo t)
        {
            t = null!;
            if (authenticate(ctx) is not { } info)
                return unauthorized(ctx);
            t = info;
            if (!Enabled)
                return SyncApp.Err(503, "vpn_disabled", "La VPN automatica non è attiva su questo server.");
            return null;
        }

        app.MapGet("/v1/vpn/devices", (HttpContext ctx) =>
            Gate(ctx, out var t) ?? Results.Json(store.ListDevices(t.AccountId).Select(d => new
            {
                name = d.Name, address = d.Address + "/32", publicKey = d.PublicKey, createdUtc = d.CreatedUtc,
            })));

        app.MapPost("/v1/vpn/devices", async (HttpContext ctx, VpnDeviceRequest? body) =>
        {
            if (Gate(ctx, out var t) is { } denied)
                return denied;
            var name = body?.Name?.Trim();
            if (!ValidName(name))
                return SyncApp.Err(400, "invalid_name", "Nome del PC: da 1 a 64 caratteri tra lettere, cifre, spazio e . _ -");
            var key = body?.PublicKey?.Trim();
            if (!ValidKey(key))
                return SyncApp.Err(400, "invalid_public_key", "Chiave pubblica WireGuard non valida (base64 di 32 byte).");

            await _gate.WaitAsync();
            try
            {
                // Stessa chiave già registrata: se è di questo account è lo stesso PC (idempotente), altrimenti è di altri.
                if (store.FindDeviceByKey(key!) is { } byKey)
                    return byKey.AccountId == t.AccountId
                        ? Results.Json(Profile(byKey))
                        : SyncApp.Err(409, "key_in_use", "Questa chiave pubblica è già registrata.");

                if (store.FindDevice(t.AccountId, name!) is { } existing)
                {
                    // Stesso nome, chiave nuova (PC reinstallato): stesso indirizzo, chiave sostituita.
                    var old = existing;
                    store.ReplaceDeviceKey(existing.Id, key!);
                    try { await wg!.ApplyAsync(Desired()); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        store.ReplaceDeviceKey(existing.Id, old.PublicKey);
                        log.LogError(ex, "VPN: chiave non sostituita per {User}/{Name}", t.Username, name);
                        return SyncApp.Err(503, "vpn_unavailable", "Il server WireGuard non risponde: riprova fra poco.");
                    }
                    log.LogInformation("VPN: chiave sostituita per {User}/{Name} ({Address})", t.Username, name, existing.Address);
                    return Results.Json(Profile(existing with { PublicKey = key! }));
                }

                if (store.ListDevices(t.AccountId).Count >= options.MaxDevicesPerAccount)
                    return SyncApp.Err(409, "too_many_devices", $"Hai già {options.MaxDevicesPerAccount} PC nella VPN: togline uno.");

                IEnumerable<string> used;
                try { used = store.ListDevices().Select(d => d.Address).Concat(wg!.ReservedAddresses()).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogError(ex, "VPN: wg0.conf non leggibile per {User}/{Name}", t.Username, name);
                    return SyncApp.Err(503, "vpn_unavailable", "Il server WireGuard non risponde: riprova fra poco.");
                }
                if (NextFree(used) is not { } ip)
                    return SyncApp.Err(409, "network_full", "La rete VPN è piena.");

                store.InsertDevice(t.AccountId, name!, key!, ip);
                var created = store.FindDevice(t.AccountId, name!)!;
                try { await wg.ApplyAsync(Desired()); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    store.DeleteDevice(created.Id);
                    log.LogError(ex, "VPN: peer non applicato per {User}/{Name}", t.Username, name);
                    try { await wg.ApplyAsync(Desired()); } catch (Exception) { /* verrà riallineato all'avvio */ }
                    return SyncApp.Err(503, "vpn_unavailable", "Il server WireGuard non risponde: riprova fra poco.");
                }
                log.LogInformation("VPN: aggiunto {User}/{Name} con {Address}", t.Username, name, ip);
                return Results.Json(Profile(created), statusCode: 201);
            }
            finally
            {
                _gate.Release();
            }
        });

        app.MapDelete("/v1/vpn/devices/{name}", async (HttpContext ctx, string name) =>
        {
            if (Gate(ctx, out var t) is { } denied)
                return denied;
            await _gate.WaitAsync();
            try
            {
                // Si cerca solo tra i dispositivi di chi chiama: quelli degli altri sono «inesistenti».
                if (store.FindDevice(t.AccountId, name) is not { } d)
                    return SyncApp.Err(404, "not_found", "Nessun PC con questo nome nel tuo account.");
                store.DeleteDevice(d.Id);
                try { await wg!.ApplyAsync(Desired()); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    log.LogError(ex, "VPN: peer {User}/{Name} tolto dal database ma non dal tunnel", t.Username, name);
                }
                log.LogInformation("VPN: tolto {User}/{Name}", t.Username, name);
                return Results.NoContent();
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Toglie i peer di un account in via di eliminazione (prima che il database li cancelli a cascata).</summary>
    public async Task ForgetAccountAsync(string username)
    {
        if (!Enabled || store.FindAccount(username) is not { } a)
            return;
        await _gate.WaitAsync();
        try
        {
            foreach (var d in store.ListDevices(a.Id))
                store.DeleteDevice(d.Id);
            try { await wg!.ApplyAsync(Desired()); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                log.LogError(ex, "VPN: peer di {User} non tolti dal tunnel", username);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._ -]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9+/]{43}=$")]
    private static partial Regex KeyRegex();
}
