using System.Security.Cryptography;
using System.Text;

namespace RdpmSync;

/// <summary>
/// Derivazione delle chiavi dalla password dell'account e cifratura del documento: è lo schema che deve seguire il
/// cliente (docs/sync-server-api.md). Sta qui perché il comando crea-account e i test usano lo stesso codice;
/// il server in funzione non lo usa mai sui dati degli utenti.
/// </summary>
public static class ClientKeys
{
    public const int Iterations = 600_000;
    public const byte BlobVersion = 1;
    private const int NonceSize = 12, TagSize = 16;

    public static string NormalizeUser(string username) => username.Trim().ToLowerInvariant();

    public static byte[] Salt(string username) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("rdpm-sync/v1/salt/" + NormalizeUser(username)));

    /// <summary>Chiave principale: PBKDF2-SHA256, 600000 iterazioni, 32 byte.</summary>
    public static byte[] Master(string username, string password, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Salt(username), iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Sub(byte[] master, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes(info));

    /// <summary>Segreto di accesso (base64) da mandare al server al posto della password.</summary>
    public static string AuthSecret(string username, string password, int iterations = Iterations) =>
        Convert.ToBase64String(Sub(Master(username, password, iterations), "rdpm-sync/v1/auth"));

    public static byte[] EncryptionKey(string username, string password, int iterations = Iterations) =>
        Sub(Master(username, password, iterations), "rdpm-sync/v1/enc");

    private static byte[] Aad(string username) => Encoding.UTF8.GetBytes("rdpm-sync/v1/doc/" + NormalizeUser(username));

    /// <summary>Blob = 0x01 | nonce(12) | testo cifrato | tag(16).</summary>
    public static byte[] Seal(byte[] key, string username, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(username));
        return [BlobVersion, .. nonce, .. cipher, .. tag];
    }

    public static byte[]? Open(byte[] key, string username, byte[] blob)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != BlobVersion)
            return null;
        var plain = new byte[blob.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize, plain.Length),
                blob.AsSpan(blob.Length - TagSize), plain, Aad(username));
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
