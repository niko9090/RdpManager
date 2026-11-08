using System.Security.Cryptography;

namespace RdpManager.Platform.Linux;

/// <summary>Chiave master in un file con permessi 0600 (riserva quando il Secret Service non c'è).</summary>
public sealed class FileMasterKeyProvider : IMasterKeyProvider
{
    public const int KeySize = 32;

    public string Path { get; }

    public FileMasterKeyProvider(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(Core.AppPaths.DataFolder, "secret.key");

    public bool Exists => TryRead() is not null;

    public byte[] GetOrCreateKey()
    {
        var existing = TryRead();
        if (existing is not null)
            return existing;
        return Create(RandomNumberGenerator.GetBytes(KeySize));
    }

    public byte[]? TryRead()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            var key = File.ReadAllBytes(Path);
            return key.Length == KeySize ? key : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Scrive la chiave (0600 fin dalla creazione: nessuna finestra con permessi larghi) e la restituisce.</summary>
    public byte[] Create(byte[] key)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, options))
            fs.Write(key);
        File.Move(tmp, Path, overwrite: true);
        return key;
    }
}

/// <summary>Chiave master nel Secret Service (GNOME Keyring, KWallet) tramite il programma secret-tool (libsecret).</summary>
public sealed class SecretToolMasterKeyProvider : IMasterKeyProvider
{
    private readonly IProcessRunner _runner;

    public SecretToolMasterKeyProvider(IProcessRunner? runner = null) => _runner = runner ?? new SystemProcessRunner();

    public enum LookupState { Found, NotFound, Unavailable }

    public static IReadOnlyList<string> LookupArguments { get; } = ["lookup", "application", "RdpManager", "kind", "master-key"];
    public static IReadOnlyList<string> StoreArguments { get; } =
        ["store", "--label=RdpManager (chiave password)", "application", "RdpManager", "kind", "master-key"];

    public (LookupState State, byte[]? Key) Lookup()
    {
        var r = _runner.RunAsync("secret-tool", LookupArguments).GetAwaiter().GetResult();
        if (r.Success)
        {
            try
            {
                var key = Convert.FromBase64String(r.StdOut.Trim());
                if (key.Length == FileMasterKeyProvider.KeySize)
                    return (LookupState.Found, key);
            }
            catch (FormatException) { }
            return (LookupState.Unavailable, null); // voce presente ma illeggibile: non sovrascriverla
        }
        // secret-tool esce con 1 e nessun messaggio quando la voce non esiste; ogni altro caso (assente, bloccato, niente bus) è "non disponibile".
        return r.ExitCode == 1 && r.StdErr.Trim().Length == 0 ? (LookupState.NotFound, null) : (LookupState.Unavailable, null);
    }

    public bool TryStore(byte[] key) =>
        _runner.RunAsync("secret-tool", StoreArguments, Convert.ToBase64String(key)).GetAwaiter().GetResult().Success;

    public byte[] GetOrCreateKey()
    {
        var (state, key) = Lookup();
        if (state == LookupState.Found)
            return key!;
        if (state == LookupState.Unavailable)
            throw new CryptographicException("Secret Service non disponibile.");
        var fresh = RandomNumberGenerator.GetBytes(FileMasterKeyProvider.KeySize);
        if (!TryStore(fresh))
            throw new CryptographicException("Impossibile salvare la chiave nel Secret Service.");
        return fresh;
    }
}

/// <summary>
/// Sceglie la chiave master: prima il Secret Service, altrimenti il file 0600.
/// Se il portachiavi è raggiungibile e c'è già una chiave su file (creata quando non lo era), la sposta nel portachiavi
/// senza cambiarla, così i dati già cifrati restano leggibili. Il risultato si memorizza per la durata del processo.
/// </summary>
public sealed class FallbackMasterKeyProvider : IMasterKeyProvider
{
    private readonly SecretToolMasterKeyProvider _keyring;
    private readonly FileMasterKeyProvider _file;
    private byte[]? _cached;

    public FallbackMasterKeyProvider(SecretToolMasterKeyProvider keyring, FileMasterKeyProvider file)
    {
        _keyring = keyring;
        _file = file;
    }

    /// <summary>"keyring" o "file" dopo la prima risoluzione.</summary>
    public string? Source { get; private set; }

    public byte[] GetOrCreateKey()
    {
        if (_cached is not null)
            return _cached;
        lock (this)
        {
            if (_cached is not null)
                return _cached;
            var (state, key) = _keyring.Lookup();
            if (state == SecretToolMasterKeyProvider.LookupState.Found)
                return Use(key!, "keyring");
            var fileKey = _file.TryRead();
            if (state == SecretToolMasterKeyProvider.LookupState.NotFound)
            {
                var candidate = fileKey ?? RandomNumberGenerator.GetBytes(FileMasterKeyProvider.KeySize);
                if (_keyring.TryStore(candidate))
                    return Use(candidate, "keyring");
            }
            return Use(fileKey ?? _file.Create(RandomNumberGenerator.GetBytes(FileMasterKeyProvider.KeySize)), "file");
        }
    }

    private byte[] Use(byte[] key, string source)
    {
        _cached = key;
        Source = source;
        return key;
    }
}
