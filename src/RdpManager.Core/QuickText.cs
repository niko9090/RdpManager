using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>
/// Testo rapido: un frammento salvato (comando, indirizzo, firma…) che si digita nella sessione attiva.
/// Non è pensato per le password: viene salvato in chiaro in connections.json ed esportato così nei .rdpm.
/// </summary>
public sealed class QuickText
{
    /// <summary>Lunghezza massima: la digitazione avviene tasto per tasto e un testo enorme bloccherebbe la sessione.</summary>
    public const int MaxLength = 20_000;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Nome per i menu: il nome, oppure l'inizio del testo su una riga.</summary>
    [JsonIgnore]
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : Preview(Text, 40);

    public QuickText Clone() => (QuickText)MemberwiseClone();

    /// <summary>Prima riga del testo, accorciata con "…".</summary>
    public static string Preview(string? text, int maxLength)
    {
        var line = (text ?? "").ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length <= maxLength ? line : line[..Math.Max(1, maxLength - 1)] + "…";
    }

    /// <summary>Testo da digitare: a capo uniformati in "\n" (la digitazione li trasforma in Invio).</summary>
    public string TextToType() => (Text ?? "").ReplaceLineEndings("\n");

    /// <summary>Messaggio d'errore per il dialogo di modifica, oppure null se il testo rapido è valido.</summary>
    public static string? Validate(string? name, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "Il testo è vuoto.";
        if (text.Length > MaxLength)
            return $"Il testo è troppo lungo (massimo {MaxLength} caratteri).";
        if ((name ?? "").Length > 100)
            return "Il nome è troppo lungo (massimo 100 caratteri).";
        return null;
    }

    internal void Repair()
    {
        Name ??= "";
        Text ??= "";
    }
}

public sealed partial class ConnectionsDocument
{
    /// <summary>Testi rapidi, nell'ordine dei menu (dalla 1.2).</summary>
    public List<QuickText> QuickTexts { get; set; } = [];

    /// <summary>Sposta il testo rapido di <paramref name="delta"/> posizioni (es. -1 = su); falso se non si muove.</summary>
    public bool MoveQuickText(Guid id, int delta)
    {
        int from = QuickTexts.FindIndex(t => t.Id == id);
        if (from < 0)
            return false;
        int to = Math.Clamp(from + delta, 0, QuickTexts.Count - 1);
        if (to == from)
            return false;
        var item = QuickTexts[from];
        QuickTexts.RemoveAt(from);
        QuickTexts.Insert(to, item);
        return true;
    }
}
