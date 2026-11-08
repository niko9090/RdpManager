using System.Security.Cryptography;
using RdpManager.Core;

namespace RdpManager.Linux.Platform;

/// <summary>
/// <see cref="ISecretStore"/> minimo: AES-GCM con una chiave casuale tenuta in un file leggibile solo dall'utente
/// (permessi 600) accanto ai dati. Protegge dalla lettura casuale di connections.json, non da chi ha accesso alla
/// cartella dell'utente: è il segnaposto da sostituire con il portachiavi di sistema (libsecret).
/// Formato: nonce (12) + tag (16) + dati cifrati; l'entropia è il dato associato autenticato.
/// </summary>
public sealed class FileKeySecretStore : ISecretStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _keyPath;
    private byte[]? _key;

    public FileKeySecretStore(string? keyPath = null)
    {
        _keyPath = keyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager", "secret.key");
    }

    public byte[] Protect(byte[] data, byte[]? entropy)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, data, cipher, tag, entropy);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data, byte[]? entropy)
    {
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Dato cifrato non valido.");
        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, entropy);
        return plain;
    }

    private byte[] Key()
    {
        if (_key is not null)
            return _key;
        if (File.Exists(_keyPath))
            return _key = File.ReadAllBytes(_keyPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_keyPath))!);
        var key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var stream = new FileStream(_keyPath, options);
            stream.Write(key);
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            return _key = File.ReadAllBytes(_keyPath); // creata nel frattempo da un altro processo
        }
        return _key = key;
    }
}
