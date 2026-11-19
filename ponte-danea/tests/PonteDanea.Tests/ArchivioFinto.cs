using PonteDanea;

namespace PonteDanea.Tests;

/// <summary>L'archivio in memoria: sostituisce Firebird nei test.</summary>
public sealed class ArchivioFinto : IArchivioDanea
{
    public List<DocumentoDanea> Documenti { get; } = [];
    public List<RigaDanea> Righe { get; } = [];
    public List<ScadenzaDanea> Scadenze { get; } = [];
    public List<SoggettoDanea> Soggetti { get; } = [];

    public DateTime? DaRicevuto { get; private set; }
    public IReadOnlyCollection<int>? AnagrafichePreviste { get; private set; }

    public IReadOnlyList<DocumentoDanea> LeggiDocumenti(DateTime da, DateTime? fino)
    {
        DaRicevuto = da;
        return Documenti.Where(d => d.Data >= da && (fino is null || d.Data <= fino)).ToList();
    }

    public IReadOnlyList<RigaDanea> LeggiRighe(IReadOnlyCollection<int> idDocumenti) => Righe.Where(r => idDocumenti.Contains(r.IdDoc)).ToList();

    public IReadOnlyList<ScadenzaDanea> LeggiScadenze(IReadOnlyCollection<int> idDocumenti) => Scadenze.Where(s => idDocumenti.Contains(s.IdDoc)).ToList();

    public IReadOnlyList<SoggettoDanea> LeggiSoggetti(IReadOnlyCollection<int> idAnagrafiche)
    {
        AnagrafichePreviste = idAnagrafiche;
        return Soggetti.Where(s => idAnagrafiche.Contains(s.IdAnagr)).ToList();
    }

    public static DocumentoDanea Fattura(int id, string tipo = "I", DateTime? data = null, int anagr = 10, decimal totale = 122m) =>
        new(id, tipo, anagr, data ?? new DateTime(2026, 11, 10), $"{id}/2026", "Rossi Srl", totale / 1.22m, totale - totale / 1.22m, totale, false, "Bonifico");

    public static SoggettoDanea Cliente(int id, string nome = "Rossi Srl") =>
        new(id, "0001", nome, true, false, "01234567890", null, "Via Roma 1", "20100", "Milano", "MI", "Italia", null, null, null, null, null, null, null, null, null);
}
