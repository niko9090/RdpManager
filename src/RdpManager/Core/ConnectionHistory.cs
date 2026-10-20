using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core;

/// <summary>Una sessione nella cronologia: quando è iniziata, quando è finita e perché.</summary>
public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }

    /// <summary>Nome e host al momento della connessione: restano leggibili anche se la connessione viene eliminata.</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";

    public DateTime StartUtc { get; set; }

    /// <summary>Null = sessione ancora aperta, oppure app chiusa di colpo (vedi <see cref="ConnectionHistory.CloseInterrupted"/>).</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>Codice di disconnessione del controllo RDP, se c'è.</summary>
    public int? ReasonCode { get; set; }

    /// <summary>Motivo della fine in italiano.</summary>
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public TimeSpan? Duration => EndUtc is DateTime end && end >= StartUtc ? end - StartUtc : null;

    [JsonIgnore]
    public bool IsOpen => EndUtc is null;

    /// <summary>"1 h 05 min", "3 min 20 s", "12 s".</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not TimeSpan d)
            return "";
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        if (d.TotalMinutes >= 1)
            return $"{d.Minutes} min {d.Seconds:00} s";
        return $"{Math.Max(0, d.Seconds)} s";
    }
}

/// <summary>
/// Cronologia delle connessioni, salvata a parte (history.json accanto a connections.json) e limitata alle
/// ultime <see cref="MaxEntries"/> sessioni. Non contiene password né utenti.
/// </summary>
public sealed class ConnectionHistory
{
    public const int MaxEntries = 2000;
    public const string InterruptedReason = "Interrotta (app chiusa)";

    public List<HistoryEntry> Entries { get; set; } = [];

    /// <summary>Annota l'inizio di una sessione; l'elemento restituito va passato a <see cref="End"/>.</summary>
    public HistoryEntry Start(RdpConnection c, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(c);
        var entry = new HistoryEntry
        {
            ConnectionId = c.Id,
            Name = c.DisplayName,
            Host = c.Host,
            StartUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
        };
        Entries.Add(entry);
        Trim();
        return entry;
    }

    /// <summary>Chiude la sessione (una sola volta: le chiamate successive non cambiano nulla).</summary>
    public void End(HistoryEntry entry, DateTime utcNow, string reason, int? reasonCode = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.EndUtc is not null)
            return;
        var end = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        entry.EndUtc = end < entry.StartUtc ? entry.StartUtc : end;
        entry.Reason = reason ?? "";
        entry.ReasonCode = reasonCode;
    }

    /// <summary>Sessioni rimaste aperte da un'esecuzione precedente (app chiusa di colpo): le chiude all'ultimo istante noto.</summary>
    public int CloseInterrupted()
    {
        int n = 0;
        foreach (var e in Entries.Where(e => e.EndUtc is null))
        {
            e.EndUtc = e.StartUtc;
            e.Reason = InterruptedReason;
            n++;
        }
        return n;
    }

    /// <summary>Dalla più recente; <paramref name="connectionId"/> filtra su una connessione.</summary>
    public IReadOnlyList<HistoryEntry> Recent(Guid? connectionId = null, int max = int.MaxValue) =>
        Entries.Where(e => connectionId is null || e.ConnectionId == connectionId)
            .OrderByDescending(e => e.StartUtc).Take(Math.Max(0, max)).ToList();

    /// <summary>Tempo totale delle sessioni concluse di una connessione.</summary>
    public TimeSpan TotalDuration(Guid connectionId) =>
        Entries.Where(e => e.ConnectionId == connectionId && e.Duration is not null)
            .Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration!.Value);

    public void Clear() => Entries.Clear();

    /// <summary>Toglie le sessioni di una connessione (es. dal menu della cronologia).</summary>
    public int Clear(Guid connectionId) => Entries.RemoveAll(e => e.ConnectionId == connectionId);

    private void Trim()
    {
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
    }

    internal void Repair()
    {
        Entries ??= [];
        Entries.RemoveAll(e => e is null);
        foreach (var e in Entries)
        {
            e.Name ??= "";
            e.Host ??= "";
            e.Reason ??= "";
            e.StartUtc = DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc);
            if (e.EndUtc is DateTime end)
                e.EndUtc = DateTime.SpecifyKind(end, DateTimeKind.Utc);
        }
        Entries.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        Trim();
    }
}

/// <summary>Lettura e scrittura di history.json. Un file illeggibile dà una cronologia vuota (non è un dato prezioso).</summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string FileName = "history.json";

    public string FilePath { get; }

    public HistoryStore(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>history.json nella stessa cartella di connections.json.</summary>
    public static HistoryStore Beside(string connectionsPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(connectionsPath)) ?? ".", FileName));

    public ConnectionHistory Load()
    {
        ConnectionHistory? history = null;
        try
        {
            if (File.Exists(FilePath))
                history = JsonSerializer.Deserialize<ConnectionHistory>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            history = null;
        }
        history ??= new ConnectionHistory();
        history.Repair();
        return history;
    }

    public void Save(ConnectionHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(history, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
