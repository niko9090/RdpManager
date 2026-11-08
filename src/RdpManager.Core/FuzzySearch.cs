using System.Globalization;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Ricerca approssimata per la ricerca rapida (Ctrl+Alt+Spazio): ogni parola della ricerca deve comparire in
/// nome, host o etichetta, anche "a pezzi" (le lettere nell'ordine giusto, es. "srvpr" trova "server-prod")
/// o con un errore di battitura (es. "sevrer" trova "server"). Maiuscole e accenti non contano.
/// </summary>
public static class FuzzySearch
{
    /// <summary>
    /// Punteggio di <paramref name="query"/> su <paramref name="text"/>: più alto = più pertinente, 0 = non trovato.
    /// Ordine dei punteggi: uguale &gt; inizio &gt; inizio di parola &gt; contenuto &gt; lettere in ordine &gt; un errore.
    /// </summary>
    public static int Score(string? query, string? text)
    {
        var q = Fold(query);
        var t = Fold(text);
        if (q.Length == 0 || t.Length == 0)
            return 0;
        if (t == q)
            return 1000;
        if (t.StartsWith(q, StringComparison.Ordinal))
            return 900 - Math.Min(t.Length - q.Length, 99);
        int at = t.IndexOf(q, StringComparison.Ordinal);
        if (at > 0)
        {
            bool wordStart = !char.IsLetterOrDigit(t[at - 1]);
            return (wordStart ? 800 : 700) - Math.Min(at, 99);
        }
        if (SubsequenceScore(q, t) is int sub)
            return sub;
        if (q.Length >= 4 && WithinOneEdit(q, t))
            return 200;
        return 0;
    }

    /// <summary>
    /// Punteggio di una ricerca di più parole su più campi: ogni parola deve trovare almeno un campo;
    /// conta il campo migliore per ciascuna parola. 0 = non trovato.
    /// </summary>
    public static int Score(string? query, IReadOnlyList<string?> fields)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return 0;
        int total = 0;
        foreach (var word in words)
        {
            int best = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                // A parità, il primo campo (il nome) vale un po' di più.
                int s = Score(word, fields[i]);
                if (s > 0)
                    s += Math.Max(0, 3 - i);
                best = Math.Max(best, s);
            }
            if (best == 0)
                return 0;
            total += best;
        }
        return total;
    }

    /// <summary>Minuscole senza accenti ("Città" → "citta").</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lettere della ricerca nell'ordine dentro il testo: 300..599, di più se compatte e a inizio parola.</summary>
    private static int? SubsequenceScore(string q, string t)
    {
        int ti = 0, first = -1, last = -1, wordStarts = 0;
        foreach (var ch in q)
        {
            while (ti < t.Length && t[ti] != ch)
                ti++;
            if (ti == t.Length)
                return null;
            if (first < 0)
                first = ti;
            if (ti == 0 || !char.IsLetterOrDigit(t[ti - 1]))
                wordStarts++;
            last = ti;
            ti++;
        }
        int span = last - first + 1;
        int gaps = span - q.Length;
        return Math.Clamp(500 - gaps * 10 - Math.Min(first, 50) + wordStarts * 20, 300, 599);
    }

    /// <summary>Vero se il testo, o una sua parola, dista al massimo una modifica (lettera in più, in meno, cambiata o scambiata).</summary>
    private static bool WithinOneEdit(string q, string t)
    {
        if (IsOneEdit(q, t))
            return true;
        foreach (var word in t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var part in word.Split(['.', '-', '_', '\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                if (IsOneEdit(q, part))
                    return true;
        }
        // Anche l'inizio del testo, così "sevrer" trova "server-prod".
        for (int len = q.Length - 1; len <= q.Length + 1; len++)
            if (len > 0 && len <= t.Length && IsOneEdit(q, t[..len]))
                return true;
        return false;
    }

    private static bool IsOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        if (a.Length == b.Length)
        {
            int diff = -1, count = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                {
                    if (++count > 2)
                        return false;
                    if (diff < 0)
                        diff = i;
                }
            if (count <= 1)
                return true;
            // Due lettere vicine scambiate ("sevrer" / "server").
            return diff + 1 < a.Length && a[diff] == b[diff + 1] && a[diff + 1] == b[diff]
                   && a[(diff + 2)..] == b[(diff + 2)..];
        }
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        int si = 0, li = 0;
        bool skipped = false;
        while (si < shorter.Length && li < longer.Length)
        {
            if (shorter[si] == longer[li])
            {
                si++;
                li++;
            }
            else
            {
                if (skipped)
                    return false;
                skipped = true;
                li++;
            }
        }
        return true;
    }
}

/// <summary>Tipo di risultato della ricerca rapida.</summary>
public enum QuickSearchKind
{
    /// <summary>Sessione già aperta: si passa lì.</summary>
    OpenSession,
    /// <summary>Connessione salvata non aperta: si apre una nuova sessione.</summary>
    Connection,
}

public sealed record QuickSearchResult(RdpConnection Connection, QuickSearchKind Kind, int Score);

/// <summary>Risultati della ricerca rapida: sessioni aperte e connessioni salvate, dalle più pertinenti.</summary>
public static class QuickSearch
{
    /// <summary>Le sessioni aperte valgono un po' di più, a parità di pertinenza vengono prima.</summary>
    public const int OpenSessionBonus = 50;

    /// <param name="openIds">Id delle connessioni con una sessione aperta, nell'ordine delle schede.</param>
    /// <remarks>
    /// Con la ricerca vuota restituisce prima le sessioni aperte (in ordine di scheda), poi preferiti e recenti.
    /// </remarks>
    public static IReadOnlyList<QuickSearchResult> Find(string? query, IEnumerable<RdpConnection> connections,
        IReadOnlyList<Guid> openIds, int max = 20)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(openIds);
        var list = connections.ToList();
        var open = new HashSet<Guid>(openIds);
        if (string.IsNullOrWhiteSpace(query))
        {
            var openFirst = openIds.Select(id => list.FirstOrDefault(c => c.Id == id)).OfType<RdpConnection>()
                .Select(c => new QuickSearchResult(c, QuickSearchKind.OpenSession, 0));
            var others = list.Where(c => !open.Contains(c.Id))
                .OrderByDescending(c => c.IsFavorite)
                .ThenByDescending(c => c.LastConnectedUtc ?? DateTime.MinValue)
                .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new QuickSearchResult(c, QuickSearchKind.Connection, 0));
            return openFirst.Concat(others).Take(max).ToList();
        }

        return list
            .Select(c =>
            {
                int score = FuzzySearch.Score(query, [c.DisplayName, c.Host, c.Label]);
                bool isOpen = open.Contains(c.Id);
                return new QuickSearchResult(c, isOpen ? QuickSearchKind.OpenSession : QuickSearchKind.Connection,
                    score == 0 ? 0 : score + (isOpen ? OpenSessionBonus : 0) + (c.IsFavorite ? 5 : 0));
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Connection.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }
}
