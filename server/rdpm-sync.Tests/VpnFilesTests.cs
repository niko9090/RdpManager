using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RdpmSync;

namespace RdpmSync.Tests;

/// <summary>Scambio di file fra i PC dello stesso account (/v1/vpn/files).</summary>
public class VpnFilesTests
{
    private const string ServerKey = "c2VydmVyLXB1YmxpYy1rZXktMzItYnl0ZXMtLi4uLi4=";

    private sealed class Fixture : IAsyncDisposable
    {
        public TestServer S { get; }
        public HttpClient Http { get; }

        public Fixture(Action<SyncOptions>? configure = null)
        {
            S = new TestServer(o =>
            {
                o.VpnServerPublicKey = ServerKey;
                o.VpnEndpoint = "vpn.example.com:51820";
                o.MaxFileBytes = 1024;
                configure?.Invoke(o);
            }, wireGuard: new FakeWireGuard());
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = S.Http.BaseAddress };
        }

        public async Task<string> UserAsync(string user, params string[] devices)
        {
            await S.CreateAccountAsync(user, "frase segreta lunga");
            var token = await S.LoginAsync(user, "frase segreta lunga");
            foreach (var d in devices)
            {
                var r = await Send(token, HttpMethod.Post, "/v1/vpn/devices", null,
                    JsonContent.Create(new { name = d, publicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
                r.EnsureSuccessStatusCode();
            }
            return token;
        }

        public Task<HttpResponseMessage> Send(string token, HttpMethod m, string path, string? device, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(m, path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (device is not null)
                req.Headers.Add("X-Device-Name", device);
            return Http.SendAsync(req);
        }

        public Task<HttpResponseMessage> Upload(string token, string from, string to, string name, byte[] data) =>
            Send(token, HttpMethod.Post, $"/v1/vpn/files?to={Uri.EscapeDataString(to)}&name={Uri.EscapeDataString(name)}", from,
                new ByteArrayContent(data));

        public async Task<JsonElement[]> Incoming(string token, string device)
        {
            var r = await Send(token, HttpMethod.Get, "/v1/vpn/files", device);
            r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files").EnumerateArray().ToArray();
        }

        public string TransferDir => Path.Combine(Path.GetDirectoryName(S.Options.DatabasePath)!, "transfers");

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await S.DisposeAsync();
        }
    }

    [Fact]
    public async Task Invio_elenco_accettazione_e_cancellazione()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");
        var data = RandomNumberGenerator.GetBytes(500);

        var up = await f.Upload(token, "Casa", "Ufficio", "relazione.pdf", data);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var id = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Empty(await f.Incoming(token, "Casa")); // il mittente non vede nulla
        var item = Assert.Single(await f.Incoming(token, "Ufficio"));
        Assert.Equal("Casa", item.GetProperty("from").GetString());
        Assert.Equal("relazione.pdf", item.GetProperty("name").GetString());
        Assert.Equal(500, item.GetProperty("size").GetInt64());

        var acc = await f.Send(token, HttpMethod.Post, $"/v1/vpn/files/{id}/accept", "Ufficio");
        Assert.Equal(HttpStatusCode.OK, acc.StatusCode);
        Assert.Equal(data, await acc.Content.ReadAsByteArrayAsync());
        Assert.Empty(await f.Incoming(token, "Ufficio"));
        Assert.Empty(Directory.GetFiles(f.TransferDir));

        var again = await f.Send(token, HttpMethod.Post, $"/v1/vpn/files/{id}/accept", "Ufficio");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Il_rifiuto_cancella_il_file()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");
        var up = await f.Upload(token, "Casa", "Ufficio", "a.txt", [1, 2, 3]);
        var id = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var rej = await f.Send(token, HttpMethod.Post, $"/v1/vpn/files/{id}/reject", "Ufficio");

        Assert.Equal(HttpStatusCode.NoContent, rej.StatusCode);
        Assert.Empty(await f.Incoming(token, "Ufficio"));
        Assert.Empty(Directory.GetFiles(f.TransferDir));
    }

    [Fact]
    public async Task Solo_il_destinatario_puo_accettare_o_rifiutare()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio", "Altro");
        var up = await f.Upload(token, "Casa", "Ufficio", "a.txt", [1, 2, 3]);
        var id = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(token, HttpMethod.Post, $"/v1/vpn/files/{id}/accept", "Altro")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(token, HttpMethod.Post, $"/v1/vpn/files/{id}/reject", "Casa")).StatusCode);
        Assert.Single(await f.Incoming(token, "Ufficio"));
    }

    [Fact]
    public async Task Un_altro_account_non_vede_ne_scarica_ne_invia()
    {
        await using var f = new Fixture();
        var mario = await f.UserAsync("mario", "Casa", "Ufficio");
        var luisa = await f.UserAsync("luisa", "Ufficio", "Portatile"); // stesso nome di PC, altro account
        var up = await f.Upload(mario, "Casa", "Ufficio", "a.txt", [1, 2, 3]);
        var id = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Empty(await f.Incoming(luisa, "Ufficio"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(luisa, HttpMethod.Post, $"/v1/vpn/files/{id}/accept", "Ufficio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(luisa, HttpMethod.Post, $"/v1/vpn/files/{id}/reject", "Ufficio")).StatusCode);
        // luisa non può mandare a un PC di mario, né fingersi un suo PC
        Assert.Equal(HttpStatusCode.NotFound, (await f.Upload(luisa, "Ufficio", "Casa", "b.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Upload(luisa, "Casa", "Ufficio", "b.txt", [1])).StatusCode);
        Assert.Single(await f.Incoming(mario, "Ufficio"));
    }

    [Fact]
    public async Task Senza_token_o_senza_pc_noto_si_viene_respinti()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Send("falso", HttpMethod.Get, "/v1/vpn/files", "Casa")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send(token, HttpMethod.Get, "/v1/vpn/files", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send(token, HttpMethod.Get, "/v1/vpn/files", "Sconosciuto")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Http.PostAsync("/v1/vpn/files?to=Ufficio&name=a", new ByteArrayContent([1]))).StatusCode);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("C:\\x.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("nome\u0001.txt")]
    [InlineData("punto.")]
    public async Task Nomi_con_percorsi_o_caratteri_strani_sono_rifiutati(string name)
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");

        var r = await f.Upload(token, "Casa", "Ufficio", name, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_file_name", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(Directory.Exists(f.TransferDir) && Directory.GetFiles(f.TransferDir).Length > 0);
    }

    [Fact]
    public async Task Sul_disco_il_file_ha_il_nome_dell_id_mai_quello_dell_utente()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");
        var up = await f.Upload(token, "Casa", "Ufficio", "ciao mondo.txt", [1, 2, 3]);
        var id = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var path = Assert.Single(Directory.GetFiles(f.TransferDir));
        Assert.Equal(id + ".bin", Path.GetFileName(path));
        // un id con percorso non arriva nemmeno al disco
        Assert.Equal(HttpStatusCode.NotFound,
            (await f.Send(token, HttpMethod.Post, "/v1/vpn/files/..%2F..%2Fdb.sqlite/accept", "Ufficio")).StatusCode);
    }

    [Fact]
    public async Task File_troppo_grande_rifiutato_senza_lasciare_residui()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");

        var big = await f.Upload(token, "Casa", "Ufficio", "grosso.bin", new byte[1025]);
        var chunked = await f.Send(token, HttpMethod.Post, "/v1/vpn/files?to=Ufficio&name=x.bin", "Casa", new ChunkedContent(2000));
        var ok = await f.Upload(token, "Casa", "Ufficio", "limite.bin", new byte[1024]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Single(Directory.GetFiles(f.TransferDir)); // solo quello al limite
    }

    [Fact]
    public async Task File_vuoto_o_a_se_stessi_o_destinatario_inesistente()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");

        Assert.Equal(HttpStatusCode.BadRequest, (await f.Upload(token, "Casa", "Ufficio", "v.txt", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Upload(token, "Casa", "casa", "a.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Upload(token, "Casa", "Nessuno", "a.txt", [1])).StatusCode);
        Assert.Empty(Directory.GetFiles(f.TransferDir));
    }

    [Fact]
    public async Task Troppi_file_in_attesa_vengono_fermati()
    {
        await using var f = new Fixture(o => o.MaxPendingFiles = 2);
        var token = await f.UserAsync("mario", "Casa", "Ufficio");

        Assert.Equal(HttpStatusCode.Created, (await f.Upload(token, "Casa", "Ufficio", "1.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.Upload(token, "Casa", "Ufficio", "2.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Upload(token, "Casa", "Ufficio", "3.txt", [1])).StatusCode);
    }

    [Fact]
    public async Task I_file_vecchi_di_sette_giorni_spariscono()
    {
        await using var f = new Fixture();
        var token = await f.UserAsync("mario", "Casa", "Ufficio");
        await f.Upload(token, "Casa", "Ufficio", "vecchio.txt", [1, 2, 3]);

        f.S.Time.Advance(TimeSpan.FromDays(6));
        Assert.Single(await f.Incoming(token, "Ufficio"));
        await f.Upload(token, "Casa", "Ufficio", "nuovo.txt", [1, 2, 3]);

        f.S.Time.Advance(TimeSpan.FromDays(2));
        var left = Assert.Single(await f.Incoming(token, "Ufficio"));
        Assert.Equal("nuovo.txt", left.GetProperty("name").GetString());
        Assert.Single(Directory.GetFiles(f.TransferDir));
    }

    [Fact]
    public void Il_limite_si_configura_con_la_variabile_d_ambiente()
    {
        Environment.SetEnvironmentVariable("RDPM_SYNC_MAX_FILE_MB", "7");
        Environment.SetEnvironmentVariable("RDPM_SYNC_FILE_DAYS", "2");
        try
        {
            var o = SyncOptions.FromEnvironment();
            Assert.Equal(7L * 1024 * 1024, o.MaxFileBytes);
            Assert.Equal(TimeSpan.FromDays(2), o.FileRetention);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RDPM_SYNC_MAX_FILE_MB", null);
            Environment.SetEnvironmentVariable("RDPM_SYNC_FILE_DAYS", null);
        }
        Assert.Equal(100L * 1024 * 1024, new SyncOptions().MaxFileBytes);
        Assert.Equal(TimeSpan.FromDays(7), new SyncOptions().FileRetention);
    }

    /// <summary>Corpo senza Content-Length (trasferimento a pezzi), per provare il limite durante la lettura.</summary>
    private sealed class ChunkedContent(int size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var chunk = new byte[500];
            for (int sent = 0; sent < size; sent += chunk.Length)
                await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
