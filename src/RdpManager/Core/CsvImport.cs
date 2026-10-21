using System.Globalization;
using System.Text;

namespace RdpManager.Core;

/// <summary>Campo di una connessione a cui si può associare una colonna del CSV.</summary>
public enum CsvField
{
    Ignore,
    Name,
    Host,
    Port,
    Username,
    Domain,
    Group,
    Label,
    MacAddress,
}

/// <summary>Una riga del CSV pronta da importare (o con l'errore che la esclude).</summary>
/// <param name="Line">Numero di riga nel file (da 1, intestazione compresa).</param>
/// <param name="Duplicate">Esiste già una connessione con la stessa destinazione (o è ripetuta nel file).</param>
public sealed record CsvRow(int Line, RdpConnection Connection, string? Error, bool Duplicate)
{
    public bool CanImport => Error is null && !Duplicate;
}

/// <summary>Contenuto letto da un CSV: separatore, intestazione e righe.</summary>
public sealed record CsvTable(char Separator, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Importazione di connessioni da CSV (es. esportato da Excel): separatore «;» o «,» riconosciuto da solo,
/// campi tra virgolette (anche con separatori e a capo dentro), intestazione obbligatoria, colonne associate ai
/// campi in automatico dal nome (modificabile nell'anteprima), righe senza host scartate, doppioni non importati.
/// </summary>
public static class CsvImport
{
    /// <summary>Nomi di intestazione riconosciuti per ogni campo (minuscole, senza spazi e accenti).</summary>
    private static readonly (CsvField Field, string[] Names)[] KnownHeaders =
    [
        (CsvField.Name, ["nome", "name", "connessione", "connection", "descrizione", "description"]),
        (CsvField.Host, ["host", "indirizzo", "address", "server", "computer", "pc", "ip", "hostname", "fulladdress"]),
        (CsvField.Port, ["porta", "port"]),
        (CsvField.Username, ["utente", "user", "username", "login", "nomeutente"]),
        (CsvField.Domain, ["dominio", "domain"]),
        (CsvField.Group, ["cartella", "gruppo", "group", "folder", "percorso"]),
        (CsvField.Label, ["etichetta", "label", "tag"]),
        (CsvField.MacAddress, ["mac", "macaddress", "indirizzomac"]),
    ];

    /// <summary>Legge il testo: separatore dall'intestazione (quello che compare di più fuori dalle virgolette).</summary>
    public static CsvTable Parse(string text)
    {
        text = (text ?? "").TrimStart('﻿');
        var firstLine = text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        char sep = CountOutsideQuotes(firstLine, ';') >= CountOutsideQuotes(firstLine, ',') && firstLine.Contains(';') ? ';' : ',';
        var records = ReadRecords(text, sep).Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
        if (records.Count == 0)
            return new CsvTable(sep, [], []);
        return new CsvTable(sep, records[0].Select(h => h.Trim()).ToList(), records.Skip(1).ToList());
    }

    /// <summary>Associazione automatica delle colonne ai campi dal nome dell'intestazione (ogni campo una volta).</summary>
    public static IReadOnlyList<CsvField> GuessMapping(IReadOnlyList<string> header)
    {
        var used = new HashSet<CsvField>();
        var map = new List<CsvField>();
        foreach (var h in header)
        {
            var key = Normalize(h);
            var field = KnownHeaders.FirstOrDefault(k => k.Names.Contains(key)).Field;
            if (field != CsvField.Ignore && used.Add(field))
                map.Add(field);
            else
                map.Add(CsvField.Ignore);
        }
        return map;
    }

    /// <summary>Righe pronte per l'anteprima, con errori e doppioni (rispetto al documento e dentro il file).</summary>
    public static IReadOnlyList<CsvRow> Preview(CsvTable table, IReadOnlyList<CsvField> mapping, ConnectionsDocument doc, string folder = "")
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(doc);
        var rows = new List<CsvRow>();
        var seen = new List<RdpConnection>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var values = table.Rows[i];
            string Get(CsvField f)
            {
                int col = IndexOf(mapping, f);
                return col >= 0 && col < values.Count ? values[col].Trim() : "";
            }
            var c = new RdpConnection
            {
                Name = Get(CsvField.Name),
                Username = Get(CsvField.Username),
                Domain = Get(CsvField.Domain),
                Label = Get(CsvField.Label),
                Group = RdpConnection.NormalizeGroup(Get(CsvField.Group) is { Length: > 0 } g ? g : folder),
            };
            string? error = null;
            var (host, portInHost) = RdpFile.SplitAddress(Get(CsvField.Host));
            c.Host = host.Trim();
            if (c.Host.Length == 0)
                error = "host mancante";
            var portText = Get(CsvField.Port);
            if (portInHost is int p)
                c.Port = p;
            else if (portText.Length > 0)
            {
                if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
                    c.Port = port;
                else
                    error ??= $"porta non valida «{portText}»";
            }
            if (c.Username.Contains('\\') && c.Domain.Length == 0)
            {
                int slash = c.Username.IndexOf('\\');
                c.Domain = c.Username[..slash];
                c.Username = c.Username[(slash + 1)..];
            }
            var mac = Get(CsvField.MacAddress);
            if (mac.Length > 0)
            {
                if (WakeOnLan.TryNormalizeMac(mac, out var normalized))
                    c.MacAddress = normalized;
                else
                    error ??= $"MAC non valido «{mac}»";
            }
            if (c.Name.Length == 0)
                c.Name = c.Host;
            bool duplicate = error is null && (doc.Connections.Any(e => e.IsSameTarget(c)) || seen.Any(e => e.IsSameTarget(c)));
            if (error is null && !duplicate)
                seen.Add(c);
            rows.Add(new CsvRow(i + 2, c, error, duplicate));
        }
        return rows;
    }

    /// <summary>Aggiunge al documento le righe importabili; restituisce le connessioni aggiunte.</summary>
    public static IReadOnlyList<RdpConnection> ImportInto(ConnectionsDocument doc, IEnumerable<CsvRow> rows)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var added = new List<RdpConnection>();
        foreach (var r in rows.Where(r => r.CanImport))
        {
            if (doc.Connections.Any(e => e.IsSameTarget(r.Connection)))
                continue;
            doc.Connections.Add(r.Connection);
            if (r.Connection.Group.Length > 0)
                doc.AddFolder("", r.Connection.Group);
            added.Add(r.Connection);
        }
        return added;
    }

    public static string Describe(CsvField f) => f switch
    {
        CsvField.Name => "Nome",
        CsvField.Host => "Host",
        CsvField.Port => "Porta",
        CsvField.Username => "Utente",
        CsvField.Domain => "Dominio",
        CsvField.Group => "Cartella",
        CsvField.Label => "Etichetta",
        CsvField.MacAddress => "MAC",
        _ => "(ignora)",
    };

    private static int IndexOf(IReadOnlyList<CsvField> mapping, CsvField f)
    {
        for (int i = 0; i < mapping.Count; i++)
            if (mapping[i] == f)
                return i;
        return -1;
    }

    private static string Normalize(string header)
    {
        var decomposed = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
            if (char.IsLetterOrDigit(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString();
    }

    private static int CountOutsideQuotes(string line, char sep)
    {
        int n = 0;
        bool quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
                quoted = !quoted;
            else if (ch == sep && !quoted)
                n++;
        }
        return n;
    }

    /// <summary>Lettura CSV secondo RFC 4180: «""» dentro le virgolette è una virgoletta, a capo ammessi nei campi.</summary>
    private static IEnumerable<List<string>> ReadRecords(string text, char sep)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                        quoted = false;
                }
                else
                    field.Append(ch);
                continue;
            }
            if (ch == '"')
                quoted = true;
            else if (ch == sep)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
            }
            else
                field.Append(ch);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
