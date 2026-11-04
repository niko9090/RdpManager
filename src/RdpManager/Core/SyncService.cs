using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>
/// Stato della sincronizzazione con l'account su questo PC (sync.json accanto all'elenco delle connessioni).
/// Password dell'account e token sono cifrati con DPAPI come le altre password del programma; la chiave di cifratura
/// del documento non si scrive mai: si ricava dalla password quando serve.
/// </summary>
public sealed class SyncState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ServerUrl { get; set; } = SyncHttp.PublicServerUrl;
    public string Username { get; set; } = "";
    public string? ProtectedPassword { get; set; }
    public string? ProtectedToken { get; set; }

    /// <summary>ETag del documento all'ultima sincronizzazione.</summary>
    public string? Etag { get; set; }

    /// <summary>Chiave (DPAPI) delle impronte; casuale, una per PC.</summary>
    public string? ProtectedHashKey { get; set; }

    /// <summary>Impronte degli elementi all'ultima sincronizzazione (null = mai sincronizzato).</summary>
    public Dictionary<string, string>? Base { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    public bool SignedIn => Username.Length > 0 && !string.IsNullOrEmpty(ProtectedPassword);

    public static string PathFor(ConnectionStore store) => store.FilePath + ".sync";

    public static SyncState Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path), JsonOptions) is { } s)
                return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // file illeggibile: si riparte da "non collegato"
        }
        return new SyncState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    internal byte[] HashKey()
    {
        if (PasswordProtector.TryUnprotect(ProtectedHashKey, out var text) && text is not null)
            return Convert.FromBase64String(text);
        var key = RandomNumberGenerator.GetBytes(32);
        ProtectedHashKey = PasswordProtector.Protect(Convert.ToBase64String(key));
        Base = null; // impronte fatte con un'altra chiave: non valgono più
        return key;
    }
}

/// <summary>Come il servizio legge e modifica l'elenco vivo (dall'interfaccia: sempre sul suo thread).</summary>
public interface ISyncDocumentAccess
{
    /// <summary>Copia del documento (vedi <see cref="SyncMerge.Snapshot"/>).</summary>
    ConnectionsDocument Snapshot();

    /// <summary>Esegue <paramref name="change"/> sul documento vivo; restituisce ciò che essa restituisce (vero = cambiato).</summary>
    bool Apply(Func<ConnectionsDocument, bool> change);
}

public enum SyncStatus { NotSignedIn, UpToDate, Synced, Offline, AuthFailed, Unreadable, Error }

/// <param name="LocalChanged">L'elenco di questo PC è stato modificato dalla sincronizzazione (da salvare e ridisegnare).</param>
/// <param name="Pushed">Le modifiche di questo PC sono state mandate al server.</param>
public sealed record SyncOutcome(SyncStatus Status, string Message, bool LocalChanged = false, bool Pushed = false)
{
    public bool Ok => Status is SyncStatus.UpToDate or SyncStatus.Synced;
}

/// <summary>
/// Sincronizzazione con l'account sul server rdpm-sync (docs/sync-server-api.md): accesso, cifratura del documento sul
/// PC, lettura/scrittura con ETag (se un altro PC ha salvato nel frattempo: si rilegge, si unisce, si rimanda).
/// </summary>
public sealed class SyncService : IDisposable
{
    private const int MaxAttempts = 5;

    private readonly string _statePath;
    private readonly int _iterations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncHttp? _http;
    private string? _httpUrl;
    private byte[]? _key;
    private string? _auth;
    private string? _keyFor; // utente+password per cui _key vale

    public SyncState State { get; private set; }

    /// <summary>Per le prove: eseguito dopo aver letto il server e prima di scrivere (simula un altro PC che salva nel frattempo).</summary>
    internal Func<Task>? BeforePutForTests { get; set; }

    /// <param name="iterations">Costo delle derivazioni (PBKDF2): solo le prove lo abbassano.</param>
    public SyncService(string statePath, int iterations = SyncCrypto.Iterations)
    {
        _statePath = statePath;
        _iterations = iterations;
        State = SyncState.Load(statePath);
    }

    public bool SignedIn => State.SignedIn;

    private SyncHttp Http(string url)
    {
        url = SyncHttp.NormalizeUrl(url);
        if (_http is null || _httpUrl != url)
        {
            _http?.Dispose();
            _http = new SyncHttp(url);
            _httpUrl = url;
        }
        return _http;
    }

    private (string Auth, byte[] Key) Keys(string user, string password)
    {
        var id = SyncCrypto.NormalizeUser(user) + "\0" + password;
        if (_key is null || _auth is null || _keyFor != id)
        {
            (_auth, _key) = SyncCrypto.Derive(user, password, _iterations);
            _keyFor = id;
        }
        return (_auth, _key);
    }

    /// <summary>Registra l'account dall'app (la password non lascia il PC: parte solo il segreto derivato).</summary>
    public async Task RegisterAsync(string url, string username, string password, string? registrationCode, CancellationToken ct = default)
    {
        ValidateCredentials(username, password);
        var (auth, _) = await Task.Run(() => SyncCrypto.Derive(username, password, _iterations), ct);
        await Http(url).RegisterAsync(username.Trim(), auth, string.IsNullOrWhiteSpace(registrationCode) ? null : registrationCode.Trim(), ct);
    }

    /// <summary>Chiede al server la VPN per questo PC con l'account attuale (rifà il login se il token è scaduto).</summary>
    public async Task<VpnGrant> RequestVpnAsync(string pcName, string publicKey, CancellationToken ct = default)
    {
        if (!SignedIn || !PasswordProtector.TryUnprotect(State.ProtectedPassword, out var password) || password is null)
            throw new InvalidOperationException("Per la VPN serve prima l'accesso all'account.");
        await _gate.WaitAsync(ct);
        try
        {
            var http = Http(State.ServerUrl);
            var token = PasswordProtector.TryUnprotect(State.ProtectedToken, out var saved) && saved is not null
                ? saved
                : await LoginAsync(State.ServerUrl, State.Username, password, ct);
            try
            {
                return await http.RequestVpnDeviceAsync(token, pcName, publicKey, ct);
            }
            catch (SyncServerException ex) when (ex.Code == "unauthorized")
            {
                token = await LoginAsync(State.ServerUrl, State.Username, password, ct);
                return await http.RequestVpnDeviceAsync(token, pcName, publicKey, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Device => Environment.MachineName;

    /// <summary>Crea l'account sul server con la chiave di amministrazione (la password non lascia il PC).</summary>
    public async Task CreateAccountAsync(string url, string adminKey, string username, string password, CancellationToken ct = default)
    {
        ValidateCredentials(username, password);
        var (auth, _) = await Task.Run(() => SyncCrypto.Derive(username, password, _iterations), ct);
        await Http(url).CreateAccountAsync(adminKey, username.Trim(), auth, ct);
    }

    /// <summary>Accesso: verifica utente e password sul server e ricorda il collegamento (password e token con DPAPI).</summary>
    /// <exception cref="SyncServerException">Credenziali rifiutate o troppi tentativi.</exception>
    /// <exception cref="SyncUnreachableException">Server non raggiungibile.</exception>
    public async Task SignInAsync(string url, string username, string password, CancellationToken ct = default)
    {
        ValidateCredentials(username, password);
        url = SyncHttp.NormalizeUrl(url);
        username = username.Trim();
        var (auth, key) = await Task.Run(() => SyncCrypto.Derive(username, password, _iterations), ct);
        var token = await Http(url).LoginAsync(username, auth, Device, ct);

        await _gate.WaitAsync(ct);
        try
        {
            bool sameAccount = State.ServerUrl == url && SyncCrypto.NormalizeUser(State.Username) == SyncCrypto.NormalizeUser(username);
            if (!sameAccount)
            {
                State.Etag = null;
                State.Base = null;
                State.LastSyncUtc = null;
            }
            State.ServerUrl = url;
            State.Username = username;
            State.ProtectedPassword = PasswordProtector.Protect(password);
            State.ProtectedToken = PasswordProtector.Protect(token);
            State.Save(_statePath);
            _auth = auth;
            _key = key;
            _keyFor = SyncCrypto.NormalizeUser(username) + "\0" + password;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Indica il nome utente dell'account.");
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Indica la password dell'account.");
    }

    /// <summary>Esce dall'account: revoca il token (se il server risponde) e dimentica password, token e stato.</summary>
    public async Task SignOutAsync()
    {
        string? token = null;
        if (PasswordProtector.TryUnprotect(State.ProtectedToken, out var t))
            token = t;
        var url = State.ServerUrl;
        await _gate.WaitAsync();
        try
        {
            State.ProtectedPassword = null;
            State.ProtectedToken = null;
            State.Etag = null;
            State.Base = null;
            State.LastSyncUtc = null;
            State.Save(_statePath);
            if (_key is not null)
                CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _auth = null;
            _keyFor = null;
        }
        finally
        {
            _gate.Release();
        }
        if (token is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Http(url).LogoutAsync(token, cts.Token);
            }
            catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or ArgumentException or OperationCanceledException)
            {
                // il token scadrà da solo
            }
        }
    }

    /// <summary>Vero se l'elenco di questo PC è diverso da quello dell'ultima sincronizzazione (o non è mai stato sincronizzato).</summary>
    public bool HasLocalChanges(ConnectionsDocument snapshot)
    {
        if (!SignedIn)
            return false;
        if (State.Base is null)
            return true;
        var fp = new SyncFingerprints(State.HashKey());
        if (State.Base is null)
            return true;
        var now = fp.Of(snapshot);
        return now.Count != State.Base.Count || now.Any(kv => !State.Base.TryGetValue(kv.Key, out var b) || b != kv.Value);
    }

    // ---------- sincronizzazione ----------

    public async Task<SyncOutcome> SyncAsync(ISyncDocumentAccess access, CancellationToken ct = default)
    {
        if (!SignedIn)
            return new SyncOutcome(SyncStatus.NotSignedIn, "Nessun account di sincronizzazione.");
        await _gate.WaitAsync(ct);
        try
        {
            return await RunAsync(access, ct);
        }
        catch (SyncUnreachableException ex)
        {
            return new SyncOutcome(SyncStatus.Offline, ex.Message + " Lavoro in locale e riprovo più tardi.");
        }
        catch (SyncServerException ex) when (ex.Code is "invalid_credentials" or "unauthorized")
        {
            return new SyncOutcome(SyncStatus.AuthFailed, "Utente o password dell'account non più validi: esci e accedi di nuovo.");
        }
        catch (SyncServerException ex) when (ex.Status == HttpStatusCode.TooManyRequests)
        {
            return new SyncOutcome(SyncStatus.Error, ex.Message);
        }
        catch (SyncServerException ex)
        {
            return new SyncOutcome(SyncStatus.Error, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or HttpRequestException or CryptographicException)
        {
            return new SyncOutcome(SyncStatus.Error, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> LoginAsync(string url, string user, string password, CancellationToken ct)
    {
        var (auth, _) = await Task.Run(() => Keys(user, password), ct);
        var token = await Http(url).LoginAsync(user, auth, Device, ct);
        State.ProtectedToken = PasswordProtector.Protect(token);
        State.Save(_statePath);
        return token;
    }

    private async Task<SyncOutcome> RunAsync(ISyncDocumentAccess access, CancellationToken ct)
    {
        var st = State;
        if (!PasswordProtector.TryUnprotect(st.ProtectedPassword, out var password) || password is null)
            return new SyncOutcome(SyncStatus.AuthFailed, "La password dell'account non è più leggibile su questo PC: esci e accedi di nuovo.");
        var user = st.Username;
        var http = Http(st.ServerUrl);
        var (_, key) = await Task.Run(() => Keys(user, password), ct);
        var fp = new SyncFingerprints(st.HashKey());

        string? token = PasswordProtector.TryUnprotect(st.ProtectedToken, out var saved) ? saved : null;
        token ??= await LoginAsync(st.ServerUrl, user, password, ct);

        async Task<T> Authed<T>(Func<string, Task<T>> call)
        {
            try
            {
                return await call(token!);
            }
            catch (SyncServerException ex) when (ex.Code == "unauthorized")
            {
                token = await LoginAsync(st.ServerUrl, user, password, ct);
                return await call(token);
            }
        }

        var local0 = access.Snapshot();
        var baseHashes = st.Base ?? new Dictionary<string, string>();
        bool localChanged = st.Base is null || !SameHashes(fp.Of(local0), baseHashes);

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Senza modifiche locali basta sapere se il server è cambiato (304 = niente da fare).
            var cond = localChanged ? null : st.Etag;
            var remote = await Authed(t => http.GetDocumentAsync(t, cond, ct));
            if (remote.Kind == RemoteDocumentKind.NotModified)
            {
                st.LastSyncUtc = DateTime.UtcNow;
                st.Save(_statePath);
                return new SyncOutcome(SyncStatus.UpToDate, "Tutto aggiornato.");
            }

            ConnectionsDocument remoteDoc;
            if (remote.Kind == RemoteDocumentKind.Missing)
                remoteDoc = new ConnectionsDocument();
            else
            {
                var read = await Task.Run(() => ReadRemote(key, user, password, remote.Blob!), ct);
                if (read is null)
                    return new SyncOutcome(SyncStatus.Unreadable,
                        "Documento sul server non leggibile: password diversa o dati danneggiati. Non ho cambiato niente.");
                remoteDoc = read;
            }

            var merged = SyncMerge.Snapshot(local0);
            SyncMerge.MergeInto(merged, remoteDoc, baseHashes, fp);
            var mergedHashes = fp.Of(merged);

            string? etag = remote.Etag;
            bool pushed = false;
            if (!SameHashes(mergedHashes, fp.Of(remoteDoc)))
            {
                var blob = await Task.Run(() =>
                    SyncCrypto.Seal(key, user, Encoding.UTF8.GetBytes(RdpmFile.Export(merged, password, _iterations))), ct);
                if (BeforePutForTests is { } hook)
                {
                    BeforePutForTests = null;
                    await hook();
                }
                var put = await Authed(t => http.PutDocumentAsync(t, blob, remote.Kind == RemoteDocumentKind.Missing ? null : remote.Etag, ct));
                if (put.Conflict)
                {
                    localChanged = true; // rileggo senza condizione, unisco e riprovo
                    continue;
                }
                etag = put.Etag;
                pushed = true;
            }

            // Porto sull'elenco vivo ciò che è arrivato (se nel frattempo l'utente ha modificato qualcosa, vince lui).
            var local0Hashes = fp.Of(local0);
            bool changed = access.Apply(live => SyncMerge.MergeInto(live, merged, local0Hashes, fp));

            st.Etag = etag;
            st.Base = mergedHashes;
            st.LastSyncUtc = DateTime.UtcNow;
            st.Save(_statePath);
            return new SyncOutcome(SyncStatus.Synced, Describe(changed, pushed), changed, pushed);
        }
        return new SyncOutcome(SyncStatus.Error, "Il server cambia di continuo: riprovo più tardi.");
    }

    private ConnectionsDocument? ReadRemote(byte[] key, string user, string password, byte[] blob)
    {
        var plain = SyncCrypto.Open(key, user, blob);
        if (plain is null)
            return null;
        try
        {
            return RdpmFile.Import(Encoding.UTF8.GetString(plain), password);
        }
        catch (Exception ex) when (ex is InvalidDataException or RdpmPasswordException)
        {
            return null;
        }
    }

    private static bool SameHashes(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Describe(bool received, bool sent) =>
        (received, sent) switch
        {
            (true, true) => "Sincronizzato: ricevute e inviate modifiche.",
            (true, false) => "Sincronizzato: ricevute modifiche dagli altri PC.",
            (false, true) => "Sincronizzato: modifiche inviate al server.",
            _ => "Tutto aggiornato.",
        };

    public void Dispose()
    {
        _http?.Dispose();
        _gate.Dispose();
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
    }
}
