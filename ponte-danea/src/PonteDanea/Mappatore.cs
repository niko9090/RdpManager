namespace PonteDanea;

/// <summary>Dalle righe dell'archivio ai testi dei CSV di GLOS. Nessun accesso a Firebird: si prova con dati finti.</summary>
public static class Mappatore
{
    /// <summary>Tipi di documento di Danea che GLOS tratta come fatture di vendita.</summary>
    public static readonly IReadOnlyDictionary<string, string> TipiFattura = new Dictionary<string, string>
    {
        ["I"] = "Fattura",
        ["J"] = "Fattura d'acconto",
        ["N"] = "Nota di credito",
        ["F"] = "Fattura accompagnatoria",
    };

    public static string NomeTipo(string tipo) => TipiFattura.TryGetValue(tipo, out var n) ? n : tipo;

    public static string Documenti(IEnumerable<DocumentoDanea> documenti) =>
        FormatoGlos.Csv(FormatoGlos.ColonneDocumenti, documenti.Select(d => (IReadOnlyList<string>)
        [
            FormatoGlos.Intero(d.IdDoc),
            d.TipoDoc,
            NomeTipo(d.TipoDoc),
            d.NumDoc ?? "",
            FormatoGlos.Data(d.Data),
            FormatoGlos.Intero(d.IdAnagr),
            d.NomeCliente ?? "",
            FormatoGlos.Numero(d.TotNetto),   // le note di credito restano col segno di Danea: GLOS usa il valore assoluto e applica il suo segno
            FormatoGlos.Numero(d.TotIva),
            FormatoGlos.Numero(d.TotDoc),
            FormatoGlos.Booleano(d.Saldato),
            d.StatoOrdine ?? "",
            "0",
            FormatoGlos.Data(d.DataPrevista),
            d.Pagamento ?? "",
            d.Vettore ?? "",
            d.CodSpedizione ?? "",
            d.Descrizione ?? "",
            d.CausaleTrasporto ?? "",
            d.NumColli ?? "",
            FormatoGlos.Intero(d.InclusoInIdDoc),
        ]));

    public static string Righe(IEnumerable<RigaDanea> righe) =>
        FormatoGlos.Csv(FormatoGlos.ColonneRighe, righe.Select(r => (IReadOnlyList<string>)
        [
            FormatoGlos.Intero(r.IdRiga),
            FormatoGlos.Intero(r.IdDoc),
            FormatoGlos.Intero(r.IdArticolo),
            r.CodArticolo ?? "",
            r.Descrizione ?? "",
            FormatoGlos.Numero(r.Quantita),
            r.Udm ?? "",
            FormatoGlos.Numero(r.Prezzo),
            r.Sconti ?? "",
            FormatoGlos.Numero(r.AliquotaIva),
            FormatoGlos.Numero(r.Importo),
        ]));

    /// <summary>
    /// pagato_il (e importo_pagato) si riempiono SOLO con Saldato = 1: in Danea DataPagam c'è anche sulle scadenze ancora aperte
    /// (è la data prevista), e GLOS la leggerebbe come incasso.
    /// </summary>
    public static string Scadenze(IEnumerable<ScadenzaDanea> scadenze) =>
        FormatoGlos.Csv(FormatoGlos.ColonneScadenze, scadenze.Select(s => (IReadOnlyList<string>)
        [
            FormatoGlos.Intero(s.IdPrimaNota),
            FormatoGlos.Intero(s.IdDoc),
            FormatoGlos.Data(s.DataScad),
            s.Saldato ? FormatoGlos.Data(s.DataPagam) : "",
            FormatoGlos.Numero(s.Importo),
            FormatoGlos.Booleano(s.Saldato),
            s.Saldato ? FormatoGlos.Numero(s.Importo) : "",
            s.Pagamento ?? "",
        ]));

    public static string Soggetti(IEnumerable<SoggettoDanea> soggetti) =>
        FormatoGlos.Csv(FormatoGlos.ColonneSoggetti, soggetti.Select(s => (IReadOnlyList<string>)
        [
            FormatoGlos.Intero(s.IdAnagr),
            s.Nome ?? "",
            s.Codice ?? "",
            FormatoGlos.Booleano(s.Cliente),
            FormatoGlos.Booleano(s.Fornitore),
            s.PartitaIva ?? "",
            s.CodiceFiscale ?? "",
            s.Indirizzo ?? "",
            s.Cap ?? "",
            s.Citta ?? "",
            s.Provincia ?? "",
            s.Nazione ?? "",
            s.Telefono ?? "",
            s.Cellulare ?? "",
            s.Email ?? "",
            s.Pec ?? "",
            s.CodiceSdi ?? "",
            s.Referente ?? "",
            s.Sito ?? "",
            s.Note ?? "",
            s.Pagamento ?? "",
        ]));
}
