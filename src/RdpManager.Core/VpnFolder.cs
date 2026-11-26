using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Cartella automatica «marshmelloVPN»: una connessione RDP per ogni PC della rete VPN (l'elenco della plancia), con
/// l'IP della VPN come indirizzo. Se fra le connessioni dell'utente ce n'è una verso lo stesso PC, la nuova ne riprende
/// accessi e impostazioni (senza mai toccare l'originale). Nessuna interfaccia: la usano Windows e Linux.
/// </summary>
public static class VpnFolder
{
    public const string FolderName = "marshmelloVPN";

    /// <summary>
    /// Id stabile della connessione di un PC: deriva solo dal nome del dispositivo VPN, così ogni PC dell'account crea la
    /// stessa connessione con lo stesso Id e la sincronizzazione non genera doppioni.
    /// </summary>
    public static Guid StableId(string deviceName, int attempt = 0)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"marshmelloVPN|{deviceName.Trim().ToLowerInvariant()}|{attempt}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Allinea la cartella ai PC della plancia: aggiunge i nuovi, aggiorna l'IP, toglie solo le connessioni create dalla
    /// cartella stessa per PC non più in rete. Con l'elenco vuoto non fa niente (meglio un PC in più che perdere tutto per
    /// una risposta vuota). Vero se il documento è cambiato.
    /// </summary>
    public static bool Apply(ConnectionsDocument doc, IReadOnlyList<VpnDashboardDevice> devices)
    {
        // Tutti i PC con un nome, anche ripetuto: servono a riconoscere gli abbinamenti ambigui.
        var entries = new List<Entry>();
        foreach (var d in devices)
        {
            var n = (d.Name ?? "").Trim();
            if (n.Length > 0)
                entries.Add(new Entry(n, VpnDashboardModel.StripMask(d.Address ?? ""), d));
        }
        if (entries.Count == 0)
            return false; // elenco vuoto (anche solo transitorio): non si cancella niente
        var all = entries.Select(e => (e.Name, e.Address)).ToList();

        // Anche il PC stesso resta in cartella: la cartella è nel documento sincronizzato, e se ogni PC togliesse sé stesso
        // l'ultimo che sincronizza cancellerebbe la propria voce per tutti (il PC spariva a turno dagli altri PC).

        // Lo stesso nome può comparire più volte (stesso PC registrato da due account, o due PC con lo stesso nome):
        // si tengono i PC distinti, vedi Resolve.
        var list = new List<(string Name, string Address)>();
        foreach (var g in entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            list.AddRange(Resolve(doc, g.Key, g.ToList()));

        bool changed = false;
        var nameSet = list.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Voci della cartella senza il segno AutoVpnDevice (perso da una versione che non lo conosceva): si riconoscono
        // come proprie e si uniscono in una sola per PC.
        if (AdoptLegacy(doc, list))
            changed = true;

        // Connessioni automatiche di PC tolti dalla rete (e doppioni dello stesso PC nati da una sincronizzazione: di due
        // gemelle resta quella con gli accessi dell'utente, poi quella con l'Id stabile, poi la prima).
        var drop = new HashSet<RdpConnection>();
        foreach (var group in doc.Connections.Where(c => c.AutoVpnDevice is not null)
            .GroupBy(c => c.AutoVpnDevice!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (!nameSet.Contains(group.Key))
            {
                drop.UnionWith(group);
                continue;
            }
            var stable = StableId(group.Key);
            drop.UnionWith(group.OrderByDescending(HasCredentials).ThenByDescending(c => c.Id == stable).Skip(1));
        }
        if (drop.Count > 0)
        {
            doc.Connections.RemoveAll(drop.Contains);
            changed = true;
        }

        var originals = doc.Connections.Where(c => c.AutoVpnDevice is null).ToList();
        foreach (var (name, address) in list)
        {
            var existing = doc.Connections.FirstOrDefault(c => c.AutoVpnDevice is { } dev
                && dev.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                if (address.Length == 0)
                    continue; // senza IP non c'è dove collegarsi
                existing = new RdpConnection
                {
                    Id = FreeId(doc, name),
                    Name = name,
                    Host = address,
                    AutoVpnDevice = name,
                    Group = doc.AddFolder("", FolderName) ?? FolderName,
                };
                doc.Connections.Add(existing);
                changed = true;
            }
            else if (address.Length > 0 && !existing.Host.Equals(address, StringComparison.Ordinal))
            {
                existing.Host = address;
                changed = true;
            }

            // Gli accessi si riprendono solo se la connessione automatica non ne ha di suoi (niente viene sovrascritto).
            if (!HasCredentials(existing) && FindSource(originals, all, name, address) is { } source)
            {
                CopyFrom(existing, source);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Riconosce le voci «vecchie» (nella cartella, senza AutoVpnDevice) di un PC della rete: host uguale al suo IP VPN
    /// o, in mancanza, nome uguale al suo. Ne resta una per PC (quella già automatica, altrimenti la più completa e poi
    /// la usata più di recente), con AutoVpnDevice rimesso; gli accessi mancanti si riprendono dalle altre copie, il
    /// preferito resta se lo era una qualunque. Le voci che non corrispondono a un solo PC non si toccano.
    /// </summary>
    private static bool AdoptLegacy(ConnectionsDocument doc, List<(string Name, string Address)> list)
    {
        var byDevice = new Dictionary<string, List<RdpConnection>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in doc.Connections)
        {
            if (c.AutoVpnDevice is not null
                || !RdpConnection.NormalizeGroup(c.Group).Equals(FolderName, StringComparison.OrdinalIgnoreCase))
                continue;
            var host = c.Host.Trim();
            var hit = list.Where(d => d.Address.Length > 0 && host.Equals(d.Address, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0)
            {
                var cname = c.Name.Trim();
                hit = list.Where(d => cname.Length > 0 && d.Name.Equals(cname, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (hit.Count != 1)
                continue; // nessuno o ambiguo: resta dell'utente
            if (!byDevice.TryGetValue(hit[0].Name, out var copies))
                byDevice[hit[0].Name] = copies = [];
            copies.Add(c);
        }

        bool changed = false;
        foreach (var (device, copies) in byDevice)
        {
            var stable = StableId(device);
            var ranked = copies.OrderByDescending(Completeness).ThenByDescending(c => c.LastConnectedUtc ?? DateTime.MinValue)
                .ThenByDescending(c => c.Id == stable).ToList();
            var auto = doc.Connections.FirstOrDefault(c => c.AutoVpnDevice is { } dev
                && dev.Trim().Equals(device, StringComparison.OrdinalIgnoreCase));
            var survivor = auto ?? ranked[0];
            var donors = ranked.Where(c => c != survivor).ToList();
            survivor.AutoVpnDevice = device;

            if (!HasCredentials(survivor) && donors.FirstOrDefault(HasCredentials) is { } source)
            {
                if (auto is not null)
                    CopyFrom(survivor, source);
                else
                {
                    survivor.Username = source.Username;
                    survivor.Domain = source.Domain;
                    survivor.Password = source.Password;
                    survivor.ProtectedPassword = source.ProtectedPassword;
                    survivor.CredentialProfileId = source.CredentialProfileId;
                }
            }
            if (donors.Any(d => d.IsFavorite))
                survivor.IsFavorite = true;
            if (survivor.Color is null && donors.Select(d => d.Color).FirstOrDefault(x => x is not null) is { } color)
                survivor.Color = color;
            if (survivor.Label.Length == 0 && donors.Select(d => d.Label).FirstOrDefault(x => x.Length > 0) is { } label)
                survivor.Label = label;
            if (survivor.MacAddress.Length == 0 && donors.Select(d => d.MacAddress).FirstOrDefault(x => x.Length > 0) is { } mac)
                survivor.MacAddress = mac;

            doc.Connections.RemoveAll(c => donors.Contains(c));
            changed = true;
        }
        return changed;
    }

    /// <summary>Quanti accessi e personalizzazioni dell'utente ha la connessione: serve a scegliere la copia da tenere.</summary>
    private static int Completeness(RdpConnection c) =>
        (c.Username.Trim().Length > 0 ? 1 : 0) + (c.Domain.Trim().Length > 0 ? 1 : 0) + (c.HasPassword ? 1 : 0)
        + (c.CredentialProfileId is not null ? 1 : 0) + (c.IsFavorite ? 1 : 0) + (c.Color is not null ? 1 : 0)
        + (c.Label.Length > 0 ? 1 : 0) + (c.MacAddress.Length > 0 ? 1 : 0);

    private sealed record Entry(string Name, string Address, VpnDashboardDevice Device);

    private static DateTimeOffset Recent(Entry e) => e.Device.LastContact ?? DateTimeOffset.MinValue;

    /// <summary>
    /// I PC dell'elenco che hanno lo stesso nome. Stesso IP: è lo stesso PC, ne resta uno. IP diversi: se uno solo è in linea
    /// (o ha l'handshake più recente) resta lui con il nome semplice, così l'Id stabile non cambia; se sono più d'uno in linea
    /// (o non si distinguono) si tengono tutti, con il nome distinto dall'IP: «nome (10.66.0.N)». Il nome semplice va a chi
    /// aveva già la connessione automatica.
    /// </summary>
    private static List<(string Name, string Address)> Resolve(ConnectionsDocument doc, string key, List<Entry> group)
    {
        var distinct = group.Where(e => e.Address.Length > 0)
            .GroupBy(e => e.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Device.Connected).ThenByDescending(Recent).First())
            .ToList();
        if (distinct.Count == 0)
            return [(group[0].Name, "")]; // senza IP: non si crea niente, ma la connessione che c'è non va tolta
        if (distinct.Count == 1)
            return [(distinct[0].Name, distinct[0].Address)];

        var online = distinct.Where(e => e.Device.Connected).ToList();
        List<Entry> keep;
        if (online.Count == 1)
            return [(online[0].Name, online[0].Address)];
        if (online.Count > 1)
            keep = online;
        else
        {
            var ranked = distinct.OrderByDescending(Recent).ToList();
            if (ranked[0].Device.LastContact is not null && Recent(ranked[0]) > Recent(ranked[1]))
                return [(ranked[0].Name, ranked[0].Address)];
            keep = distinct; // nessuna differenza: meglio un PC in più che uno sbagliato
        }

        var existing = doc.Connections.FirstOrDefault(c => c.AutoVpnDevice is { } dev
            && dev.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
        var result = new List<(string Name, string Address)>();
        bool plainTaken = false;
        foreach (var e in keep.OrderBy(e => e.Address, StringComparer.Ordinal))
        {
            if (!plainTaken && existing is not null && existing.Host.Equals(e.Address, StringComparison.OrdinalIgnoreCase))
            {
                plainTaken = true;
                result.Add((e.Name, e.Address));
            }
            else
                result.Add(($"{e.Name} ({e.Address})", e.Address));
        }
        return result;
    }

    private static Guid FreeId(ConnectionsDocument doc, string name)
    {
        for (int attempt = 0; ; attempt++)
        {
            var id = StableId(name, attempt);
            if (id != Guid.Empty && doc.Find(id) is null)
                return id;
        }
    }

    private static bool HasCredentials(RdpConnection c) =>
        c.Username.Trim().Length > 0 || c.CredentialProfileId is not null || c.HasPassword;

    /// <summary>
    /// La connessione dell'utente verso lo stesso PC, se c'è e l'abbinamento è sicuro. Lo stesso PC si riconosce se
    /// l'host è l'IP della VPN, il nome del computer (anche con il suffisso di dominio, es. «pc1.lan») o se il nome della
    /// connessione è il nome del computer, sempre senza distinguere maiuscole. Una connessione che corrisponde a più PC
    /// della rete è ambigua e si scarta; più candidati con accessi diversi sono un dubbio: nessuna copia.
    /// </summary>
    internal static RdpConnection? FindSource(IReadOnlyList<RdpConnection> originals,
        IReadOnlyList<(string Name, string Address)> devices, string name, string address)
    {
        var candidates = originals
            .Where(c => Matches(c, name, address) && !devices.Any(d =>
                !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !(address.Length > 0 && d.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) // il PC stesso (nome distinto fra gemelli)
                && Matches(c, d.Name, d.Address)))
            .Where(c => !c.HasGateway && HasCredentials(c)) // dietro un gateway lo stesso indirizzo è un altro PC
            .ToList();
        if (candidates.Count == 0)
            return null;
        var first = candidates[0];
        if (candidates.Any(c => !SameAccess(first, c)))
            return null;
        // Fra candidati identici negli accessi, quella usata più di recente porta anche le impostazioni.
        return candidates.OrderByDescending(c => c.LastConnectedUtc ?? DateTime.MinValue).First();
    }

    private static bool Matches(RdpConnection c, string name, string address)
    {
        var host = c.Host.Trim();
        if (host.Length == 0)
            return false;
        if (address.Length > 0 && host.Equals(address, StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;
        int dot = host.IndexOf('.');
        // «pc1.lan» = «pc1»; ma un IP (1.2.3.4) non si accorcia.
        if (dot > 0 && !host.All(ch => char.IsDigit(ch) || ch == '.')
            && host[..dot].Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;
        return c.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameAccess(RdpConnection a, RdpConnection b) =>
        a.Username.Trim().Equals(b.Username.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.Domain.Trim().Equals(b.Domain.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.CredentialProfileId == b.CredentialProfileId
        && string.Equals(a.Password ?? "", b.Password ?? "", StringComparison.Ordinal)
        && string.Equals(a.ProtectedPassword ?? "", b.ProtectedPassword ?? "", StringComparison.Ordinal);

    private static void CopyFrom(RdpConnection to, RdpConnection from)
    {
        to.Username = from.Username;
        to.Domain = from.Domain;
        to.Password = from.Password;
        to.ProtectedPassword = from.ProtectedPassword;
        to.CredentialProfileId = from.CredentialProfileId;
        to.Port = from.Port;
        to.ResolutionMode = from.ResolutionMode;
        to.DesktopWidth = from.DesktopWidth;
        to.DesktopHeight = from.DesktopHeight;
        to.InheritFolderResolution = from.InheritFolderResolution;
        to.RedirectClipboard = from.RedirectClipboard;
        to.RedirectDrives = from.RedirectDrives;
        to.RedirectPrinters = from.RedirectPrinters;
        to.RedirectMicrophone = from.RedirectMicrophone;
        to.AudioMode = from.AudioMode;
        to.MonitorMode = from.MonitorMode;
        to.SelectedMonitors = from.SelectedMonitors;
        to.SelectedMonitorDevices = from.SelectedMonitorDevices;
        to.AutoReconnect = from.AutoReconnect;
        to.AutoReconnectMaxAttempts = from.AutoReconnectMaxAttempts;
        to.Quality = from.Quality;
        to.KeepAlive = from.KeepAlive;
        if (to.MacAddress.Length == 0)
            to.MacAddress = from.MacAddress;
    }

    // --- Nomi scelti dall'utente (Plancia «Rinomina…») ---
    // Il nome scelto è il nome della connessione automatica del PC: la cartella è nel documento sincronizzato, quindi il nome
    // arriva su tutti i PC dell'account e si vede ovunque si veda la connessione (elenco, schede, barra della sessione).
    // Apply non tocca mai il nome di una connessione già creata.

    /// <summary>Nomi scelti per i PC della VPN: chiave <see cref="VpnDashboardModel.NameKey(string)"/> del PC → nome.</summary>
    public static Dictionary<string, string> CustomNames(ConnectionsDocument doc)
    {
        var names = new Dictionary<string, string>();
        foreach (var c in doc.Connections)
        {
            if (c.AutoVpnDevice is not { } device || device.Trim().Length == 0)
                continue;
            var name = c.Name.Trim();
            if (name.Length > 0 && !name.Equals(device.Trim(), StringComparison.Ordinal))
                names.TryAdd(VpnDashboardModel.NameKey(device), name);
        }
        return names;
    }

    /// <summary>
    /// Dà al PC <paramref name="deviceName"/> il nome <paramref name="name"/> (vuoto = il nome del PC) rinominando la sua
    /// connessione nella cartella; se la connessione non c'è ancora la crea con l'IP <paramref name="address"/>.
    /// Vero se il documento è cambiato (va salvato e sincronizzato).
    /// </summary>
    public static bool Rename(ConnectionsDocument doc, string deviceName, string? name, string address)
    {
        var device = (deviceName ?? "").Trim();
        if (device.Length == 0)
            return false;
        var clean = (name ?? "").Trim();
        var target = clean.Length == 0 ? device : clean;
        var connections = doc.Connections.Where(c => c.AutoVpnDevice is { } d && d.Trim().Equals(device, StringComparison.OrdinalIgnoreCase)).ToList();
        if (connections.Count == 0)
        {
            var host = VpnDashboardModel.StripMask(address ?? "");
            if (host.Length == 0)
                return false;
            doc.Connections.Add(new RdpConnection
            {
                Id = FreeId(doc, device),
                Name = target,
                Host = host,
                AutoVpnDevice = device,
                Group = doc.AddFolder("", FolderName) ?? FolderName,
            });
            return true;
        }
        bool changed = false;
        foreach (var c in connections.Where(c => c.Name != target))
        {
            c.Name = target;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// I nomi della 1.19.9, salvati solo su questo PC (<see cref="AppSettings.VpnNames"/>), passano nella cartella
    /// sincronizzata e si tolgono dalle impostazioni. Vero se il documento è cambiato.
    /// </summary>
    public static bool MigrateLegacyNames(ConnectionsDocument doc, IReadOnlyList<VpnDashboardDevice> devices)
    {
        var legacy = doc.Settings.VpnNames;
        if (legacy is null || legacy.Count == 0)
            return false;
        bool changed = false;
        foreach (var d in devices)
        {
            string? name = null;
            if (d.DeviceId is { Length: > 0 } id && legacy.TryGetValue("id:" + id, out var byId))
                name = byId;
            else if (legacy.TryGetValue(VpnDashboardModel.NameKey(d.Name), out var byName))
                name = byName;
            if (name is not null)
                changed |= Rename(doc, d.Name, name, d.Address);
        }
        legacy.Clear();
        return true;
    }
}
