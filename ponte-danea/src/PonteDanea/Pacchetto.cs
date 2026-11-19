using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PonteDanea;

/// <summary>Lo zip pronto da spedire, con i numeri che servono al resoconto.</summary>
public sealed record Pacchetto(byte[] Zip, int Fatture, int Righe, int Scadenze, int Soggetti);

public sealed record InfoPacchetto(string VersioneProgramma, string NomePc, string? PercorsoArchivio);

public static class CostruttorePacchetto
{
    public static Pacchetto Costruisci(IArchivioDanea archivio, DateTime da, DateTime? fino, InfoPacchetto info)
    {
        var documenti = archivio.LeggiDocumenti(da, fino)
            .Where(d => Mappatore.TipiFattura.ContainsKey(d.TipoDoc))
            .OrderBy(d => d.Data).ThenBy(d => d.IdDoc)
            .ToList();
        var idDoc = documenti.Select(d => d.IdDoc).ToHashSet();

        var righe = archivio.LeggiRighe(idDoc).Where(r => idDoc.Contains(r.IdDoc)).OrderBy(r => r.IdDoc).ThenBy(r => r.IdRiga).ToList();
        var scadenze = archivio.LeggiScadenze(idDoc).Where(s => idDoc.Contains(s.IdDoc)).OrderBy(s => s.IdDoc).ThenBy(s => s.IdPrimaNota).ToList();

        // Solo i clienti che hanno ricevuto queste fatture: senza il cliente in soggetti.csv GLOS salta la fattura.
        var idAnagr = documenti.Where(d => d.IdAnagr.HasValue).Select(d => d.IdAnagr!.Value).ToHashSet();
        var soggetti = archivio.LeggiSoggetti(idAnagr).Where(s => idAnagr.Contains(s.IdAnagr)).OrderBy(s => s.IdAnagr).ToList();

        var contenuti = new Dictionary<string, string>
        {
            [FormatoGlos.FileDocumenti] = Mappatore.Documenti(documenti),
            [FormatoGlos.FileRighe] = Mappatore.Righe(righe),
            [FormatoGlos.FileScadenze] = Mappatore.Scadenze(scadenze),
            [FormatoGlos.FileSoggetti] = Mappatore.Soggetti(soggetti),
            // Sedi e articoli: GLOS li accetta solo come facoltativi; questo ponte non li legge (resta l'intestazione).
            [FormatoGlos.FileSedi] = FormatoGlos.Csv(FormatoGlos.ColonneSedi, []),
            [FormatoGlos.FileArticoli] = FormatoGlos.Csv(FormatoGlos.ColonneArticoli, []),
        };

        return new Pacchetto(Zip(contenuti, info), documenti.Count, righe.Count, scadenze.Count, soggetti.Count);
    }

    /// <summary>Tutti i file alla radice dello zip (GLOS rifiuta i percorsi), CSV in UTF-8 con BOM e info.json.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, string> csv, InfoPacchetto info)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var nome in FormatoGlos.FileCsv)
                Scrivi(zip, nome, FormatoGlos.InByte(csv[nome]));

            var json = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["versione_programma"] = info.VersioneProgramma,
                ["nome_pc"] = info.NomePc,
                ["percorso_archivio"] = info.PercorsoArchivio,
            }, new JsonSerializerOptions { Encoder =System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Scrivi(zip, FormatoGlos.FileInfo, new UTF8Encoding(false).GetBytes(json));
        }
        return ms.ToArray();
    }

    private static void Scrivi(ZipArchive zip, string nome, byte[] byteFile)
    {
        var voce = zip.CreateEntry(nome, CompressionLevel.Optimal);
        using var s = voce.Open();
        s.Write(byteFile);
    }
}
