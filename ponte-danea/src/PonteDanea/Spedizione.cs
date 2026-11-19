using System.Net.Http.Headers;
using System.Text.Json;

namespace PonteDanea;

public sealed record EsitoSpedizione(bool Ok, int Stato, string Esito, string Messaggio);

/// <summary>POST /api/danea/pacchetto: campo «pacchetto» (lo zip), token nell'intestazione Authorization: Bearer.</summary>
public sealed class Spedizione(HttpClient http)
{
    public async Task<EsitoSpedizione> InviaAsync(Uri indirizzo, string token, byte[] zip, InfoPacchetto info, CancellationToken ct = default)
    {
        using var corpo = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        corpo.Add(file, "pacchetto", "pacchetto.zip");
        corpo.Add(new StringContent(info.VersioneProgramma), "versione_programma");
        corpo.Add(new StringContent(info.NomePc), "computer");

        using var richiesta = new HttpRequestMessage(HttpMethod.Post, indirizzo) { Content = corpo };
        richiesta.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        richiesta.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var risposta = await http.SendAsync(richiesta, ct);
        var testo = await risposta.Content.ReadAsStringAsync(ct);
        var stato = (int)risposta.StatusCode;
        try
        {
            using var doc = JsonDocument.Parse(testo);
            var r = doc.RootElement;
            var ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var esito = r.TryGetProperty("esito", out var e) ? e.GetString() ?? "" : "";
            var messaggio = r.TryGetProperty("messaggio", out var m) ? m.GetString()
                : r.TryGetProperty("message", out var m2) ? m2.GetString() : null;
            return new EsitoSpedizione(ok && risposta.IsSuccessStatusCode, stato, esito, messaggio ?? risposta.ReasonPhrase ?? "");
        }
        catch (JsonException)
        {
            return new EsitoSpedizione(false, stato, "", $"Risposta non leggibile ({stato}).");
        }
    }
}
