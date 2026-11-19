using PonteDanea;

namespace PonteDanea.Tests;

public class MappatoreTests
{
    private static string[] Righe(string csv) => csv.Split(FormatoGlos.AcapoRiga, StringSplitOptions.RemoveEmptyEntries);

    private static string[] Campi(string riga) => riga.Split(';');

    // ---- pagato_il solo con Saldato = 1

    [Fact]
    public void Scadenza_saldata_ha_pagato_il_e_importo_pagato()
    {
        var csv = Mappatore.Scadenze([new ScadenzaDanea(5, 100, new DateTime(2026, 10, 31), new DateTime(2026, 11, 3), 122.50m, true, "Bonifico")]);

        var righe = Righe(csv);
        var intestazione = Campi(righe[0]);
        var r = Campi(righe[1]);
        Assert.Equal("2026-11-03", r[Array.IndexOf(intestazione, "pagato_il")]);
        Assert.Equal("1", r[Array.IndexOf(intestazione, "saldato")]);
        Assert.Equal("122.50", r[Array.IndexOf(intestazione, "importo_pagato")]);
        Assert.Equal("2026-10-31", r[Array.IndexOf(intestazione, "scadenza")]);
    }

    [Fact]
    public void Scadenza_aperta_non_ha_pagato_il_anche_se_Danea_scrive_DataPagam()
    {
        // In Danea DataPagam c'è anche con Saldato = 0 (è la data prevista): GLOS la leggerebbe come un incasso.
        var csv = Mappatore.Scadenze([new ScadenzaDanea(6, 100, new DateTime(2027, 1, 31), new DateTime(2027, 1, 31), 122.50m, false, "RIBA")]);

        var righe = Righe(csv);
        var intestazione = Campi(righe[0]);
        var r = Campi(righe[1]);
        Assert.Equal("", r[Array.IndexOf(intestazione, "pagato_il")]);
        Assert.Equal("", r[Array.IndexOf(intestazione, "importo_pagato")]);
        Assert.Equal("0", r[Array.IndexOf(intestazione, "saldato")]);
        Assert.Equal("2027-01-31", r[Array.IndexOf(intestazione, "scadenza")]);
    }

    [Fact]
    public void Scadenza_saldata_senza_DataPagam_resta_vuota_senza_inventare_una_data()
    {
        var csv = Mappatore.Scadenze([new ScadenzaDanea(7, 100, new DateTime(2026, 10, 31), null, 10m, true, null)]);

        var righe = Righe(csv);
        Assert.Equal("", Campi(righe[1])[Array.IndexOf(Campi(righe[0]), "pagato_il")]);
    }

    // ---- nota di credito

    [Fact]
    public void Nota_di_credito_ha_tipo_N_nome_tipo_e_importi_col_segno_di_Danea()
    {
        var nota = new DocumentoDanea(12300, "N", 1481, new DateTime(2026, 11, 12), "142/2026", "MOTEL RIAZZINO", -47300m, 0m, -47300m, false, null);

        var righe = Righe(Mappatore.Documenti([nota]));
        var h = Campi(righe[0]);
        var r = Campi(righe[1]);
        Assert.Equal("N", r[Array.IndexOf(h, "tipo")]);
        Assert.Equal("Nota di credito", r[Array.IndexOf(h, "nome_tipo")]);
        Assert.Equal("142/2026", r[Array.IndexOf(h, "numero")]);
        Assert.Equal("-47300", r[Array.IndexOf(h, "totale")]);
        Assert.Equal("-47300", r[Array.IndexOf(h, "imponibile")]);
        Assert.Equal("2026-11-12", r[Array.IndexOf(h, "data")]);
    }

    [Theory]
    [InlineData("I", "Fattura")]
    [InlineData("J", "Fattura d'acconto")]
    [InlineData("N", "Nota di credito")]
    [InlineData("F", "Fattura accompagnatoria")]
    public void Nomi_dei_tipi_come_li_aspetta_GLOS(string tipo, string nome) => Assert.Equal(nome, Mappatore.NomeTipo(tipo));

    // ---- codifica, separatore, numeri e date

    [Fact]
    public void Csv_ha_separatore_punto_e_virgola_e_una_riga_di_intestazione_per_file()
    {
        var csv = Mappatore.Documenti([ArchivioFinto.Fattura(1)]);

        var righe = Righe(csv);
        Assert.Equal(2, righe.Length);
        Assert.Equal(FormatoGlos.ColonneDocumenti, Campi(righe[0]));
        Assert.Equal(FormatoGlos.ColonneDocumenti.Length, Campi(righe[1]).Length);
        Assert.StartsWith("danea_id;tipo;nome_tipo;numero;data;", righe[0]);
    }

    [Fact]
    public void Campi_con_separatore_virgolette_o_a_capo_vanno_tra_virgolette_raddoppiate()
    {
        var riga = new RigaDanea(1, 100, null, "A;B", "Vite \"M8\"; zincata\nlunga", 2m, "nr", 1.5m, null, 22m, 3m);

        var csv = Mappatore.Righe([riga]);

        Assert.Contains("\"A;B\"", csv);
        Assert.Contains("\"Vite \"\"M8\"\"; zincata\nlunga\"", csv);
    }

    [Fact]
    public void Byte_in_UTF8_con_BOM_e_accenti_intatti()
    {
        var csv = Mappatore.Soggetti([ArchivioFinto.Cliente(10, "Società Perché è più")]);

        var byteCsv = FormatoGlos.InByte(csv);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, byteCsv[..3]);
        var testo = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(byteCsv[3..]);
        Assert.Contains("Società Perché è più", testo);
        // «à» in UTF-8 sono due byte (C3 A0), non uno (E0 di WIN1252).
        Assert.Contains((byte)0xC3, byteCsv);
        Assert.DoesNotContain((byte)0xE0, byteCsv);
    }

    [Fact]
    public void Decimali_col_punto_e_date_AAAA_MM_GG_qualunque_sia_la_lingua_del_PC()
    {
        var vecchia = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
            Assert.Equal("1234.56", FormatoGlos.Numero(1234.56m));
            Assert.Equal("2026-03-07", FormatoGlos.Data(new DateTime(2026, 3, 7)));
            Assert.Equal("", FormatoGlos.Numero(null));
            Assert.Equal("", FormatoGlos.Data(null));
        }
        finally { Thread.CurrentThread.CurrentCulture = vecchia; }
    }

    [Fact]
    public void Riga_con_aliquota_mancante_lascia_il_campo_vuoto()
    {
        var csv = Mappatore.Righe([new RigaDanea(1, 100, 575, "COD", "Lampada", 20m, "nr", 6.4m, null, null, 128m)]);

        var righe = Righe(csv);
        var h = Campi(righe[0]);
        var r = Campi(righe[1]);
        Assert.Equal("", r[Array.IndexOf(h, "aliquota_iva")]);
        Assert.Equal("6.4", r[Array.IndexOf(h, "prezzo")]);
        Assert.Equal("128", r[Array.IndexOf(h, "importo")]);
    }

    [Fact]
    public void Cliente_e_fornitore_sono_0_o_1()
    {
        var s = ArchivioFinto.Cliente(10) with { Fornitore = true };

        var righe = Righe(Mappatore.Soggetti([s]));
        var h = Campi(righe[0]);
        var r = Campi(righe[1]);
        Assert.Equal("1", r[Array.IndexOf(h, "cliente")]);
        Assert.Equal("1", r[Array.IndexOf(h, "fornitore")]);
        Assert.Equal("01234567890", r[Array.IndexOf(h, "partita_iva")]);
    }
}
