using Microsoft.Win32;

namespace RdpManager.Core;

/// <summary>Un PC trovato nella cronologia di mstsc.</summary>
/// <param name="Address">Come lo scrive mstsc: "pc", "pc:3390", "[::1]:3390".</param>
/// <param name="UsernameHint">Ultimo utente usato con quel PC ("DOMINIO\utente"), se mstsc lo ricorda.</param>
public sealed record MstscHistoryItem(string Address, string UsernameHint);

/// <summary>
/// Importazione della cronologia del client Desktop remoto di Windows (mstsc), su richiesta dell'utente.
/// Legge soltanto, da HKCU, con chiavi aperte in sola lettura: nessuna scrittura nel registro e nessuna
/// credenziale (mstsc lì tiene solo indirizzi e nomi utente, mai password).
/// </summary>
public static class MstscHistory
{
    public const string ClientKeyPath = @"Software\Microsoft\Terminal Server Client";
    public const string DefaultFolder = "Importate da mstsc";

    /// <summary>Legge la cronologia dell'utente corrente: prima l'elenco MRU (più recenti prima), poi gli altri PC noti.</summary>
    public static IReadOnlyList<MstscHistoryItem> ReadFromRegistry()
    {
        var mru = new List<string>();
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var defaults = Registry.CurrentUser.OpenSubKey(ClientKeyPath + @"\Default", writable: false))
        {
            if (defaults is not null)
                for (int i = 0; i < 10; i++)
                    if (defaults.GetValue("MRU" + i) is string s)
                        mru.Add(s);
        }
        using (var servers = Registry.CurrentUser.OpenSubKey(ClientKeyPath + @"\Servers", writable: false))
        {
            if (servers is not null)
                foreach (var name in servers.GetSubKeyNames())
                {
                    using var server = servers.OpenSubKey(name, writable: false);
                    hints[name] = server?.GetValue("UsernameHint") as string ?? "";
                }
        }
        return Merge(mru, hints);
    }

    /// <summary>
    /// Unisce l'elenco MRU e i PC con il loro utente: niente doppioni (senza distinguere maiuscole), voci vuote scartate,
    /// prima i PC dell'MRU nel loro ordine.
    /// </summary>
    public static IReadOnlyList<MstscHistoryItem> Merge(IEnumerable<string?> mru, IReadOnlyDictionary<string, string>? hints)
    {
        hints ??= new Dictionary<string, string>();
        var lookup = new Dictionary<string, string>(hints, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MstscHistoryItem>();
        foreach (var address in mru.Concat(hints.Keys))
        {
            var a = (address ?? "").Trim();
            if (a.Length == 0 || !seen.Add(a))
                continue;
            result.Add(new MstscHistoryItem(a, lookup.TryGetValue(a, out var hint) ? hint?.Trim() ?? "" : ""));
        }
        return result;
    }

    /// <summary>Connessione da una voce della cronologia (host, porta, utente e dominio), nella cartella indicata.</summary>
    public static RdpConnection ToConnection(MstscHistoryItem item, string folder = DefaultFolder)
    {
        var (host, port) = RdpFile.SplitAddress(item.Address);
        var c = new RdpConnection { Host = host, Port = port ?? RdpConnection.DefaultPort, Name = host, Group = RdpConnection.NormalizeGroup(folder) };
        var user = item.UsernameHint;
        int slash = user.IndexOf('\\');
        if (slash > 0)
        {
            c.Domain = user[..slash];
            c.Username = user[(slash + 1)..];
        }
        else
        {
            c.Username = user;
        }
        return c;
    }

    /// <summary>
    /// Aggiunge al documento le voci scelte che non ci sono già (stesso host, porta, utente e dominio).
    /// Restituisce le connessioni aggiunte.
    /// </summary>
    public static IReadOnlyList<RdpConnection> ImportInto(ConnectionsDocument doc, IEnumerable<MstscHistoryItem> items,
        string folder = DefaultFolder)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var added = new List<RdpConnection>();
        foreach (var item in items)
        {
            var c = ToConnection(item, folder);
            if (c.Host.Length == 0 || IsDuplicate(doc, c))
                continue;
            doc.Connections.Add(c);
            added.Add(c);
        }
        if (added.Count > 0)
            doc.AddFolder("", folder);
        return added;
    }

    /// <summary>Vero se la voce è già nell'elenco (per mostrarla come tale nel dialogo).</summary>
    public static bool IsAlreadyPresent(ConnectionsDocument doc, MstscHistoryItem item) =>
        IsDuplicate(doc, ToConnection(item));

    /// <summary>Stessa destinazione; senza utente basta lo stesso host e la stessa porta.</summary>
    private static bool IsDuplicate(ConnectionsDocument doc, RdpConnection c) =>
        doc.Connections.Any(e => e.IsSameTarget(c)
            || (c.Username.Length == 0 && e.Port == c.Port && e.Host.Trim().Equals(c.Host, StringComparison.OrdinalIgnoreCase)));
}
