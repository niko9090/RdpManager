using System.Text.Json;
using System.Text.Json.Serialization;

namespace PonteDanea;

/// <summary>Il file di configurazione (JSON). Indirizzo, token e percorso dell'archivio stanno qui, mai nel codice.</summary>
public sealed class Configurazione
{
    /// <summary>Il file .eft di Easyfatt (si lavora su una copia temporanea, l'originale non si tocca).</summary>
    [JsonPropertyName("archivio")] public string? Archivio { get; set; }

    /// <summary>Il fbclient.dll di Firebird 2.5 embedded (fbembed.dll rinominato), con accanto le sue dll.</summary>
    [JsonPropertyName("libreriaFirebird")] public string? LibreriaFirebird { get; set; }

    [JsonPropertyName("utenteFirebird")] public string UtenteFirebird { get; set; } = "SYSDBA";

    [JsonPropertyName("passwordFirebird")] public string PasswordFirebird { get; set; } = "masterkey";

    /// <summary>L'indirizzo di GLOS (es. https://gestionale.esempio.it): ci si aggiunge /api/danea/pacchetto.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>Il token creato in GLOS, Impostazioni → Danea (dnt_…).</summary>
    [JsonPropertyName("token")] public string? Token { get; set; }

    [JsonPropertyName("nomePc")] public string? NomePc { get; set; }

    /// <summary>Di default si prendono le fatture degli ultimi N giorni.</summary>
    [JsonPropertyName("giorniIndietro")] public int GiorniIndietro { get; set; } = 30;

    public static Configurazione Carica(string percorso)
    {
        if (!File.Exists(percorso))
            throw new ErroreUtente($"Manca il file di configurazione: {percorso}. Si parte da ponte-danea.esempio.json.");
        try
        {
            return JsonSerializer.Deserialize<Configurazione>(File.ReadAllText(percorso), new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new ErroreUtente("Il file di configurazione è vuoto.");
        }
        catch (JsonException e)
        {
            throw new ErroreUtente($"Il file di configurazione non è JSON valido: {e.Message}");
        }
    }

    /// <summary>I campi che mancano. Per la prova bastano l'archivio e la libreria: niente indirizzo né token.</summary>
    public IReadOnlyList<string> Mancanti(bool prova)
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(Archivio)) m.Add("archivio");
        if (string.IsNullOrWhiteSpace(LibreriaFirebird)) m.Add("libreriaFirebird");
        if (!prova)
        {
            if (string.IsNullOrWhiteSpace(Url)) m.Add("url");
            if (string.IsNullOrWhiteSpace(Token)) m.Add("token");
        }
        return m;
    }

    public Uri IndirizzoPacchetto()
    {
        var base_ = Url!.Trim().TrimEnd('/');
        if (!Uri.TryCreate(base_ + "/api/danea/pacchetto", UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw new ErroreUtente($"L'indirizzo «{Url}» non è valido: serve http:// o https://.");
        return u;
    }
}

/// <summary>Un errore da dire all'utente in parole semplici, senza traccia dello stack.</summary>
public sealed class ErroreUtente(string messaggio) : Exception(messaggio);
