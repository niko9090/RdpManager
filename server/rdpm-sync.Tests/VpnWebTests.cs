using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RdpmSync;

namespace RdpmSync.Tests;

public class VpnWebTests
{
    private const string ServerKey = "c2VydmVyLXB1YmxpYy1rZXktMzItYnl0ZXMtLi4uLi4=";
    private const string Pass = "frase segreta lunga";

    private static TestServer Server(FakeWireGuard wg) =>
        new(o =>
        {
            o.VpnServerPublicKey = ServerKey;
            o.VpnEndpoint = "vpn.example.com:51820";
            o.WebKdfIterations = TestServer.KdfIterations;
            o.WebAdmins = "capo";
        }, wireGuard: wg);

    private static HttpClient Browser(TestServer s) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = s.Http.BaseAddress };

    private static async Task<string> LoginCookieAsync(TestServer s, HttpClient b, string user, string password = Pass)
    {
        var r = await b.PostAsync("/vpn/accesso", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["utente"] = user, ["password"] = password }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var set = r.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", set, StringComparison.OrdinalIgnoreCase);
        return set.Split(';')[0];
    }

    private static Task<HttpResponseMessage> Get(HttpClient b, string path, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            req.Headers.Add("Cookie", cookie);
        return b.SendAsync(req);
    }

    private static async Task RegisterAsync(TestServer s, string user, string name, string publicKey)
    {
        var token = await s.LoginAsync(user, Pass);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/vpn/devices") { Content = JsonContent.Create(new { name, publicKey }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        (await s.Http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("/vpn")]
    [InlineData("/vpn/")]
    public async Task Senza_login_rimanda_alla_pagina_di_accesso(string path)
    {
        await using var s = Server(new FakeWireGuard());
        var b = Browser(s);
        var r = await Get(b, path);
        if (r.StatusCode == HttpStatusCode.NotFound)
            return; // «/vpn/» non è una rotta: comunque non mostra nulla
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/vpn/accesso", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Cookie_falso_non_apre_la_pagina_e_la_revoca_senza_login_rimanda_all_accesso()
    {
        await using var s = Server(new FakeWireGuard());
        var b = Browser(s);
        var r = await Get(b, "/vpn", "rdpm_web=rdpm_finto");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var p = await b.PostAsync("/vpn/revoca", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "1" }));
        Assert.Equal(HttpStatusCode.Redirect, p.StatusCode);
    }

    [Fact]
    public async Task Password_sbagliata_non_entra()
    {
        await using var s = Server(new FakeWireGuard());
        await s.CreateAccountAsync("mario", Pass);
        var b = Browser(s);
        var r = await b.PostAsync("/vpn/accesso", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["utente"] = "mario", ["password"] = "sbagliata sbagliata" }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Con_login_la_pagina_mostra_i_dispositivi_senza_chiavi()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        await s.CreateAccountAsync("mario", Pass);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await RegisterAsync(s, "mario", "Ufficio", key);
        wg.Handshakes[key] = s.Time.GetUtcNow().AddSeconds(-30);

        var b = Browser(s);
        var cookie = await LoginCookieAsync(s, b, "mario");
        var r = await Get(b, "/vpn", cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("Ufficio", html);
        Assert.Contains("10.66.0.2", html);
        Assert.Contains("10.66.0.0/24", html);
        Assert.Contains("vpn.example.com:51820", html);
        Assert.Contains("Connesso", html);
        Assert.DoesNotContain("Non connesso", html);
        Assert.DoesNotContain(key, html);
        Assert.DoesNotContain(ServerKey, html);
        Assert.DoesNotContain("PrivateKey", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("[A-Za-z0-9+/]{43}="), html);
        // utente comune: nessun pulsante di revoca, nessun nome di account
        Assert.DoesNotContain("/vpn/revoca", html);
        Assert.DoesNotContain("Account:", html);
    }

    [Fact]
    public async Task Senza_handshake_si_usa_l_ultimo_contatto_registrato()
    {
        await using var s = Server(new FakeWireGuard());
        await s.CreateAccountAsync("mario", Pass);
        await RegisterAsync(s, "mario", "PC", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var b = Browser(s);
        var cookie = await LoginCookieAsync(s, b, "mario");

        var html = await (await Get(b, "/vpn", cookie)).Content.ReadAsStringAsync();
        Assert.Contains("(registrazione)", html);
        Assert.Contains("Connesso", html); // appena registrato

        s.Time.Advance(TimeSpan.FromMinutes(10));
        html = await (await Get(b, "/vpn", cookie)).Content.ReadAsStringAsync();
        Assert.Contains("Non connesso", html);
    }

    [Fact]
    public async Task Solo_l_amministratore_revoca_e_il_peer_sparisce()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        await s.CreateAccountAsync("mario", Pass);
        await s.CreateAccountAsync("capo", Pass);
        await RegisterAsync(s, "mario", "PC", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Single(wg.Peers);
        var b = Browser(s);

        // utente comune: rifiutato
        var userCookie = await LoginCookieAsync(s, b, "mario");
        var page = await (await Get(b, "/vpn", userCookie)).Content.ReadAsStringAsync();
        var form = new Dictionary<string, string> { ["id"] = "1", ["csrf"] = "x" };
        var denied = new HttpRequestMessage(HttpMethod.Post, "/vpn/revoca") { Content = new FormUrlEncodedContent(form) };
        denied.Headers.Add("Cookie", userCookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(denied)).StatusCode);
        Assert.Single(wg.Peers);

        // amministratore: senza token anti-falsificazione no, con sì
        var adminCookie = await LoginCookieAsync(s, b, "capo");
        var adminHtml = await (await Get(b, "/vpn", adminCookie)).Content.ReadAsStringAsync();
        Assert.Contains("Account: mario", adminHtml);
        var bad = new HttpRequestMessage(HttpMethod.Post, "/vpn/revoca") { Content = new FormUrlEncodedContent(form) };
        bad.Headers.Add("Cookie", adminCookie);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.SendAsync(bad)).StatusCode);
        Assert.Single(wg.Peers);

        var csrf = Regex.Match(adminHtml, "name=csrf value=\"([0-9A-F]+)\"").Groups[1].Value;
        var ok = new HttpRequestMessage(HttpMethod.Post, "/vpn/revoca")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "1", ["csrf"] = csrf }),
        };
        ok.Headers.Add("Cookie", adminCookie);
        var r = await b.SendAsync(ok);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Empty(wg.Peers);
    }

    [Fact]
    public async Task Uscita_chiude_l_accesso()
    {
        await using var s = Server(new FakeWireGuard());
        await s.CreateAccountAsync("mario", Pass);
        var b = Browser(s);
        var cookie = await LoginCookieAsync(s, b, "mario");
        var html = await (await Get(b, "/vpn", cookie)).Content.ReadAsStringAsync();
        var csrf = Regex.Match(html, "name=csrf value=\"([0-9A-F]+)\"").Groups[1].Value;
        var req = new HttpRequestMessage(HttpMethod.Post, "/vpn/esci")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf"] = csrf }),
        };
        req.Headers.Add("Cookie", cookie);
        await b.SendAsync(req);
        Assert.Equal(HttpStatusCode.Redirect, (await Get(b, "/vpn", cookie)).StatusCode);
    }
}
