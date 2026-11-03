using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Chiavi e cifratura del documento per il server di sincronizzazione (contratto: docs/sync-server-api.md).
/// Dalla password dell'account si ricavano due chiavi indipendenti: il «segreto di accesso» (l'unica cosa che va al
/// server) e la chiave di cifratura del documento (che resta sul PC, solo in memoria).
/// </summary>
public static class SyncCrypto
{
    public const int Iterations = 600_000;
    public const byte BlobVersion = 1;
    private const int NonceSize = 12, TagSize = 16;

    public static string NormalizeUser(string username) => username.Trim().ToLowerInvariant();

    public static byte[] Salt(string username) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("rdpm-sync/v1/salt/" + NormalizeUser(username)));

    private static byte[] Sub(byte[] master, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes(info));

    /// <summary>Deriva entrambe le chiavi con un solo PBKDF2 (circa mezzo secondo con le iterazioni vere).</summary>
    public static (string AuthSecret, byte[] EncryptionKey) Derive(string username, string password, int iterations = Iterations)
    {
        var master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Salt(username), iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return (Convert.ToBase64String(Sub(master, "rdpm-sync/v1/auth")), Sub(master, "rdpm-sync/v1/enc"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static byte[] Aad(string username) => Encoding.UTF8.GetBytes("rdpm-sync/v1/doc/" + NormalizeUser(username));

    /// <summary>Blob = 0x01 | nonce(12) | testo cifrato | tag(16); l'utente è autenticato ma non cifrato.</summary>
    public static byte[] Seal(byte[] key, string username, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(username));
        return [BlobVersion, .. nonce, .. cipher, .. tag];
    }

    /// <summary>Null se la versione è sconosciuta o il tag non torna (password diversa o dati danneggiati).</summary>
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
