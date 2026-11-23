using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Un PC della VPN come lo vede la plancia: gli stessi dati della pagina web <c>/vpn</c> del server.</summary>
public sealed record VpnDashboardDevice(string Name, string Address, bool Connected, DateTimeOffset? LastContact);

/// <summary>
/// Plancia VPN: legge l'elenco dei PC da <c>GET v1/vpn/overview</c> di rdpm-sync con il token dell'account.
/// Nessuna interfaccia qui: la usano sia Windows sia Linux.
/// </summary>
public sealed class VpnDashboardClient : IDisposable
{
    private readonly HttpClient _http;

    public VpnDashboardClient(string baseUrl, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(SyncHttp.NormalizeUrl(baseUrl));
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <exception cref="SyncServerException">401 (<c>unauthorized</c>), 503 <c>vpn_disabled</c> o altro errore del server.</exception>
    /// <exception cref="SyncUnreachableException">Il server non risponde.</exception>
    public async Task<IReadOnlyList<VpnDashboardDevice>> GetDevicesAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/vpn/overview");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var r = await _http.SendAsync(req, ct);
            if (r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                throw new SyncUnreachableException(
                    $"Il server di sincronizzazione è spento o si sta riavviando (errore {(int)r.StatusCode}).", serverDown: true);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
                throw Error(r.StatusCode, body);
            return Parse(body, r.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncUnreachableException("Il server di sincronizzazione non è raggiungibile.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SyncUnreachableException("Il server di sincronizzazione non risponde.", ex);
        }
    }

    internal static SyncServerException Error(HttpStatusCode status, string raw)
    {
        string code = status == HttpStatusCode.Unauthorized ? "unauthorized" : "error";
        string message = status == HttpStatusCode.Unauthorized ? "Accesso scaduto o rifiutato." : $"Errore del server ({(int)status}).";
        try
        {
            var body = JsonDocument.Parse(raw).RootElement;
            if (body.TryGetProperty("error", out var c) && c.GetString() is { } cs)
                code = cs;
            if (body.TryGetProperty("message", out var m) && m.GetString() is { } ms)
                message = ms;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // corpo non JSON: restano i valori generici
        }
        return new SyncServerException(status, code, message);
    }

    private static IReadOnlyList<VpnDashboardDevice> Parse(string raw, HttpStatusCode status)
    {
        try
        {
            var root = JsonDocument.Parse(raw).RootElement;
            var list = new List<VpnDashboardDevice>();
            foreach (var d in root.GetProperty("devices").EnumerateArray())
            {
                DateTimeOffset? last = d.TryGetProperty("lastContact", out var lc) && lc.ValueKind == JsonValueKind.String
                    && lc.TryGetDateTimeOffset(out var t) ? t : null;
                list.Add(new VpnDashboardDevice(
                    d.GetProperty("name").GetString() ?? "",
                    d.GetProperty("address").GetString() ?? "",
                    d.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                    last));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SyncServerException(status, "invalid_response", "Risposta del server non valida.");
        }
    }

    public void Dispose() => _http.Dispose();
}

public enum VpnDashboardStatus { NotSignedIn, Ok, Empty, Unauthorized, Unreachable, Disabled, Error }

/// <summary>Una riga della plancia, già in testo italiano.</summary>
public sealed record VpnDashboardRow(string Name, string Address, bool Connected, string StateText, string LastContactText);

/// <summary>Cosa mostra la finestra: stato, messaggio (vuoto se tutto bene) e righe.</summary>
public sealed record VpnDashboardView(VpnDashboardStatus Status, string Message, IReadOnlyList<VpnDashboardRow> Rows)
{
    /// <summary>Si può aprire il desktop remoto solo da una riga con un IP.</summary>
    public static bool CanOpen(VpnDashboardRow row) => row.Address.Length > 0;
}

/// <summary>Decide cosa mostra la plancia VPN a partire dall'esito della lettura (nessuna interfaccia: si prova da sola).</summary>
public static class VpnDashboardModel
{
    public const int RefreshSeconds = 30;

    public static VpnDashboardView NotSignedIn() => new(VpnDashboardStatus.NotSignedIn,
        "Per vedere i PC della VPN serve l'accesso all'account: apri «Account e sincronizzazione» dal menu Strumenti.", []);

    public static VpnDashboardView FromDevices(IEnumerable<VpnDashboardDevice> devices, DateTimeOffset now)
    {
        var rows = devices
            .OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new VpnDashboardRow(d.Name, StripMask(d.Address), d.Connected,
                d.Connected ? "Collegato" : "Non collegato", LastContactText(d.LastContact, now)))
            .ToList();
        return rows.Count == 0
            ? new VpnDashboardView(VpnDashboardStatus.Empty, "Nessun PC registrato nella VPN.", rows)
            : new VpnDashboardView(VpnDashboardStatus.Ok, "", rows);
    }

    public static VpnDashboardView FromError(Exception ex) => ex switch
    {
        SyncUnreachableException { ServerDown: true } => Fail(VpnDashboardStatus.Unreachable,
            "Il server di sincronizzazione è spento o si sta riavviando. Riprova tra poco."),
        SyncUnreachableException => Fail(VpnDashboardStatus.Unreachable,
            "Il server non risponde. Controlla la connessione (e la VPN) e premi Aggiorna."),
        SyncServerException { Status: HttpStatusCode.Unauthorized } => Fail(VpnDashboardStatus.Unauthorized,
            "L'accesso all'account non è più valido: rifai l'accesso da «Account e sincronizzazione»."),
        SyncServerException { Code: "vpn_disabled" } => Fail(VpnDashboardStatus.Disabled,
            "La VPN automatica non è attiva su questo server."),
        SyncServerException s => Fail(VpnDashboardStatus.Error, "Il server ha risposto con un errore: " + s.Message),
        _ => Fail(VpnDashboardStatus.Error, "Non riesco a leggere i PC della VPN: " + ex.Message),
    };

    private static VpnDashboardView Fail(VpnDashboardStatus status, string message) => new(status, message, []);

    private static string StripMask(string address)
    {
        var slash = address.IndexOf('/');
        return (slash < 0 ? address : address[..slash]).Trim();
    }

    public static string LastContactText(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is not { } t)
            return "mai";
        var age = now - t;
        if (age < TimeSpan.FromMinutes(1))
            return "adesso";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min fa";
        if (age < TimeSpan.FromHours(24))
            return $"{(int)age.TotalHours} h fa";
        return t.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    }
}
