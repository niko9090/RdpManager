using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RdpmSync;

namespace RdpmSync.Tests;

public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>Server vero su una porta libera di 127.0.0.1, con database temporaneo e orologio manuale.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public const int KdfIterations = 1000; // nei test: il costo vero (600000) non serve
    public const string AdminKey = "chiave-amministrazione-di-prova";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-sync-test-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplication _app;
    public ManualTime Time { get; } = new();
    public HttpClient Http { get; }
    public SyncOptions Options { get; }

    public TestServer(Action<SyncOptions>? configure = null, string adminKey = AdminKey)
    {
        Options = new SyncOptions
        {
            DatabasePath = Path.Combine(_dir, "db.sqlite"),
            AdminKey = adminKey,
            ServerHashIterations = 1000,
            MaxDocumentBytes = 64 * 1024,
        };
        configure?.Invoke(Options);
        _app = SyncApp.Build(Options, Time, "http://127.0.0.1:0");
        _app.Start();
        Http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public static string Secret(string user, string password) => ClientKeys.AuthSecret(user, password, KdfIterations);

    public async Task<HttpResponseMessage> CreateAccountAsync(string user, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/accounts")
        {
            Content = JsonContent.Create(new { username = user, password = Secret(user, password) }),
        };
        req.Headers.Add("X-Admin-Key", AdminKey);
        return await Http.SendAsync(req);
    }

    public Task<HttpResponseMessage> LoginRawAsync(string user, string password) =>
        Http.PostAsJsonAsync("/v1/login", new { username = user, password = Secret(user, password) });

    public async Task<string> LoginAsync(string user, string password)
    {
        var r = await LoginRawAsync(user, password);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, byte[]? body = null,
        string? ifMatch = null, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null)
            req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (body is not null)
            req.Content = new ByteArrayContent(body);
        return Http.SendAsync(req);
    }

    public Task<HttpResponseMessage> PutAsync(string token, byte[] blob, string? ifMatch = null, string? ifNoneMatch = null) =>
        SendAsync(token, HttpMethod.Put, "/v1/document", blob, ifMatch, ifNoneMatch);

    public Task<HttpResponseMessage> GetDocAsync(string token) => SendAsync(token, HttpMethod.Get, "/v1/document");

    public static byte[] Blob(string text) =>
        ClientKeys.Seal(new byte[32], "x", Encoding.UTF8.GetBytes(text));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public class LoginTests
{
    [Fact]
    public async Task Login_giusto_da_un_token_e_sbagliato_401()
    {
        await using var s = new TestServer();
        Assert.Equal(HttpStatusCode.Created, (await s.CreateAccountAsync("mario", "frase segreta lunga")).StatusCode);

        var ok = await s.LoginRawAsync("mario", "frase segreta lunga");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("rdpm_", json.GetProperty("token").GetString());
        Assert.Equal("Bearer", json.GetProperty("tokenType").GetString());

        var wrong = await s.LoginRawAsync("mario", "altra password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // utente inesistente: stessa risposta di password sbagliata
        var unknown = await s.LoginRawAsync("nessuno", "frase segreta lunga");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Il_nome_utente_non_distingue_maiuscole()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("Mario", "frase segreta lunga");
        Assert.Equal(HttpStatusCode.OK, (await s.LoginRawAsync("mario", "frase segreta lunga")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.CreateAccountAsync("MARIO", "altra")).StatusCode);
    }

    [Fact]
    public async Task Token_scaduto_non_vale_piu()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        Assert.Equal(HttpStatusCode.OK, (await s.SendAsync(token, HttpMethod.Get, "/v1/session")).StatusCode);

        s.Time.Advance(s.Options.TokenLifetime + TimeSpan.FromMinutes(1));
        var r = await s.SendAsync(token, HttpMethod.Get, "/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Token_revocato_non_vale_piu_e_logout_all_li_revoca_tutti()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var t1 = await s.LoginAsync("mario", "pw");
        var t2 = await s.LoginAsync("mario", "pw");
        var t3 = await s.LoginAsync("mario", "pw");

        Assert.Equal(HttpStatusCode.NoContent, (await s.SendAsync(t1, HttpMethod.Post, "/v1/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(t1, HttpMethod.Get, "/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.SendAsync(t2, HttpMethod.Get, "/v1/session")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await s.SendAsync(t2, HttpMethod.Post, "/v1/logout-all")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(t2, HttpMethod.Get, "/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(t3, HttpMethod.Get, "/v1/session")).StatusCode);
    }

    [Fact]
    public async Task Senza_token_o_con_token_inventato_401()
    {
        await using var s = new TestServer();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Http.GetAsync("/v1/document")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.GetDocAsync("rdpm_inventato")).StatusCode);
    }

    [Fact]
    public async Task Limite_tentativi_blocca_anche_la_password_giusta_e_poi_si_sblocca()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        for (int i = 0; i < s.Options.MaxFailuresPerIpUser; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await s.LoginRawAsync("mario", "sbagliata" + i)).StatusCode);

        var blocked = await s.LoginRawAsync("mario", "pw");
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta?.TotalSeconds > 0);

        s.Time.Advance(s.Options.FailureWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await s.LoginRawAsync("mario", "pw")).StatusCode);
    }

    [Fact]
    public async Task Un_accesso_riuscito_azzera_i_tentativi_sbagliati_della_coppia()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < s.Options.MaxFailuresPerIpUser - 1; i++)
                await s.LoginRawAsync("mario", "no");
            Assert.Equal(HttpStatusCode.OK, (await s.LoginRawAsync("mario", "pw")).StatusCode);
        }
    }

    [Fact]
    public async Task Richiesta_malformata_400()
    {
        await using var s = new TestServer();
        var r = await s.Http.PostAsJsonAsync("/v1/login", new { username = "mario" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

public class AdminTests
{
    [Fact]
    public async Task Registrazione_solo_con_la_chiave_di_amministrazione()
    {
        await using var s = new TestServer();
        var body = new { username = "mario", password = TestServer.Secret("mario", "pw") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Http.PostAsJsonAsync("/v1/admin/accounts", body)).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/accounts") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", "sbagliata-sbagliata-sbagliata");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Http.SendAsync(req)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await s.CreateAccountAsync("mario", "pw")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.CreateAccountAsync("mario", "pw")).StatusCode);
    }

    [Fact]
    public async Task Senza_chiave_configurata_l_amministrazione_http_e_spenta()
    {
        await using var s = new TestServer(adminKey: "");
        var r = await s.CreateAccountAsync("mario", "pw");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Eliminare_un_account_ne_toglie_token_e_documento()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        await s.PutAsync(token, TestServer.Blob("a"), ifNoneMatch: "*");

        var del = new HttpRequestMessage(HttpMethod.Delete, "/v1/admin/accounts/mario");
        del.Headers.Add("X-Admin-Key", TestServer.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.SendAsync(del)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.GetDocAsync(token)).StatusCode);

        await s.CreateAccountAsync("mario", "pw");
        var again = await s.LoginAsync("mario", "pw");
        Assert.Equal(HttpStatusCode.NotFound, (await s.GetDocAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Nome_utente_non_valido_400()
    {
        await using var s = new TestServer();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.CreateAccountAsync("a b", "pw")).StatusCode);
    }

    [Fact]
    public void Comando_crea_account_crea_un_account_che_fa_login()
    {
        var db = Path.Combine(Path.GetTempPath(), "rdpm-sync-cli-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new SyncOptions { DatabasePath = db, ServerHashIterations = 1000 };
            var output = new StringWriter();
            Assert.Equal(0, Cli.Run(["crea-account", "anna"], options, new StringReader("una frase lunga\n"), output));
            Assert.Equal(1, Cli.Run(["crea-account", "anna"], options, new StringReader("una frase lunga\n"), output));
            var acc = new Store(db, TimeProvider.System).FindAccount("anna")!;
            Assert.True(PasswordHasher.Verify(ClientKeys.AuthSecret("anna", "una frase lunga"), acc.Hash, acc.Salt, acc.Iterations));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                try { File.Delete(f); } catch (IOException) { }
        }
    }
}

public class DocumentTests
{
    [Fact]
    public async Task Primo_salvataggio_lettura_e_aggiornamento_con_etag()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");

        Assert.Equal(HttpStatusCode.NotFound, (await s.GetDocAsync(token)).StatusCode);

        var blob1 = TestServer.Blob("uno");
        var put1 = await s.PutAsync(token, blob1, ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.Created, put1.StatusCode);
        Assert.Equal("\"v1\"", put1.Headers.ETag?.Tag);

        var get = await s.GetDocAsync(token);
        Assert.Equal(blob1, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"v1\"", get.Headers.ETag?.Tag);

        var blob2 = TestServer.Blob("due");
        var put2 = await s.PutAsync(token, blob2, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
        Assert.Equal("\"v2\"", put2.Headers.ETag?.Tag);
        Assert.Equal(blob2, await (await s.GetDocAsync(token)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Conflitto_di_versione_409_senza_sovrascrivere()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        await s.PutAsync(token, TestServer.Blob("uno"), ifNoneMatch: "*");
        var blob2 = TestServer.Blob("due");
        await s.PutAsync(token, blob2, ifMatch: "\"v1\"");

        // un secondo dispositivo scrive partendo dalla versione 1, ormai vecchia
        var stale = await s.PutAsync(token, TestServer.Blob("vecchio"), ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("\"v2\"", stale.Headers.ETag?.Tag);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict", body.GetProperty("error").GetString());
        Assert.Equal("\"v2\"", body.GetProperty("currentEtag").GetString());

        // e anche un "primo salvataggio" quando il documento esiste già
        Assert.Equal(HttpStatusCode.Conflict, (await s.PutAsync(token, TestServer.Blob("x"), ifNoneMatch: "*")).StatusCode);

        Assert.Equal(blob2, await (await s.GetDocAsync(token)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Scritture_contemporanee_con_la_stessa_versione_ne_vince_una_sola()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        await s.PutAsync(token, TestServer.Blob("base"), ifNoneMatch: "*");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            s.PutAsync(token, TestServer.Blob("scrittura " + i), ifMatch: "\"v1\"")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Senza_condizione_428_e_etag_malformato_400()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        Assert.Equal((HttpStatusCode)428, (await s.PutAsync(token, TestServer.Blob("a"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsync(token, TestServer.Blob("a"), ifMatch: "\"abc\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsync(token, TestServer.Blob("a"), ifMatch: "*")).StatusCode);
    }

    [Fact]
    public async Task Documento_troppo_grande_413_e_troppo_corto_400()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        var big = new byte[s.Options.MaxDocumentBytes + 1];
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await s.PutAsync(token, big, ifNoneMatch: "*")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsync(token, [1, 2, 3], ifNoneMatch: "*")).StatusCode);
    }

    [Fact]
    public async Task Get_con_if_none_match_uguale_da_304()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw");
        var token = await s.LoginAsync("mario", "pw");
        await s.PutAsync(token, TestServer.Blob("a"), ifNoneMatch: "*");
        var r = await s.SendAsync(token, HttpMethod.Get, "/v1/document", ifNoneMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NotModified, r.StatusCode);
    }

    [Fact]
    public async Task Un_account_non_vede_i_dati_di_un_altro()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("mario", "pw1");
        await s.CreateAccountAsync("anna", "pw2");
        var mario = await s.LoginAsync("mario", "pw1");
        var anna = await s.LoginAsync("anna", "pw2");

        var segreto = TestServer.Blob("dati di mario");
        await s.PutAsync(mario, segreto, ifNoneMatch: "*");

        Assert.Equal(HttpStatusCode.NotFound, (await s.GetDocAsync(anna)).StatusCode);

        // Anna scrive il suo: la versione è sua, quella di Mario non cambia
        var annaBlob = TestServer.Blob("dati di anna");
        Assert.Equal(HttpStatusCode.Created, (await s.PutAsync(anna, annaBlob, ifNoneMatch: "*")).StatusCode);
        Assert.Equal(segreto, await (await s.GetDocAsync(mario)).Content.ReadAsByteArrayAsync());
        Assert.Equal(annaBlob, await (await s.GetDocAsync(anna)).Content.ReadAsByteArrayAsync());

        // la password di Anna non apre il login di Mario
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.LoginRawAsync("mario", "pw2")).StatusCode);
    }

    [Fact]
    public async Task Piu_dispositivi_con_lo_stesso_login_vedono_lo_stesso_documento()
    {
        await using var s = new TestServer();
        await s.CreateAccountAsync("famiglia", "pw");
        var pc1 = await s.LoginAsync("famiglia", "pw");
        var pc2 = await s.LoginAsync("famiglia", "pw");
        var blob = TestServer.Blob("condiviso");
        await s.PutAsync(pc1, blob, ifNoneMatch: "*");
        Assert.Equal(blob, await (await s.GetDocAsync(pc2)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Il_documento_sopravvive_al_riavvio_del_server()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-sync-restart-" + Guid.NewGuid().ToString("N"));
        var options = new SyncOptions { DatabasePath = Path.Combine(dir, "db.sqlite"), ServerHashIterations = 1000 };
        var blob = TestServer.Blob("persistente");
        string token;
        var app1 = SyncApp.Build(options, null, "http://127.0.0.1:0");
        app1.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app1.Urls.First()) };
            Assert.True(new Store(options.DatabasePath, TimeProvider.System).CreateAccount("mario", TestServer.Secret("mario", "pw"), 1000));
            var login = await http.PostAsJsonAsync("/v1/login", new { username = "mario", password = TestServer.Secret("mario", "pw") });
            token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            var req = new HttpRequestMessage(HttpMethod.Put, "/v1/document") { Content = new ByteArrayContent(blob) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(req)).StatusCode);
        }
        finally
        {
            await app1.StopAsync();
            await app1.DisposeAsync();
        }

        var app2 = SyncApp.Build(options, null, "http://127.0.0.1:0");
        app2.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app2.Urls.First()) };
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/document");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var r = await http.SendAsync(req);
            Assert.Equal(blob, await r.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await app2.StopAsync();
            await app2.DisposeAsync();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Health_risponde_senza_login()
    {
        await using var s = new TestServer();
        var r = await s.Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}

public class ClientKeysTests
{
    [Fact]
    public void Cifratura_del_blob_roundtrip_e_chiave_o_utente_sbagliati_falliscono()
    {
        var key = ClientKeys.EncryptionKey("mario", "pw", 1000);
        var plain = Encoding.UTF8.GetBytes("{\"format\":\"rdpm\"}");
        var blob = ClientKeys.Seal(key, "Mario", plain);
        Assert.Equal(1, blob[0]);
        Assert.Equal(plain, ClientKeys.Open(key, "mario", blob));
        Assert.Null(ClientKeys.Open(ClientKeys.EncryptionKey("mario", "altra", 1000), "mario", blob));
        Assert.Null(ClientKeys.Open(key, "anna", blob));
        blob[^1] ^= 1;
        Assert.Null(ClientKeys.Open(key, "mario", blob));
    }

    [Fact]
    public void Segreto_di_accesso_e_chiave_di_cifratura_sono_diversi()
    {
        var auth = Convert.FromBase64String(ClientKeys.AuthSecret("mario", "pw", 1000));
        Assert.NotEqual(auth, ClientKeys.EncryptionKey("mario", "pw", 1000));
        Assert.Equal(ClientKeys.AuthSecret("MARIO ", "pw", 1000), ClientKeys.AuthSecret("mario", "pw", 1000));
    }

    [Fact]
    public void Vettore_di_prova_documentato()
    {
        // Gli stessi valori sono in docs/sync-server-api.md: il cliente deve ottenerli identici.
        Assert.Equal(VectorAuth, ClientKeys.AuthSecret("mario", "correct horse battery staple"));
        Assert.Equal(VectorEnc, Convert.ToHexString(ClientKeys.EncryptionKey("mario", "correct horse battery staple")).ToLowerInvariant());
    }

    private const string VectorAuth = "VECTOR_AUTH";
    private const string VectorEnc = "VECTOR_ENC";
}
