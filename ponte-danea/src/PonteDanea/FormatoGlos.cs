using System.Globalization;
using System.Text;

namespace PonteDanea;

/// <summary>
/// Il formato dei CSV che GLOS si aspetta (gestionale 2.140.x, DaneaPacchetto / DaneaDocumenti):
/// separatore «;», UTF-8 con BOM, prima riga = intestazione, campi con ; " o a capo tra virgolette
/// (le virgolette interne raddoppiate), date AAAA-MM-GG, decimali col punto, booleani 0/1.
/// </summary>
public static class FormatoGlos
{
    public const char Separatore = ';';

    public const string AcapoRiga = "\r\n";

    public const string FileDocumenti = "documenti.csv";
    public const string FileRighe = "righe.csv";
    public const string FileScadenze = "scadenze.csv";
    public const string FileSoggetti = "soggetti.csv";
    public const string FileSedi = "sedi.csv";
    public const string FileArticoli = "articoli.csv";
    public const string FileInfo = "info.json";

    /// <summary>I sei CSV, nell'ordine in cui entrano nello zip.</summary>
    public static readonly string[] FileCsv = [FileDocumenti, FileRighe, FileScadenze, FileSoggetti, FileSedi, FileArticoli];

    public static readonly string[] ColonneDocumenti =
    [
        "danea_id", "tipo", "nome_tipo", "numero", "data", "danea_id_soggetto", "cliente", "imponibile", "iva", "totale", "saldato",
        "stato_ordine", "annullato", "data_prevista", "pagamento", "vettore", "codice_spedizione", "descrizione", "causale_trasporto", "colli", "incluso_in",
    ];

    public static readonly string[] ColonneRighe =
    [
        "danea_id", "danea_id_documento", "danea_id_articolo", "codice_articolo", "descrizione", "quantita", "unita", "prezzo", "sconti", "aliquota_iva", "importo",
    ];

    public static readonly string[] ColonneScadenze =
    [
        "danea_id", "danea_id_documento", "scadenza", "pagato_il", "importo", "saldato", "importo_pagato", "pagamento",
    ];

    public static readonly string[] ColonneSoggetti =
    [
        "danea_id", "nome", "codice", "cliente", "fornitore", "partita_iva", "codice_fiscale", "indirizzo", "cap", "citta", "provincia",
        "nazione", "telefono", "cellulare", "email", "pec", "codice_sdi", "referente", "sito", "note", "pagamento",
    ];

    public static readonly string[] ColonneSedi =
    [
        "danea_id_soggetto", "codice_sede", "tipo", "nome", "indirizzo", "cap", "citta", "provincia",
    ];

    public static readonly string[] ColonneArticoli =
    [
        "danea_id", "codice", "descrizione", "unita", "prezzo", "prezzo_acquisto", "aliquota_iva", "danea_id_fornitore", "codice_fornitore", "barcode", "categoria",
    ];

    /// <summary>Il testo di un CSV (senza BOM: lo aggiunge <see cref="InByte"/>).</summary>
    public static string Csv(IReadOnlyList<string> colonne, IEnumerable<IReadOnlyList<string>> righe)
    {
        var sb = new StringBuilder();
        sb.Append(Riga(colonne)).Append(AcapoRiga);
        foreach (var r in righe)
        {
            if (r.Count != colonne.Count)
                throw new InvalidOperationException($"Riga con {r.Count} campi, attesi {colonne.Count}.");
            sb.Append(Riga(r)).Append(AcapoRiga);
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 con BOM (GLOS lo toglie se c'è, e legge i byte come UTF-8).</summary>
    public static byte[] InByte(string testo) =>
        [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(testo)];

    public static string Riga(IEnumerable<string> campi) => string.Join(Separatore, campi.Select(Campo));

    public static string Campo(string? valore)
    {
        valore ??= "";
        // Danea tiene spazi in coda ai testi: GLOS li taglia comunque, qui si tolgono per non sporcare l'impronta.
        valore = valore.Trim();
        return valore.IndexOfAny([Separatore, '"', '\r', '\n']) >= 0
            ? "\"" + valore.Replace("\"", "\"\"") + "\""
            : valore;
    }

    public static string Data(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static string Numero(decimal? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Intero(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Booleano(bool b) => b ? "1" : "0";
}
