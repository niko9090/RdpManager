using System.Collections.ObjectModel;
using System.Globalization;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Una colonna del file CSV con il campo della connessione scelto per lei.</summary>
public sealed class CsvColumnViewModel : ViewModelBase
{
    private ChoiceItem<CsvField> _field;

    public CsvColumnViewModel(string header, int index, CsvField guessed, Action changed)
    {
        Header = header.Length > 0 ? header : $"(colonna {index + 1})";
        _field = Choices.Pick(CsvImportViewModel.FieldChoices, guessed);
        Changed = changed;
    }

    private Action Changed { get; }

    public string Header { get; }

    public IReadOnlyList<ChoiceItem<CsvField>> FieldChoices => CsvImportViewModel.FieldChoices;

    public ChoiceItem<CsvField> Field
    {
        get => _field;
        set { if (value is not null && Set(ref _field, value)) Changed(); }
    }
}

/// <summary>Una riga dell'anteprima.</summary>
public sealed record CsvPreviewRow(string Line, string Name, string Host, string Port, string User, string Folder, string Outcome, bool CanImport);

/// <summary>Anteprima dell'importazione da CSV (#22): associazione delle colonne ai campi, cartella, esito di ogni riga.</summary>
public sealed class CsvImportViewModel : ViewModelBase
{
    public static IReadOnlyList<ChoiceItem<CsvField>> FieldChoices { get; } =
        Enum.GetValues<CsvField>().Select(f => new ChoiceItem<CsvField>(f, CsvImport.Describe(f))).ToList();

    private readonly CsvTable _table;
    private readonly ConnectionsDocument _doc;
    private string _folder;
    private IReadOnlyList<CsvRow> _rows = [];
    private string _summary = "";

    public CsvImportViewModel(string fileName, CsvTable table, ConnectionsDocument doc, string folder)
    {
        _table = table;
        _doc = doc;
        _folder = folder;
        FileName = fileName;
        var guessed = CsvImport.GuessMapping(table.Header);
        Columns = [.. table.Header.Select((h, i) => new CsvColumnViewModel(h, i, guessed[i], Refill))];
        Refill();
    }

    public string WindowTitle => $"Importa da CSV: {FileName}";

    public string FileName { get; }

    public string Intro => $"Separatore «{_table.Separator}». Scegli per ogni colonna il campo della connessione (riconosciuti in automatico " +
                           "dall'intestazione). Le righe senza host, con porta o MAC non validi e i doppioni non vengono importati.";

    public IReadOnlyList<CsvColumnViewModel> Columns { get; }

    public ObservableCollection<CsvPreviewRow> Preview { get; } = [];

    /// <summary>Cartella per le righe senza cartella.</summary>
    public string Folder
    {
        get => _folder;
        set { if (Set(ref _folder, value)) Refill(); }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public int ImportableCount => _rows.Count(r => r.CanImport);

    public string ImportLabel => $"Importa {ImportableCount}";

    public bool CanImport => ImportableCount > 0;

    /// <summary>Le righe valutate, da passare a <see cref="CsvImport.ImportInto"/>.</summary>
    public IReadOnlyList<CsvRow> Rows => _rows;

    private void Refill()
    {
        var mapping = Columns.Select(c => c.Field.Value).ToList();
        _rows = CsvImport.Preview(_table, mapping, _doc, _folder);
        Preview.Clear();
        foreach (var r in _rows)
        {
            var c = r.Connection;
            Preview.Add(new CsvPreviewRow(r.Line.ToString(CultureInfo.InvariantCulture), c.Name, c.Host,
                c.Port.ToString(CultureInfo.InvariantCulture), c.Username, c.Group,
                r.Error is { } e ? "Esclusa: " + e : r.Duplicate ? "Esclusa: c'è già" : "Da importare", r.CanImport));
        }
        int ok = ImportableCount;
        Summary = $"{ok} da importare, {_rows.Count - ok} escluse";
        OnPropertyChanged(nameof(ImportLabel));
        OnPropertyChanged(nameof(CanImport));
    }
}
