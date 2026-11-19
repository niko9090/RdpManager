using System.Net;
using System.Text;
using PonteDanea;

namespace PonteDanea.Tests;

public class SpedizioneEConfigurazioneTests
{
    private sealed class Gestore(Func<HttpRequestMessage, HttpResponseMessage> risposta) : HttpMessageHandler
    {
        public HttpRequestMessage? Richiesta { get; private set; }
        public string? Corpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Richiesta = request;
            Corpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return risposta(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode stato, string json) =>
        new(stato) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly InfoPacchetto Info = new("1.0.0", "PC", null);

    [Fact]
    public async Task Invia_lo_zip_nel_campo_pacchetto_con_il_token_Bearer()
    {
        var g = new Gestore(_ => Json(HttpStatusCode.Accepted, """{"ok":true,"esito":"ricevuto","messaggio":"Pacchetto ricevuto."}"""));

        var esito = await new Spedizione(new HttpClient(g)).InviaAsync(new Uri("https://glos.example/api/danea/pacchetto"), "dnt_segreto", [1, 2, 3], Info);

        Assert.True(esito.Ok);
        Assert.Equal("ricevuto", esito.Esito);
        Assert.Equal(HttpMethod.Post, g.Richiesta!.Method);
        Assert.Equal("https://glos.example/api/danea/pacchetto", g.Richiesta.RequestUri!.ToString());
        Assert.Equal("Bearer", g.Richiesta.Headers.Authorization!.Scheme);
        Assert.Equal("dnt_segreto", g.Richiesta.Headers.Authorization.Parameter);
        Assert.Contains("name=pacchetto", g.Corpo);
        Assert.Contains("filename=pacchetto.zip", g.Corpo);
        Assert.Contains("name=computer", g.Corpo);
    }

    [Fact]
    public async Task Pacchetto_rifiutato_da_GLOS_non_e_un_successo()
    {
        var g = new Gestore(_ => Json(HttpStatusCode.UnprocessableEntity, """{"ok":false,"esito":"rifiutato","messaggio":"Nel pacchetto manca documenti.csv."}"""));

        var esito = await new Spedizione(new HttpClient(g)).InviaAsync(new Uri("https://glos.example/api/danea/pacchetto"), "t", [1], Info);

        Assert.False(esito.Ok);
        Assert.Equal(422, esito.Stato);
        Assert.Equal("Nel pacchetto manca documenti.csv.", esito.Messaggio);
    }

    [Fact]
    public async Task Token_non_valido_401_si_legge_dal_campo_message()
    {
        var g = new Gestore(_ => Json(HttpStatusCode.Unauthorized, """{"message":"Token non valido o revocato."}"""));

        var esito = await new Spedizione(new HttpClient(g)).InviaAsync(new Uri("https://glos.example/api/danea/pacchetto"), "sbagliato", [1], Info);

        Assert.False(esito.Ok);
        Assert.Equal(401, esito.Stato);
        Assert.Equal("Token non valido o revocato.", esito.Messaggio);
    }

    [Fact]
    public void Per_la_prova_servono_solo_archivio_e_libreria_non_indirizzo_e_token()
    {
        var c = new Configurazione { Archivio = "a.eft", LibreriaFirebird = "fbclient.dll" };

        Assert.Empty(c.Mancanti(prova: true));
        Assert.Equal(["url", "token"], c.Mancanti(prova: false));
    }

    [Fact]
    public void Indirizzo_del_pacchetto_si_costruisce_dalla_base_con_o_senza_barra()
    {
        Assert.Equal("https://glos.example/api/danea/pacchetto", new Configurazione { Url = "https://glos.example/" }.IndirizzoPacchetto().ToString());
        Assert.Equal("https://glos.example/api/danea/pacchetto", new Configurazione { Url = "https://glos.example" }.IndirizzoPacchetto().ToString());
        Assert.Throws<ErroreUtente>(() => new Configurazione { Url = "ftp://x" }.IndirizzoPacchetto());
    }

    [Fact]
    public void Configurazione_si_legge_da_JSON_con_i_valori_di_default()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, """{ "archivio": "C:/x.eft", "libreriaFirebird": "C:/fb/fbclient.dll", "url": "https://g.example", "token": "dnt_x" }""");

            var c = Configurazione.Carica(f);

            Assert.Equal("C:/x.eft", c.Archivio);
            Assert.Equal("SYSDBA", c.UtenteFirebird);
            Assert.Equal(30, c.GiorniIndietro);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task Con_prova_lo_zip_si_scrive_e_non_si_contatta_nessun_indirizzo()
    {
        // Senza archivio vero la prova si ferma all'apertura: ma prima non deve chiedere url né token.
        var o = new StringWriter();
        var e = new StringWriter();
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, """{ "archivio": "non-esiste.eft", "libreriaFirebird": "non-esiste.dll" }""");

            var codice = await Avvio.EseguiAsync(["--config", f, "--prova"], o, e);

            Assert.Equal(1, codice);
            Assert.DoesNotContain("url", e.ToString());
            Assert.DoesNotContain("token", e.ToString());
            Assert.Contains("L'archivio non c'è", e.ToString());
        }
        finally { File.Delete(f); }
    }

    [Theory]
    [InlineData("--giorni", "0")]
    [InlineData("--giorni", "x")]
    [InlineData("--da", "01/02/2026")]
    [InlineData("--boh", "1")]
    public void Opzioni_sbagliate_sono_un_errore_chiaro(string opzione, string valore) =>
        Assert.Throws<ErroreUtente>(() => Opzioni.Leggi([opzione, valore]));

    [Fact]
    public void Opzioni_prova_giorni_e_zip()
    {
        var o = Opzioni.Leggi(["--prova", "--giorni", "7", "--zip", "x.zip"]);

        Assert.True(o.Prova);
        Assert.Equal(7, o.Giorni);
        Assert.Equal("x.zip", o.Zip);
    }
}
