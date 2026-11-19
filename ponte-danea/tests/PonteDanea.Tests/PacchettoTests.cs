using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PonteDanea;

namespace PonteDanea.Tests;

public class PacchettoTests
{
    private static readonly InfoPacchetto Info = new("1.0.0", "PC-UFFICIO", "GL OS SRL.eft");

    private static ArchivioFinto ArchivioConUnaFattura()
    {
        var a = new ArchivioFinto();
        a.Documenti.Add(ArchivioFinto.Fattura(100));
        a.Righe.Add(new RigaDanea(1, 100, 575, "COD", "Lampada", 2m, "nr", 6.4m, null, 22m, 12.8m));
        a.Righe.Add(new RigaDanea(2, 100, null, null, "Trasporto", 1m, null, 5m, null, 22m, 5m));
        a.Scadenze.Add(new ScadenzaDanea(9, 100, new DateTime(2026, 12, 31), new DateTime(2026, 12, 31), 122m, false, "RIBA"));
        a.Soggetti.Add(ArchivioFinto.Cliente(10));
        return a;
    }

    private static Dictionary<string, byte[]> Apri(byte[] zip)
    {
        using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return z.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    [Fact]
    public void Lo_zip_ha_i_sei_csv_e_info_json_alla_radice_senza_cartelle()
    {
        var p = CostruttorePacchetto.Costruisci(ArchivioConUnaFattura(), new DateTime(2026, 11, 1), null, Info);

        var file = Apri(p.Zip);

        Assert.Equal(
            ["documenti.csv", "righe.csv", "scadenze.csv", "soggetti.csv", "sedi.csv", "articoli.csv", "info.json"],
            file.Keys.ToArray());
        Assert.All(file.Keys, n => Assert.DoesNotContain('/', n));
        Assert.All(file.Keys, n => Assert.DoesNotContain('\\', n));
    }

    [Fact]
    public void Ogni_csv_dello_zip_comincia_col_BOM_e_con_le_colonne_attese()
    {
        var p = CostruttorePacchetto.Costruisci(ArchivioConUnaFattura(), new DateTime(2026, 11, 1), null, Info);
        var file = Apri(p.Zip);
        var attese = new Dictionary<string, string[]>
        {
            ["documenti.csv"] = FormatoGlos.ColonneDocumenti,
            ["righe.csv"] = FormatoGlos.ColonneRighe,
            ["scadenze.csv"] = FormatoGlos.ColonneScadenze,
            ["soggetti.csv"] = FormatoGlos.ColonneSoggetti,
            ["sedi.csv"] = FormatoGlos.ColonneSedi,
            ["articoli.csv"] = FormatoGlos.ColonneArticoli,
        };

        foreach (var (nome, colonne) in attese)
        {
            var b = file[nome];
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, b[..3]);
            var prima = Encoding.UTF8.GetString(b[3..]).Split("\r\n")[0];
            Assert.Equal(string.Join(';', colonne), prima);
        }
        // Le colonne che GLOS pretende di trovare (DaneaArrivo::OBBLIGATORI).
        Assert.Contains("danea_id", FormatoGlos.ColonneDocumenti);
        Assert.Contains("tipo", FormatoGlos.ColonneDocumenti);
        Assert.Contains("numero", FormatoGlos.ColonneDocumenti);
        Assert.Contains("danea_id_documento", FormatoGlos.ColonneScadenze);
    }

    [Fact]
    public void Righe_di_dati_nei_csv_e_conteggi()
    {
        var p = CostruttorePacchetto.Costruisci(ArchivioConUnaFattura(), new DateTime(2026, 11, 1), null, Info);
        var file = Apri(p.Zip);

        static string[] Linee(byte[] b) => Encoding.UTF8.GetString(b[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, Linee(file["documenti.csv"]).Length);   // intestazione + 1
        Assert.Equal(3, Linee(file["righe.csv"]).Length);
        Assert.Equal(2, Linee(file["scadenze.csv"]).Length);
        Assert.Equal(2, Linee(file["soggetti.csv"]).Length);
        Assert.Single(Linee(file["sedi.csv"]));                 // solo intestazione
        Assert.Single(Linee(file["articoli.csv"]));
        Assert.Equal((1, 2, 1, 1), (p.Fatture, p.Righe, p.Scadenze, p.Soggetti));
    }

    [Fact]
    public void Info_json_dice_chi_manda_il_pacchetto()
    {
        var p = CostruttorePacchetto.Costruisci(ArchivioConUnaFattura(), new DateTime(2026, 11, 1), null, Info);

        using var json = JsonDocument.Parse(Apri(p.Zip)["info.json"]);

        Assert.Equal("1.0.0", json.RootElement.GetProperty("versione_programma").GetString());
        Assert.Equal("PC-UFFICIO", json.RootElement.GetProperty("nome_pc").GetString());
        Assert.Equal("GL OS SRL.eft", json.RootElement.GetProperty("percorso_archivio").GetString());
    }

    [Fact]
    public void Nello_zip_entrano_solo_fatture_di_vendita_non_ordini_ddt_o_acquisti()
    {
        var a = ArchivioConUnaFattura();
        foreach (var tipo in new[] { "C", "D", "E", "U", "V", "Q" })
            a.Documenti.Add(ArchivioFinto.Fattura(200 + tipo[0], tipo));
        a.Documenti.Add(ArchivioFinto.Fattura(300, "N"));

        var p = CostruttorePacchetto.Costruisci(a, new DateTime(2026, 11, 1), null, Info);

        Assert.Equal(2, p.Fatture); // I e N
    }

    [Fact]
    public void Il_filtro_di_date_si_applica_e_i_clienti_sono_solo_quelli_delle_fatture()
    {
        var a = ArchivioConUnaFattura();
        a.Documenti.Add(ArchivioFinto.Fattura(101, data: new DateTime(2026, 5, 1), anagr: 77)); // fuori periodo
        a.Soggetti.Add(ArchivioFinto.Cliente(77, "Vecchio Cliente"));
        a.Righe.Add(new RigaDanea(50, 101, null, null, "Riga vecchia", 1m, null, 1m, null, 22m, 1m));

        var p = CostruttorePacchetto.Costruisci(a, new DateTime(2026, 11, 1), null, Info);

        Assert.Equal(1, p.Fatture);
        Assert.Equal(2, p.Righe);
        Assert.Equal(1, p.Soggetti);
        Assert.Equal(new DateTime(2026, 11, 1), a.DaRicevuto);
        Assert.Equal([10], a.AnagrafichePreviste!.ToArray());
    }

    [Fact]
    public void Senza_fatture_lo_zip_e_comunque_valido_con_le_sole_intestazioni()
    {
        var p = CostruttorePacchetto.Costruisci(new ArchivioFinto(), new DateTime(2026, 11, 1), null, Info);

        var file = Apri(p.Zip);

        Assert.Equal(0, p.Fatture);
        Assert.Equal(7, file.Count);
        Assert.All(FormatoGlos.FileCsv, n => Assert.True(file[n].Length > 3));
    }
}
