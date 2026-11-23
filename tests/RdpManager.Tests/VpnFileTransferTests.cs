using System.Net;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Scambio di file fra i PC: client contro un server finto, testi e coordinatore dell'avviso.</summary>
public class VpnFileTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-files-" + Guid.NewGuid().ToString("N"));

    public VpnFileTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[]?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            LastBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            return respond(request, LastBody);
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static VpnFileTransferClient Client(HttpMessageHandler h, TimeSpan? timeout = null) =>
        new("https://sync.example.com:8443", "Ufficio", h, timeout);

    private static readonly IncomingFile File1 = new("a1b2", "Casa", "relazione.pdf", 5, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Invio_con_token_nome_pc_e_nome_file_codificato()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Created, "{}"));
        using var client = Client(handler);

        await client.SendAsync("tok", "Casa mia", "a&b #1.txt", new MemoryStream([1, 2, 3, 4]));

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://sync.example.com:8443/v1/vpn/files?to=Casa%20mia&name=a%26b%20%231.txt", req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer tok", req.Headers.Authorization!.ToString());
        Assert.Equal("Ufficio", req.Headers.GetValues("X-Device-Name").Single());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, handler.LastBody);
    }

    [Fact]
    public async Task Invio_rifiutato_dal_server_porta_codice_e_messaggio()
    {
        using var client = Client(new FakeHandler((_, _) =>
            Json(HttpStatusCode.RequestEntityTooLarge, """{"error":"too_large","message":"Il file supera il limite di 100 MB."}""")));

        var ex = await Assert.ThrowsAsync<SyncServerException>(() => client.SendAsync("t", "Casa", "x.bin", new MemoryStream([1])));

        Assert.Equal("too_large", ex.Code);
        Assert.Equal("Il file supera il limite di 100 MB.", VpnFileModel.SendError(ex));
    }

    [Fact]
    public async Task Elenco_in_arrivo_letto_bene()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """
            {"files":[{"id":"a1b2","from":"Casa","to":"Ufficio","name":"relazione.pdf","size":2048,"createdUtc":"2026-11-23T10:00:00+00:00"}]}
            """));
        using var client = Client(handler);

        var files = await client.ListIncomingAsync("tok");

        var f = Assert.Single(files);
        Assert.Equal(new IncomingFile("a1b2", "Casa", "relazione.pdf", 2048, new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero)), f);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.EndsWith("/v1/vpn/files", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Risposta_senza_files_e_non_valida()
    {
        using var client = Client(new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"x":1}""")));

        var ex = await Assert.ThrowsAsync<SyncServerException>(() => client.ListIncomingAsync("t"));

        Assert.Equal("invalid_response", ex.Code);
    }

    [Fact]
    public async Task Accettazione_salva_il_file_nella_cartella_scelta_senza_sovrascrivere()
    {
        var data = Encoding.ASCII.GetBytes("ciao!");
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        using var client = Client(handler);

        var first = await client.AcceptAsync("tok", File1, _dir);
        var second = await client.AcceptAsync("tok", File1, _dir);

        Assert.Equal(Path.Combine(_dir, "relazione.pdf"), first);
        Assert.Equal(Path.Combine(_dir, "relazione (1).pdf"), second);
        Assert.Equal(data, await File.ReadAllBytesAsync(first));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // nessun .part rimasto
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/v1/vpn/files/a1b2/accept", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    public async Task Nome_con_percorso_non_esce_dalla_cartella(string serverName, string _)
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5]) });
        using var client = Client(handler);

        var saved = await client.AcceptAsync("t", File1 with { FileName = serverName }, _dir);

        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(saved)));
    }

    [Theory]
    [InlineData("a/b.txt", "a_b.txt")]
    [InlineData("..", "file")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("", "file")]
    [InlineData("fine.txt.", "fine.txt")]
    public void Nome_locale_sicuro(string input, string expected) =>
        Assert.Equal(expected, VpnFileTransferClient.SafeLocalName(input));

    [Fact]
    public async Task Download_incompleto_non_lascia_file_e_da_errore()
    {
        using var client = Client(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) }));

        await Assert.ThrowsAsync<IOException>(() => client.AcceptAsync("t", File1, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Accettazione_di_file_sparito_dal_server()
    {
        using var client = Client(new FakeHandler((_, _) => Json(HttpStatusCode.NotFound, """{"error":"not_found","message":"no"}""")));

        var ex = await Assert.ThrowsAsync<SyncServerException>(() => client.AcceptAsync("t", File1, _dir));

        Assert.Equal("not_found", ex.Code);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Rifiuto_chiama_reject_con_il_token()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = Client(handler);

        await client.RejectAsync("tok", File1);

        var req = handler.Requests.Single();
        Assert.EndsWith("/v1/vpn/files/a1b2/reject", req.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer tok", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Server_non_raggiungibile_e_server_spento()
    {
        using var down = Client(new ThrowingHandler(new HttpRequestException("giù")));
        await Assert.ThrowsAsync<SyncUnreachableException>(() => down.ListIncomingAsync("t"));

        using var bad = Client(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var ex = await Assert.ThrowsAsync<SyncUnreachableException>(() => bad.RejectAsync("t", File1));
        Assert.True(ex.ServerDown);
    }

    [Fact]
    public async Task Server_che_non_risponde_scade_con_il_tempo_massimo()
    {
        using var client = Client(new HangingHandler(), TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<SyncUnreachableException>(() => client.ListIncomingAsync("t"));
    }

    [Fact]
    public async Task Annullamento_dell_utente_resta_annullamento()
    {
        using var client = Client(new HangingHandler());
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListIncomingAsync("t", cts.Token));
    }

    [Theory]
    [InlineData(500, "500 byte")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_572_864, "1,5 MB")]
    [InlineData(104_857_600, "100 MB")]
    public void Dimensione_in_italiano(long bytes, string expected) => Assert.Equal(expected, VpnFileModel.FormatSize(bytes));

    [Fact]
    public void Testo_dell_avviso_ha_mittente_nome_e_dimensione()
    {
        var text = VpnFileModel.PromptText(File1 with { Size = 2048 });

        Assert.Contains("Casa", text);
        Assert.Contains("«relazione.pdf»", text);
        Assert.Contains("2 KB", text);
    }

    // ---------- coordinatore ----------

    private sealed class Rig
    {
        public bool SignedIn = true;
        public List<IncomingFile> Server = [];
        public Exception? LoadError;
        public Func<IncomingFile, IncomingFileDecision> Decide = _ => new IncomingFileDecision(IncomingFileChoice.Later);
        public List<string> Asked = [], Accepted = [], Rejected = [], Notices = [];
        public Exception? AcceptError;

        public IncomingFilesCoordinator Build() => new(
            () => SignedIn,
            _ => LoadError is null ? Task.FromResult<IReadOnlyList<IncomingFile>>(Server.ToList()) : Task.FromException<IReadOnlyList<IncomingFile>>(LoadError),
            (f, _) => { Asked.Add(f.Id); return Task.FromResult(Decide(f)); },
            (f, folder, _) =>
            {
                if (AcceptError is not null)
                    return Task.FromException<string>(AcceptError);
                Accepted.Add(f.Id + "@" + folder);
                return Task.FromResult(Path.Combine(folder, f.FileName));
            },
            (f, _) => { Rejected.Add(f.Id); return Task.CompletedTask; },
            Notices.Add);
    }

    [Fact]
    public async Task Coordinatore_chiede_una_volta_sola_per_file()
    {
        var rig = new Rig { Server = [File1] };
        var c = rig.Build();

        await c.CheckAsync();
        await c.CheckAsync();

        Assert.Equal(["a1b2"], rig.Asked); // «più tardi»: non richiesto di nuovo nella sessione
        Assert.Empty(rig.Accepted);
        Assert.Empty(rig.Rejected);
    }

    [Fact]
    public async Task Coordinatore_accetta_nella_cartella_scelta_e_avvisa()
    {
        var rig = new Rig { Server = [File1], Decide = _ => new IncomingFileDecision(IncomingFileChoice.Accept, "/tmp/x") };

        await rig.Build().CheckAsync();

        Assert.Equal(["a1b2@/tmp/x"], rig.Accepted);
        Assert.Contains("relazione.pdf", Assert.Single(rig.Notices));
    }

    [Fact]
    public async Task Coordinatore_accetta_senza_cartella_usa_scaricati()
    {
        var rig = new Rig { Server = [File1], Decide = _ => new IncomingFileDecision(IncomingFileChoice.Accept) };

        await rig.Build().CheckAsync();

        Assert.Equal(["a1b2@" + VpnFileModel.DefaultFolder()], rig.Accepted);
    }

    [Fact]
    public async Task Coordinatore_rifiuta()
    {
        var rig = new Rig { Server = [File1], Decide = _ => new IncomingFileDecision(IncomingFileChoice.Reject) };

        await rig.Build().CheckAsync();

        Assert.Equal(["a1b2"], rig.Rejected);
        Assert.Empty(rig.Accepted);
    }

    [Fact]
    public async Task Coordinatore_senza_accesso_o_con_server_giu_non_chiede_niente()
    {
        var rig = new Rig { Server = [File1], SignedIn = false };
        await rig.Build().CheckAsync();

        rig.SignedIn = true;
        rig.LoadError = new SyncUnreachableException("giù");
        await rig.Build().CheckAsync();

        Assert.Empty(rig.Asked);
        Assert.Empty(rig.Notices);
    }

    [Fact]
    public async Task Coordinatore_segnala_il_salvataggio_fallito()
    {
        var rig = new Rig
        {
            Server = [File1], Decide = _ => new IncomingFileDecision(IncomingFileChoice.Accept, "/x"),
            AcceptError = new IOException("disco pieno"),
        };

        await rig.Build().CheckAsync();

        Assert.Contains("disco pieno", Assert.Single(rig.Notices));
    }

    [Fact]
    public async Task Coordinatore_non_si_sovrappone_mentre_l_avviso_e_aperto()
    {
        var gate = new TaskCompletionSource<IncomingFileDecision>();
        int asks = 0;
        var c = new IncomingFilesCoordinator(() => true,
            _ => Task.FromResult<IReadOnlyList<IncomingFile>>([File1, File1 with { Id = "c3" }]),
            (_, _) => { asks++; return gate.Task; },
            (_, _, _) => Task.FromResult(""), (_, _) => Task.CompletedTask, _ => { });

        var first = c.CheckAsync();
        await c.CheckAsync(); // secondo giro mentre il primo aspetta la risposta: esce subito
        Assert.Equal(1, asks);
        gate.SetResult(new IncomingFileDecision(IncomingFileChoice.Later));
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, asks); // dopo la risposta passa al file successivo
    }
}
