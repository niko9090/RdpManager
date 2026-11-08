using System.Security.Cryptography;

namespace RdpManager.Core;

/// <summary>Implementazione Windows di <see cref="ISecretStore"/>: DPAPI, ambito utente corrente.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    public byte[] Protect(byte[] data, byte[]? entropy) =>
        ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, byte[]? entropy) =>
        ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
}
