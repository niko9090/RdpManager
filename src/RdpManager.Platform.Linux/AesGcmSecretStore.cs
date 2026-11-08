using System.Security.Cryptography;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>Fornisce la chiave master di 32 byte (la crea al primo uso).</summary>
public interface IMasterKeyProvider
{
    byte[] GetOrCreateKey();
}

/// <summary>
/// <see cref="ISecretStore"/> con AES-256-GCM. La chiave master la fornisce un <see cref="IMasterKeyProvider"/>
/// (portachiavi di sistema o file 0600). L'entropia richiesta dal nucleo entra come dato associato (AAD):
/// un dato cifrato con un'entropia diversa non si apre.
/// Formato: [versione 1 byte = 1][nonce 12][tag 16][testo cifrato].
/// </summary>
public sealed class AesGcmSecretStore : ISecretStore
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16, HeaderSize = 1 + NonceSize + TagSize;
    private readonly IMasterKeyProvider _keys;

    public AesGcmSecretStore(IMasterKeyProvider keys) => _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    public byte[] Protect(byte[] data, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new byte[HeaderSize + data.Length];
        result[0] = Version;
        var nonce = result.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(DeriveKey(), TagSize);
        aes.Encrypt(nonce, data, result.AsSpan(HeaderSize), result.AsSpan(1 + NonceSize, TagSize), entropy);
        return result;
    }

    public byte[] Unprotect(byte[] data, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || data[0] != Version)
            throw new CryptographicException("Dato cifrato non riconosciuto o corrotto.");
        var plain = new byte[data.Length - HeaderSize];
        using var aes = new AesGcm(DeriveKey(), TagSize);
        // AuthenticationTagMismatchException deriva da CryptographicException: come DPAPI.
        aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(HeaderSize), data.AsSpan(1 + NonceSize, TagSize), plain, entropy);
        return plain;
    }

    // Chiave di lavoro derivata dalla master con HKDF: la master non cifra mai direttamente.
    private byte[] DeriveKey() =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _keys.GetOrCreateKey(), 32, salt: null,
            info: Encoding.UTF8.GetBytes("RdpManager/aesgcm/v1"));
}
