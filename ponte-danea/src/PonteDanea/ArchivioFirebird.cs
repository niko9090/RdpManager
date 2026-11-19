using System.Data;
using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;

namespace PonteDanea;

/// <summary>
/// Legge l'archivio Easyfatt (Firebird 2.5, ODS 11.2) senza server, con Firebird embedded. Solo SELECT.
/// Di solito si lavora su una copia temporanea del file, cancellata alla fine (<see cref="ApriCopia"/>).
/// I nomi di tabelle e colonne di Easyfatt sono misti maiuscole/minuscole: vanno sempre tra virgolette.
/// </summary>
public sealed class ArchivioFirebird : IArchivioDanea, IDisposable
{
    private const int Blocco = 500;
    private const string TipiFattura = "'I','J','N','F'";

    private readonly FbConnection _conn;
    private readonly string? _fileTemporaneo;
    private Dictionary<string, decimal>? _aliquote;

    private ArchivioFirebird(string file, string libreria, string utente, string password, string? fileTemporaneo)
    {
        _fileTemporaneo = fileTemporaneo;
        var cs = new FbConnectionStringBuilder
        {
            Database = file,
            ClientLibrary = libreria,
            ServerType = FbServerType.Embedded,
            UserID = utente,
            Password = password,
            Charset = "UTF8", // l'archivio è WIN1252: Firebird traduce, i testi arrivano già in Unicode
            Pooling = false,
        }.ToString();
        _conn = new FbConnection(cs);
        _conn.Open();
    }

    /// <summary>Copia il file in una cartella temporanea e apre la copia: l'originale (magari aperto da Easyfatt) non si tocca.</summary>
    public static ArchivioFirebird ApriCopia(Configurazione c)
    {
        if (!File.Exists(c.Archivio)) throw new ErroreUtente($"L'archivio non c'è: {c.Archivio}.");
        if (!File.Exists(c.LibreriaFirebird)) throw new ErroreUtente($"Manca la libreria Firebird embedded: {c.LibreriaFirebird}.");

        var copia = Path.Combine(Path.GetTempPath(), "ponte-danea-" + Guid.NewGuid().ToString("N") + ".fdb");
        File.Copy(c.Archivio!, copia);
        try
        {
            // Windows carica la dll solo con percorso a barre rovesciate e completo.
            return new ArchivioFirebird(copia, Path.GetFullPath(c.LibreriaFirebird!), c.UtenteFirebird, c.PasswordFirebird, copia);
        }
        catch (Exception e)
        {
            TogliCopia(copia);
            throw new ErroreUtente($"Non riesco ad aprire l'archivio: {e.Message}");
        }
    }

    public IReadOnlyList<DocumentoDanea> LeggiDocumenti(DateTime da, DateTime? fino)
    {
        // Le fatture emesse nel periodo, e quelle (anche più vecchie) con un incasso registrato nel periodo.
        var sql = $@"select ""IDDoc"",""TipoDoc"",""IDAnagr"",""Data"",""NumDoc"",""Anagr_Nome"",""TotNetto"",""TotIva"",""TotDoc"",""Pagam_Saldato"",""Pagamento"",
 ""DescDoc"",""Vettore"",""CausaleTrasporto"",""NumColli"",""Trasp_CodSpedizione"",""StatoOrdine"",""DataPrevistaConclOrdine"",""InclusoInIDDoc""
 from ""TDocTestate""
 where ""TipoDoc"" in ({TipiFattura})
   and ((""Data"" >= @da {(fino.HasValue ? "and \"Data\" <= @fino" : "")})
        or ""IDDoc"" in (select ""IDDoc"" from ""TPrimaNota"" where ""Saldato"" = 1 and ""DataPagam"" >= @da {(fino.HasValue ? "and \"DataPagam\" <= @fino" : "")}))
 order by ""Data"", ""IDDoc""";
        return Leggi(sql, cmd =>
        {
            cmd.Parameters.Add("@da", FbDbType.Date).Value = da.Date;
            if (fino.HasValue) cmd.Parameters.Add("@fino", FbDbType.Date).Value = fino.Value.Date;
        }, r => new DocumentoDanea(
            IdDoc: r.GetInt32(0),
            TipoDoc: Testo(r, 1) ?? "",
            IdAnagr: Intero(r, 2),
            Data: r.GetDateTime(3),
            NumDoc: Testo(r, 4),
            NomeCliente: Testo(r, 5),
            TotNetto: Numero(r, 6),
            TotIva: Numero(r, 7),
            TotDoc: Numero(r, 8),
            Saldato: Intero(r, 9) == 1,
            Pagamento: Testo(r, 10),
            Descrizione: Testo(r, 11),
            Vettore: Testo(r, 12),
            CausaleTrasporto: Testo(r, 13),
            NumColli: Testo(r, 14),
            CodSpedizione: Testo(r, 15),
            StatoOrdine: Testo(r, 16),
            DataPrevista: Data(r, 17),
            InclusoInIdDoc: Intero(r, 18)));
    }

    public IReadOnlyList<RigaDanea> LeggiRighe(IReadOnlyCollection<int> idDocumenti)
    {
        var aliquote = Aliquote();
        return PerBlocchi(idDocumenti, elenco => Leggi(
            $@"select ""IDDocRiga"",""IDDoc"",""IDArticoloScaricato"",""CodArticolo"",""Desc"",""Qta"",""Udm"",""PrezzoNetto"",""Sconti"",""CodIva"",""ImportoNettoRiga""
 from ""TDocRighe"" where ""IDDoc"" in ({elenco}) order by ""IDDoc"", ""IDDocRiga""",
            null,
            r =>
            {
                var cod = Testo(r, 9);
                return new RigaDanea(r.GetInt32(0), r.GetInt32(1), Intero(r, 2), Testo(r, 3), Testo(r, 4), Numero(r, 5), Testo(r, 6),
                    Numero(r, 7), Testo(r, 8), cod is not null && aliquote.TryGetValue(cod, out var p) ? p : null, Numero(r, 10));
            }));
    }

    public IReadOnlyList<ScadenzaDanea> LeggiScadenze(IReadOnlyCollection<int> idDocumenti) =>
        PerBlocchi(idDocumenti, elenco => Leggi(
            $@"select ""IDPrimaNota"",""IDDoc"",""DataScad"",""DataPagam"",""Importo"",""Saldato"",""NomePagamDoc""
 from ""TPrimaNota"" where ""IDDoc"" in ({elenco}) order by ""IDDoc"", ""IDPrimaNota""",
            null,
            r => new ScadenzaDanea(r.GetInt32(0), r.GetInt32(1), Data(r, 2), Data(r, 3), Numero(r, 4), Intero(r, 5) == 1, Testo(r, 6))));

    public IReadOnlyList<SoggettoDanea> LeggiSoggetti(IReadOnlyCollection<int> idAnagrafiche) =>
        PerBlocchi(idAnagrafiche, elenco => Leggi(
            $@"select ""IDAnagr"",""CodAnagr"",""Nome"",""Cliente"",""Fornitore"",""PartitaIva"",""CodiceFiscale"",""Indirizzo"",""Cap"",""Citta"",""Prov"",""Nazione"",
 ""Tel"",""Cell"",""Email"",""Pec"",""FE_CodUfficio"",""Referente"",""HomePage"",""Note"",""PagamentoDefault""
 from ""TAnagrafica"" where ""IDAnagr"" in ({elenco}) order by ""IDAnagr""",
            null,
            r => new SoggettoDanea(r.GetInt32(0), Testo(r, 1), Testo(r, 2), Intero(r, 3) == 1, Intero(r, 4) == 1, Testo(r, 5), Testo(r, 6),
                Testo(r, 7), Testo(r, 8), Testo(r, 9), Testo(r, 10), Testo(r, 11), Testo(r, 12), Testo(r, 13), Testo(r, 14), Testo(r, 15),
                Testo(r, 16), Testo(r, 17), Testo(r, 18), Testo(r, 19), Testo(r, 20))));

    /// <summary>Codice IVA di Danea («22», «E10», «22sp»…) → percentuale, da TIva.</summary>
    private Dictionary<string, decimal> Aliquote() => _aliquote ??= Leggi(
        @"select ""CodIva"",""PercIva"" from ""TIva""", null,
        r => (Cod: Testo(r, 0), Perc: Numero(r, 1))).Where(x => x.Cod is not null && x.Perc.HasValue)
        .GroupBy(x => x.Cod!).ToDictionary(g => g.Key, g => g.First().Perc!.Value);

    private List<T> Leggi<T>(string sql, Action<FbCommand>? parametri, Func<DbDataReader, T> mappa)
    {
        using var cmd = new FbCommand(sql, _conn);
        parametri?.Invoke(cmd);
        using var r = cmd.ExecuteReader();
        var lista = new List<T>();
        while (r.Read()) lista.Add(mappa(r));
        return lista;
    }

    /// <summary>Gli id sono interi: si scrivono nell'SQL (nessuna iniezione possibile), a blocchi per i limiti di Firebird.</summary>
    private static List<T> PerBlocchi<T>(IReadOnlyCollection<int> id, Func<string, List<T>> leggi)
    {
        var tutti = new List<T>();
        foreach (var blocco in id.Distinct().Chunk(Blocco))
            tutti.AddRange(leggi(string.Join(",", blocco)));
        return tutti;
    }

    private static string? Testo(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i))?.TrimEnd();

    private static int? Intero(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    private static decimal? Numero(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    private static DateTime? Data(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);

    public void Dispose()
    {
        _conn.Dispose();
        FbConnection.ClearAllPools();
        if (_fileTemporaneo is not null) TogliCopia(_fileTemporaneo);
    }

    private static void TogliCopia(string file)
    {
        try { File.Delete(file); } catch (IOException) { /* si cancella al prossimo giro: sta nella cartella temporanea */ }
    }
}
