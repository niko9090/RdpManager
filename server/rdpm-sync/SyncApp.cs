using System.Security.Cryptography;
using System.Text;

namespace RdpmSync;

public sealed record ErrorBody(string Error, string Message, string? CurrentEtag = null, int? RetryAfterSeconds = null);

public sealed record CredentialsRequest(string? Username, string? Password, string? DeviceName);

/// <summary>Costruisce l'applicazione web (minimal API). Contratto: docs/sync-server-api.md.</summary>
public static class SyncApp
{
    public const string Version = "1.0.0";
    private const int MinSecretLength = 16, MaxSecretLength = 256;

    public static WebApplication Build(SyncOptions options, TimeProvider? time = null, string? url = null)
    {
        time ??= TimeProvider.System;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url ?? $"http://{options.ListenAddress}:{options.Port}");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.MaxDocumentBytes + 4096;
        });
        // Log senza dati sensibili: niente log delle richieste (contengono l'intestazione Authorization nei dettagli).
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        var store = new Store(options.DatabasePath, time);
        var byIpUser = new FailureLimiter(time, options.MaxFailuresPerIpUser, options.FailureWindow);
        var byIp = new FailureLimiter(time, options.MaxFailuresPerIp, options.FailureWindow);
        var byUser = new FailureLimiter(time, options.MaxFailuresPerUser, options.FailureWindow);
        var byIpAdmin = new FailureLimiter(time, 10, options.FailureWindow);
        // Hash finto: un utente inesistente costa lo stesso tempo di uno esistente.
        var (dummyHash, dummySalt) = PasswordHasher.Hash("dummy", options.ServerHashIterations);

        var app = builder.Build();
        var log = app.Logger;

        app.MapGet("/health", () => Results.Json(new { status = "ok", version = Version }));

        // ---------- amministrazione ----------
        IResult? AdminCheck(HttpContext ctx)
        {
            if (options.AdminKey.Length == 0)
                return Err(503, "admin_disabled", "Gli endpoint di amministrazione sono spenti (manca RDPM_SYNC_ADMIN_KEY).");
            var ip = Ip(ctx);
            if (byIpAdmin.Blocked(ip) is { } wait)
                return TooMany(wait);
            var given = ctx.Request.Headers["X-Admin-Key"].ToString();
            if (!SecretEquals(given, options.AdminKey))
            {
                byIpAdmin.Fail(ip);
                log.LogWarning("Chiave di amministrazione sbagliata da {Ip}", ip);
                return Err(401, "unauthorized", "Chiave di amministrazione mancante o sbagliata.");
            }
            byIpAdmin.Reset(ip);
            return null;
        }

        app.MapPost("/v1/admin/accounts", (HttpContext ctx, CredentialsRequest? body) =>
        {
            if (AdminCheck(ctx) is { } denied)
                return denied;
            if (!Store.ValidUsername(body?.Username))
                return Err(400, "invalid_username", "Nome utente: da 3 a 64 caratteri tra lettere, cifre e . _ - @");
            if (!ValidSecret(body?.Password))
                return Err(400, "invalid_password", "Il segreto di accesso deve essere derivato dal cliente (vedi contratto).");
            if (!store.CreateAccount(body!.Username!, body.Password!, options.ServerHashIterations))
                return Err(409, "account_exists", "Esiste già un account con questo nome.");
            log.LogInformation("Account creato: {User}", body.Username);
            return Results.Json(new { username = body.Username }, statusCode: 201);
        });

        app.MapGet("/v1/admin/accounts", (HttpContext ctx) =>
            AdminCheck(ctx) ?? Results.Json(store.ListAccounts().Select(a => new
            {
                username = a.Username, createdUtc = a.Created, disabled = a.Disabled, hasDocument = a.HasDocument,
            })));

        app.MapDelete("/v1/admin/accounts/{username}", (HttpContext ctx, string username) =>
        {
            if (AdminCheck(ctx) is { } denied)
                return denied;
            if (!store.DeleteAccount(username))
                return Err(404, "not_found", "Account inesistente.");
            log.LogInformation("Account eliminato: {User}", username);
            return Results.NoContent();
        });

        // ---------- accesso ----------
        app.MapPost("/v1/login", (HttpContext ctx, CredentialsRequest? body) =>
        {
            if (body?.Username is not { Length: > 0 } || !ValidSecret(body.Password))
                return Err(400, "invalid_request", "Servono username e password (segreto derivato dal cliente).");
            var ip = Ip(ctx);
            var user = ClientKeys.NormalizeUser(body.Username);
            string kIpUser = ip + "|" + user;
            foreach (var (limiter, key) in new[] { (byIpUser, kIpUser), (byIp, ip), (byUser, user) })
                if (limiter.Blocked(key) is { } wait)
                {
                    log.LogWarning("Accesso bloccato per troppi tentativi: utente {User} da {Ip}", user, ip);
                    return TooMany(wait);
                }

            var account = Store.ValidUsername(body.Username) ? store.FindAccount(body.Username) : null;
            bool ok = false;
            if (account is null)
                PasswordHasher.Verify(body.Password!, dummyHash, dummySalt, options.ServerHashIterations);
            else
                ok = PasswordHasher.Verify(body.Password!, account.Hash, account.Salt, account.Iterations) && !account.Disabled;
            if (!ok)
            {
                byIpUser.Fail(kIpUser);
                byIp.Fail(ip);
                byUser.Fail(user);
                log.LogWarning("Accesso fallito: utente {User} da {Ip}", user, ip);
                return Err(401, "invalid_credentials", "Utente o password sbagliati.");
            }

            byIpUser.Reset(kIpUser);
            var label = body.DeviceName is { Length: > 0 } d ? d[..Math.Min(d.Length, 64)] : null;
            var (token, expires) = store.IssueToken(account!.Id, options.TokenLifetime, label);
            log.LogInformation("Accesso riuscito: utente {User} da {Ip}", user, ip);
            return Results.Json(new { token, tokenType = "Bearer", expiresUtc = expires, username = account.Username });
        });

        TokenInfo? Authenticate(HttpContext ctx)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return null;
            var token = header[7..].Trim();
            return token.Length is 0 or > 200 ? null : store.ValidateToken(token);
        }

        IResult Unauthorized(HttpContext ctx)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return Err(401, "unauthorized", "Token mancante, scaduto o revocato: rifai il login.");
        }

        app.MapGet("/v1/session", (HttpContext ctx) =>
            Authenticate(ctx) is { } t
                ? Results.Json(new { username = t.Username, expiresUtc = t.ExpiresUtc })
                : Unauthorized(ctx));

        app.MapPost("/v1/logout", (HttpContext ctx) =>
        {
            if (Authenticate(ctx) is not { } t)
                return Unauthorized(ctx);
            store.RevokeToken(t.TokenId);
            return Results.NoContent();
        });

        app.MapPost("/v1/logout-all", (HttpContext ctx) =>
        {
            if (Authenticate(ctx) is not { } t)
                return Unauthorized(ctx);
            var n = store.RevokeAllTokens(t.AccountId);
            log.LogInformation("Token revocati: {Count} per {User}", n, t.Username);
            return Results.Json(new { revoked = n });
        });

        // ---------- documento ----------
        app.MapGet("/v1/document", (HttpContext ctx) =>
        {
            if (Authenticate(ctx) is not { } t)
                return Unauthorized(ctx);
            if (store.GetDocument(t.AccountId) is not { } doc)
                return Err(404, "no_document", "Questo account non ha ancora un documento.");
            var etag = Etag(doc.Version);
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers["Last-Modified"] = doc.UpdatedUtc.ToString("R");
            if (ctx.Request.Headers.IfNoneMatch.ToString().Trim() == etag)
                return Results.StatusCode(304);
            return Results.Bytes(doc.Blob, "application/octet-stream");
        });

        app.MapPut("/v1/document", async (HttpContext ctx) =>
        {
            if (Authenticate(ctx) is not { } t)
                return Unauthorized(ctx);

            var ifMatch = ctx.Request.Headers.IfMatch.ToString().Trim();
            var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString().Trim();
            long expected;
            if (ifNoneMatch == "*" && ifMatch.Length == 0)
                expected = 0;
            else if (ifMatch.Length > 0 && ifNoneMatch.Length == 0)
            {
                if (!TryParseEtag(ifMatch, out expected))
                    return Err(400, "invalid_etag", "If-Match non valido: usa l'ETag ricevuto dal server.");
            }
            else if (ifMatch.Length == 0 && ifNoneMatch.Length == 0)
                return Err(428, "precondition_required", "Serve If-Match con l'ETag letto (o If-None-Match: * per il primo salvataggio).");
            else
                return Err(400, "invalid_etag", "Usa solo If-Match oppure solo If-None-Match: *.");

            if (ctx.Request.ContentLength is { } len && len > options.MaxDocumentBytes)
                return Err(413, "too_large", $"Il documento supera {options.MaxDocumentBytes} byte.");
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > options.MaxDocumentBytes)
                    return Err(413, "too_large", $"Il documento supera {options.MaxDocumentBytes} byte.");
            }
            // Un blob valido ha almeno versione + nonce + tag.
            if (ms.Length < 1 + 12 + 16)
                return Err(400, "invalid_document", "Documento vuoto o troppo corto: dev'essere il blob cifrato dal cliente.");

            if (store.PutDocument(t.AccountId, expected, ms.ToArray(), out var version) == PutResult.Conflict)
            {
                var current = store.GetDocument(t.AccountId);
                string? currentEtag = current is null ? null : Etag(current.Version);
                if (currentEtag is not null)
                    ctx.Response.Headers.ETag = currentEtag;
                return Results.Json(new ErrorBody("conflict", "Il documento è cambiato: leggilo, uniscilo e riprova.", currentEtag), statusCode: 409);
            }
            ctx.Response.Headers.ETag = Etag(version);
            return Results.Json(new { etag = Etag(version), version }, statusCode: expected == 0 ? 201 : 200);
        });

        return app;
    }

    public static string Etag(long version) => $"\"v{version}\"";

    public static bool TryParseEtag(string text, out long version)
    {
        version = 0;
        var s = text.Trim();
        return s.Length > 3 && s[0] == '"' && s[^1] == '"' && s[1] == 'v'
               && long.TryParse(s[2..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version)
               && version > 0;
    }

    private static bool ValidSecret(string? s) => s is { Length: >= MinSecretLength and <= MaxSecretLength };

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static bool SecretEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private static IResult Err(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), statusCode: status);

    private static IResult TooMany(TimeSpan wait)
    {
        int seconds = (int)Math.Ceiling(wait.TotalSeconds);
        return new RetryResult(seconds);
    }

    private sealed class RetryResult(int seconds) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = seconds.ToString();
            await ctx.Response.WriteAsJsonAsync(new ErrorBody("too_many_attempts",
                "Troppi tentativi sbagliati: riprova più tardi.", RetryAfterSeconds: seconds));
        }
    }
}
