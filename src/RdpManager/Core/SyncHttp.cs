using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Il server ha risposto con un errore del contratto (<c>Code</c> è il codice, mai il testo, a decidere).</summary>
public sealed class SyncServerException(HttpStatusCode status, string code, string message, int? retryAfterSeconds = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Il server non risponde (VPN spenta, indirizzo sbagliato, tempo scaduto).</summary>
public sealed class SyncUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

public enum RemoteDocumentKind { Missing, NotModified, Found }

public sealed record RemoteDocument(RemoteDocumentKind Kind, byte[]? Blob, string? Etag);

/// <param name="Conflict">Vero se la versione è cambiata: nulla è stato scritto.</param>
public sealed record PutOutcome(bool Conflict, string? Etag);

/// <summary>Chiamate HTTP del contratto v1 (docs/sync-server-api.md), senza logica di sincronizzazione.</summary>
public sealed class SyncHttp : IDisposable
{
    /// <summary>Indirizzo predefinito: il server sul Synology, visto dalla VPN WireGuard (porta 8787 di rdpm-sync).</summary>
    public const string DefaultServerUrl = "http://10.66.0.1:8787";

    /// <summary>
    /// Indirizzo pubblico del server (HTTPS, dietro il proxy inverso del Synology): serve a chi non ha ancora la VPN,
    /// cioè alla registrazione e alla richiesta del primo profilo VPN.
    /// </summary>
    public const string PublicServerUrl = "https://syncnetpro.com:8443";

    private readonly HttpClient _http;

    public SyncHttp(string baseUrl, TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(NormalizeUrl(baseUrl)), Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    /// <summary>Aggiunge «http://» se manca e toglie la barra finale. Lancia ArgumentException se non è un indirizzo.</summary>
    public static string NormalizeUrl(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException("Indica l'indirizzo del server.");
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            throw new ArgumentException("L'indirizzo del server non è valido (esempio: http://10.66.0.1:8787).");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
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

    private static async Task<SyncServerException> ErrorAsync(HttpResponseMessage r, CancellationToken ct)
    {
        string code = "error", message = $"Errore del server ({(int)r.StatusCode}).";
        int? retry = null;
        try
        {
            var body = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.TryGetProperty("error", out var c) && c.GetString() is { } cs)
                code = cs;
            if (body.TryGetProperty("message", out var m) && m.GetString() is { } ms)
                message = ms;
            if (body.TryGetProperty("retryAfterSeconds", out var ra) && ra.ValueKind == JsonValueKind.Number)
                retry = ra.GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // corpo non JSON: resta il messaggio generico
        }
        return new SyncServerException(r.StatusCode, code, message, retry);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var r = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "health"), ct);
        return r.IsSuccessStatusCode;
    }

    /// <summary>Crea l'account con la chiave di amministrazione (<c>X-Admin-Key</c>).</summary>
    public async Task CreateAccountAsync(string adminKey, string username, string authSecret, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/admin/accounts")
        {
            Content = JsonContent.Create(new { username, password = authSecret }),
        };
        req.Headers.Add("X-Admin-Key", adminKey);
        using var r = await SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw await ErrorAsync(r, ct);
    }

    /// <summary>Registra un account dall'app (senza chiave di amministrazione); il codice solo se il server lo chiede.</summary>
    public async Task RegisterAsync(string username, string authSecret, string? registrationCode, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/register")
        {
            Content = JsonContent.Create(new { username, password = authSecret, registrationCode }),
        };
        using var r = await SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw await ErrorAsync(r, ct);
    }

    /// <summary>Chiede la VPN per questo PC: manda nome e chiave PUBBLICA; stesso PC = stessa risposta.</summary>
    public async Task<VpnGrant> RequestVpnDeviceAsync(string token, string pcName, string publicKey, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Post, "v1/vpn/devices", token);
        req.Content = JsonContent.Create(new { name = pcName, publicKey });
        using var r = await SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw await ErrorAsync(r, ct);
        return VpnGrant.Parse(await r.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    /// <summary>Toglie un PC dall'account (e dal tunnel del server). 404 = già tolto.</summary>
    public async Task RemoveVpnDeviceAsync(string token, string pcName, CancellationToken ct = default)
    {
        using var r = await SendAsync(Authorized(HttpMethod.Delete, "v1/vpn/devices/" + Uri.EscapeDataString(pcName), token), ct);
        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.NotFound)
            throw await ErrorAsync(r, ct);
    }

    public async Task<string> LoginAsync(string username, string authSecret, string deviceName, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/login")
        {
            Content = JsonContent.Create(new { username, password = authSecret, deviceName }),
        };
        using var r = await SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw await ErrorAsync(r, ct);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("token").GetString()
               ?? throw new SyncServerException(r.StatusCode, "invalid_response", "Risposta del server non valida.");
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        using var r = await SendAsync(Authorized(HttpMethod.Post, "v1/logout", token), ct);
        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.Unauthorized)
            throw await ErrorAsync(r, ct);
    }

    public async Task<RemoteDocument> GetDocumentAsync(string token, string? ifNoneMatch, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Get, "v1/document", token);
        if (ifNoneMatch is not null)
            req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var r = await SendAsync(req, ct);
        switch (r.StatusCode)
        {
            case HttpStatusCode.NotModified:
                return new RemoteDocument(RemoteDocumentKind.NotModified, null, ifNoneMatch);
            case HttpStatusCode.OK:
                return new RemoteDocument(RemoteDocumentKind.Found, await r.Content.ReadAsByteArrayAsync(ct), r.Headers.ETag?.ToString());
            default:
                var error = await ErrorAsync(r, ct);
                if (error.Code == "no_document")
                    return new RemoteDocument(RemoteDocumentKind.Missing, null, null);
                throw error;
        }
    }

    /// <param name="ifMatch">ETag letto per ultimo; null = primo salvataggio (<c>If-None-Match: *</c>).</param>
    public async Task<PutOutcome> PutDocumentAsync(string token, byte[] blob, string? ifMatch, CancellationToken ct = default)
    {
        using var req = Authorized(HttpMethod.Put, "v1/document", token);
        req.Content = new ByteArrayContent(blob);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Headers.TryAddWithoutValidation(ifMatch is null ? "If-None-Match" : "If-Match", ifMatch ?? "*");
        using var r = await SendAsync(req, ct);
        if (r.StatusCode == HttpStatusCode.Conflict)
            return new PutOutcome(true, null);
        if (!r.IsSuccessStatusCode)
            throw await ErrorAsync(r, ct);
        return new PutOutcome(false, r.Headers.ETag?.ToString());
    }

    public void Dispose() => _http.Dispose();
}
