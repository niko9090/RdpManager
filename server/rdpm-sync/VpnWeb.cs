using System.Net;
using System.Text;

namespace RdpmSync;

/// <summary>
/// Plancia web della VPN (HTML servito dal server). Si entra solo con un account esistente: il modulo di accesso deriva
/// il segreto dalla password come farebbe l'app e usa gli stessi limiti di tentativi del login API; l'accesso vale come
/// cookie HttpOnly. Senza accesso ogni pagina rimanda a /vpn/accesso. La revoca è riservata agli utenti in RDPM_SYNC_WEB_ADMINS.
/// </summary>
public sealed class VpnWeb(SyncOptions options, Store store, VpnEndpoints vpn, TimeProvider time)
{
    private const string Cookie = "rdpm_web";
    private static readonly TimeSpan ConnectedWindow = VpnEndpoints.ConnectedWindow;

    private readonly HashSet<string> _admins = options.WebAdmins
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ClientKeys.NormalizeUser).ToHashSet();

    private bool IsAdmin(TokenInfo t) => _admins.Contains(ClientKeys.NormalizeUser(t.Username));

    private (TokenInfo? Info, string Token) Session(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[Cookie] ?? "";
        return token.Length is 0 or > 200 ? (null, "") : (store.ValidateToken(token), token);
    }

    private static string Csrf(string token) => Store.HashToken("csrf|" + token);

    public void Map(WebApplication app, Func<HttpContext, string, string, (Account? Account, IResult? Error)> checkCredentials)
    {
        app.MapGet("/vpn/accesso", (HttpContext ctx) =>
            Session(ctx).Info is not null ? Results.Redirect("/vpn") : Page(ctx, "Accesso", LoginForm(null)));

        app.MapPost("/vpn/accesso", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Page(ctx, "Accesso", LoginForm("Richiesta non valida."), 400);
            var form = await ctx.Request.ReadFormAsync();
            string user = form["utente"].ToString().Trim(), password = form["password"].ToString();
            if (user.Length == 0 || password.Length == 0 || user.Length > 64 || password.Length > 256)
                return Page(ctx, "Accesso", LoginForm("Inserisci utente e password."), 400);

            var secret = ClientKeys.AuthSecret(user, password, options.WebKdfIterations);
            var (account, error) = checkCredentials(ctx, user, secret);
            if (account is null)
            {
                var blocked = error is SyncApp.RetryResult;
                return Page(ctx, "Accesso", LoginForm(blocked ? "Troppi tentativi sbagliati: riprova più tardi." : "Utente o password sbagliati."),
                    blocked ? 429 : 401);
            }

            var (token, expires) = store.IssueToken(account.Id, options.WebSessionLifetime, "web");
            ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"].ToString() == "https",
                Expires = expires,
                Path = "/",
            });
            return Results.Redirect("/vpn");
        });

        app.MapPost("/vpn/esci", async (HttpContext ctx) =>
        {
            var (info, token) = Session(ctx);
            if (info is not null && await FormOk(ctx, token))
                store.RevokeToken(info.TokenId);
            ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
            return Results.Redirect("/vpn/accesso");
        });

        app.MapGet("/vpn", async (HttpContext ctx) =>
        {
            var (info, token) = Session(ctx);
            if (info is null)
                return Results.Redirect("/vpn/accesso");
            return Page(ctx, "VPN", await Dashboard(info, token, ctx.Request.Query["esito"].ToString()));
        });

        app.MapPost("/vpn/revoca", async (HttpContext ctx) =>
        {
            var (info, token) = Session(ctx);
            if (info is null)
                return Results.Redirect("/vpn/accesso");
            if (!IsAdmin(info))
                return Page(ctx, "VPN", "<p class=err>Solo gli amministratori possono revocare un dispositivo.</p><p><a href=\"/vpn\">Indietro</a></p>", 403);
            if (!await FormOk(ctx, token))
                return Page(ctx, "VPN", "<p class=err>Richiesta non valida: ricarica la pagina e riprova.</p><p><a href=\"/vpn\">Indietro</a></p>", 400);
            var form = await ctx.Request.ReadFormAsync();
            bool done = long.TryParse(form["id"], out var id) && await vpn.RevokeAsync(id, info.Username);
            return Results.Redirect(done ? "/vpn?esito=revocato" : "/vpn?esito=nontrovato");
        });
    }

    private static async Task<bool> FormOk(HttpContext ctx, string token)
    {
        if (!ctx.Request.HasFormContentType)
            return false;
        var form = await ctx.Request.ReadFormAsync();
        return form["csrf"].ToString() == Csrf(token);
    }

    private async Task<string> Dashboard(TokenInfo info, string token, string esito)
    {
        var admin = IsAdmin(info);
        var sb = new StringBuilder();
        var csrf = Csrf(token);
        sb.Append("<div class=top><span>Utente: <b>").Append(H(info.Username)).Append("</b></span>")
          .Append("<form method=post action=\"/vpn/esci\"><input type=hidden name=csrf value=\"").Append(csrf)
          .Append("\"><button class=lnk>Esci</button></form></div>");
        if (esito == "revocato")
            sb.Append("<p class=ok>Dispositivo revocato.</p>");
        else if (esito == "nontrovato")
            sb.Append("<p class=err>Dispositivo non trovato.</p>");

        var ov = await vpn.OverviewAsync();
        sb.Append("<h2>Server marshmelloVPN</h2><dl>")
          .Append("<dt>Rete</dt><dd>").Append(H(ov.Network)).Append("</dd>")
          .Append("<dt>Indirizzo pubblico (endpoint)</dt><dd>").Append(H(ov.Endpoint.Length > 0 ? ov.Endpoint : "non impostato")).Append("</dd>");
        if (ov.Dns.Length > 0)
            sb.Append("<dt>DNS</dt><dd>").Append(H(ov.Dns)).Append("</dd>");
        sb.Append("<dt>Registrazione automatica</dt><dd>").Append(ov.Enabled ? "attiva" : "non attiva").Append("</dd></dl>");

        sb.Append("<h2>Dispositivi (").Append(ov.Devices.Count).Append(")</h2>");
        if (ov.Devices.Count == 0)
            sb.Append("<p>Nessun dispositivo registrato.</p>");
        var now = time.GetUtcNow();
        foreach (var d in ov.Devices)
        {
            bool connected = d.LastContact is { } lc && now - lc <= ConnectedWindow;
            sb.Append("<div class=card><div class=row><b>").Append(H(d.Name)).Append("</b><span class=\"")
              .Append(connected ? "on" : "off").Append("\">").Append(connected ? "Connesso" : "Non connesso").Append("</span></div>")
              .Append("<div>IP marshmelloVPN:").Append(H(d.Address)).Append("</div>");
            if (admin)
                sb.Append("<div>Account: ").Append(d.Id == 0 ? "(peer messo a mano nel server)" : H(d.Owner)).Append("</div>");
            sb.Append("<div>Ultimo contatto: ").Append(d.LastContact is { } l ? H(Format(l)) + (d.FromHandshake ? " (handshake)" : " (registrazione)") : "mai")
              .Append("</div>");
            if (admin && d.Id != 0) // i peer messi a mano non hanno una registrazione da revocare
                sb.Append("<form method=post action=\"/vpn/revoca\" onsubmit=\"return confirm('Revocare questo dispositivo?')\">")
                  .Append("<input type=hidden name=csrf value=\"").Append(csrf).Append("\"><input type=hidden name=id value=\"").Append(d.Id)
                  .Append("\"><button class=del>Revoca</button></form>");
            sb.Append("</div>");
        }
        if (!admin)
            sb.Append("<p class=muted>La revoca dei dispositivi è riservata agli amministratori.</p>");
        return sb.ToString();
    }

    private static string Format(DateTimeOffset t)
    {
        TimeZoneInfo? tz = null;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return tz is null
            ? t.UtcDateTime.ToString("dd/MM/yyyy HH:mm") + " UTC"
            : TimeZoneInfo.ConvertTime(t, tz).ToString("dd/MM/yyyy HH:mm");
    }

    private static string LoginForm(string? error) =>
        (error is null ? "" : "<p class=err>" + H(error) + "</p>") +
        "<form method=post action=\"/vpn/accesso\"><label>Utente<input name=utente autocomplete=username autocapitalize=none required></label>" +
        "<label>Password<input name=password type=password autocomplete=current-password required></label>" +
        "<button>Entra</button></form>";

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static IResult Page(HttpContext ctx, string title, string body, int status = 200)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
        var html = "<!doctype html><html lang=it><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">" +
            "<meta name=robots content=noindex><title>" + H(title == "VPN" ? "marshmelloVPN" : "marshmelloVPN · " + title) + " · RdpManager</title><style>" + Css + "</style></head><body><main><h1>" +
            H(title == "VPN" ? "Plancia marshmelloVPN" : "Plancia marshmelloVPN · " + title) + "</h1>" + body + "</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private const string Css =
        "body{font-family:system-ui,sans-serif;margin:0;background:#f4f5f7;color:#1c1e21}main{max-width:640px;margin:0 auto;padding:16px}" +
        "h1{font-size:1.4rem}h2{font-size:1.1rem;margin-top:1.5rem}.card,form label,dl{background:#fff;border-radius:8px;padding:12px;margin:8px 0;display:block;box-shadow:0 1px 2px #0002}" +
        ".row{display:flex;justify-content:space-between;gap:8px}.on{color:#0a7d33;font-weight:600}.off{color:#a33}dt{color:#667;font-size:.85rem}dd{margin:0 0 8px}" +
        "input{display:block;width:100%;box-sizing:border-box;padding:10px;margin-top:4px;font-size:1rem}button{padding:10px 16px;font-size:1rem;border:0;border-radius:6px;background:#1a73e8;color:#fff}" +
        ".del{background:#c62828;margin-top:8px}.lnk{background:none;color:#1a73e8;padding:0}.top{display:flex;justify-content:space-between;align-items:center}" +
        ".err{color:#a00}.ok{color:#0a7d33}.muted{color:#667;font-size:.9rem}";
}
