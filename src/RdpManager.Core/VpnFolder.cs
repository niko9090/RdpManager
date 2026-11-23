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
        // Un PC per nome (senza distinguere maiuscole): con due nomi uguali l'Id stabile coinciderebbe.
        var list = new List<(string Name, string Address)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            var name = (d.Name ?? "").Trim();
            if (name.Length > 0 && names.Add(name))
                list.Add((name, VpnDashboardModel.StripMask(d.Address ?? "")));
        }
        if (list.Count == 0)
            return false;

        bool changed = false;
        var nameSet = list.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Connessioni automatiche di PC tolti dalla rete (e doppioni dello stesso PC nati da una sincronizzazione).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        changed |= doc.Connections.RemoveAll(c => c.AutoVpnDevice is { } dev
            && (!nameSet.Contains(dev.Trim()) || !seen.Add(dev.Trim()))) > 0;

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
            if (!HasCredentials(existing) && FindSource(originals, list, name, address) is { } source)
            {
                CopyFrom(existing, source);
                changed = true;
            }
        }
        return changed;
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
                !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Matches(c, d.Name, d.Address)))
            .Where(HasCredentials)
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
        to.UseAllMonitors = from.UseAllMonitors;
        to.AutoReconnect = from.AutoReconnect;
        to.AutoReconnectMaxAttempts = from.AutoReconnectMaxAttempts;
        to.Quality = from.Quality;
        to.KeepAlive = from.KeepAlive;
        if (to.MacAddress.Length == 0)
            to.MacAddress = from.MacAddress;
    }
}
