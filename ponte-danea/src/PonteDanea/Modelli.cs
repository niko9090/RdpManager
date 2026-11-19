namespace PonteDanea;

/// <summary>Una riga di TDocTestate: solo i campi che servono al pacchetto, come li dà l'archivio.</summary>
public sealed record DocumentoDanea(
    int IdDoc,
    string TipoDoc,
    int? IdAnagr,
    DateTime Data,
    string? NumDoc,
    string? NomeCliente,
    decimal? TotNetto,
    decimal? TotIva,
    decimal? TotDoc,
    bool Saldato,
    string? Pagamento,
    string? Descrizione = null,
    string? Vettore = null,
    string? CausaleTrasporto = null,
    string? NumColli = null,
    string? CodSpedizione = null,
    string? StatoOrdine = null,
    DateTime? DataPrevista = null,
    int? InclusoInIdDoc = null);

/// <summary>Una riga di TDocRighe. L'aliquota è già la percentuale (risolta da TIva), non il codice.</summary>
public sealed record RigaDanea(
    int IdRiga,
    int IdDoc,
    int? IdArticolo,
    string? CodArticolo,
    string? Descrizione,
    decimal? Quantita,
    string? Udm,
    decimal? Prezzo,
    string? Sconti,
    decimal? AliquotaIva,
    decimal? Importo);

/// <summary>Una riga di TPrimaNota. DataPagam può esserci anche con Saldato = false: la regola è nel mappatore.</summary>
public sealed record ScadenzaDanea(
    int IdPrimaNota,
    int IdDoc,
    DateTime? DataScad,
    DateTime? DataPagam,
    decimal? Importo,
    bool Saldato,
    string? Pagamento);

/// <summary>Una riga di TAnagrafica.</summary>
public sealed record SoggettoDanea(
    int IdAnagr,
    string? Codice,
    string? Nome,
    bool Cliente,
    bool Fornitore,
    string? PartitaIva,
    string? CodiceFiscale,
    string? Indirizzo,
    string? Cap,
    string? Citta,
    string? Provincia,
    string? Nazione,
    string? Telefono,
    string? Cellulare,
    string? Email,
    string? Pec,
    string? CodiceSdi,
    string? Referente,
    string? Sito,
    string? Note,
    string? Pagamento);

/// <summary>La lettura dell'archivio, separata dal resto: nei test la sostituisce una versione in memoria.</summary>
public interface IArchivioDanea
{
    /// <summary>Le fatture di vendita (I, J, N, F) emesse da <paramref name="da"/> (e fino a <paramref name="fino"/>, se dato),
    /// più quelle incassate in quel periodo.</summary>
    IReadOnlyList<DocumentoDanea> LeggiDocumenti(DateTime da, DateTime? fino);

    IReadOnlyList<RigaDanea> LeggiRighe(IReadOnlyCollection<int> idDocumenti);

    IReadOnlyList<ScadenzaDanea> LeggiScadenze(IReadOnlyCollection<int> idDocumenti);

    IReadOnlyList<SoggettoDanea> LeggiSoggetti(IReadOnlyCollection<int> idAnagrafiche);
}
