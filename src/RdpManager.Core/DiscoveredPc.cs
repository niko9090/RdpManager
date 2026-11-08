namespace RdpManager.Core;

/// <summary>
/// Un PC trovato da «Cerca PC in rete» (scanner o Tailscale). <see cref="Address"/> è quello che va nella connessione;
/// <see cref="Aliases"/> sono gli altri nomi e indirizzi dello stesso PC, usati per riconoscere i doppioni.
/// </summary>
public sealed record DiscoveredPc(string Name, string Address, int Port, string Source, IReadOnlyList<string> Aliases)
{
    public DiscoveredPc(string name, string address, int port, string source) : this(name, address, port, source, []) { }

    /// <summary>Indirizzo, alias e nome breve (prima parte del nome DNS), senza vuoti e senza ripetizioni.</summary>
    public IReadOnlyList<string> AllNames()
    {
        var names = new List<string>();
        void Add(string? n)
        {
            n = (n ?? "").Trim().TrimEnd('.');
            if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                names.Add(n);
        }
        Add(Address);
        foreach (var a in Aliases)
            Add(a);
        return names;
    }

    public static DiscoveredPc FromScan(ScanHit hit, string source = "Rete locale") =>
        new(ShortName(hit.HostName) ?? hit.Address.ToString(), hit.Address.ToString(), hit.Port, source,
            hit.HostName is { Length: > 0 } h ? [h] : []);

    public static DiscoveredPc FromTailscale(TailscalePeer peer, int port = RdpConnection.DefaultPort)
    {
        var aliases = new List<string> { peer.HostName, peer.DnsName };
        if (peer.IPv4 is { } v4)
            aliases.Add(v4.ToString());
        if (peer.IPv6 is { } v6)
            aliases.Add(v6.ToString());
        return new DiscoveredPc(peer.DisplayName, peer.ConnectAddress, port, "Tailscale", aliases.Where(a => a.Length > 0).ToList());
    }

    private static string? ShortName(string? dns)
    {
        if (string.IsNullOrWhiteSpace(dns))
            return null;
        var n = dns.Trim().TrimEnd('.');
        return IpRange.TryParseIPv4(n, out _) ? n : n.Split('.')[0];
    }
}

/// <summary>Esito dell'aggiunta dei PC trovati a una cartella.</summary>
public sealed record DiscoveryImportResult(IReadOnlyList<RdpConnection> Added, int Duplicates);

/// <summary>Aggiunta dei PC trovati all'elenco, in una cartella, senza doppioni.</summary>
public static class DiscoveryImport
{
    public const string DefaultFolder = "PC trovati in rete";

    /// <summary>
    /// Vero se nell'elenco c'è già una connessione verso lo stesso PC: stessa porta e host uguale (senza maiuscole)
    /// a uno dei suoi nomi o indirizzi, oppure stesso nome breve (es. «pc-ufficio» e «pc-ufficio.tailnet.ts.net»).
    /// </summary>
    public static bool IsAlreadyPresent(ConnectionsDocument doc, DiscoveredPc pc) =>
        doc.Connections.Any(c => IsSamePc(c.Host, c.Port, pc));

    /// <summary>
    /// Aggiunge i PC a <paramref name="folder"/> (creata se serve) saltando quelli già presenti nell'elenco e i
    /// doppioni della stessa selezione. Utente, password e il resto restano vuoti/predefiniti.
    /// </summary>
    public static DiscoveryImportResult ImportInto(ConnectionsDocument doc, IEnumerable<DiscoveredPc> pcs, string? folder = DefaultFolder)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var group = RdpConnection.NormalizeGroup(folder);
        var added = new List<RdpConnection>();
        int duplicates = 0;
        foreach (var pc in pcs)
        {
            if (pc.Address.Trim().Length == 0)
                continue;
            if (IsAlreadyPresent(doc, pc))
            {
                duplicates++;
                continue;
            }
            var c = new RdpConnection
            {
                Name = pc.Name.Trim().Length > 0 ? pc.Name.Trim() : pc.Address.Trim(),
                Host = pc.Address.Trim(),
                Port = pc.Port is > 0 and <= 65535 ? pc.Port : RdpConnection.DefaultPort,
                Group = group,
            };
            doc.Connections.Add(c);
            added.Add(c);
        }
        if (group.Length > 0 && added.Count > 0)
            doc.AddFolder("", group);
        return new DiscoveryImportResult(added, duplicates);
    }

    private static bool IsSamePc(string host, int port, DiscoveredPc pc)
    {
        if (port != pc.Port)
            return false;
        var h = host.Trim().TrimEnd('.');
        if (h.Length == 0)
            return false;
        var names = pc.AllNames();
        if (names.Any(n => n.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return true;
        // Nomi DNS: confronto del nome breve, ma mai fra indirizzi IP (10.0.0.1 e 10.0.0.2 non sono lo stesso PC).
        if (IpRange.TryParseIPv4(h, out _) || h.Contains(':'))
            return false;
        var shortHost = h.Split('.')[0];
        return names.Where(n => !IpRange.TryParseIPv4(n, out _) && !n.Contains(':'))
            .Any(n => n.Split('.')[0].Equals(shortHost, StringComparison.OrdinalIgnoreCase));
    }
}
