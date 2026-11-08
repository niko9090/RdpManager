using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// Cifratura dei segreti legata al sistema (su Windows DPAPI, ambito utente; su Linux per esempio il portachiavi).
/// Il nucleo conosce solo questa interfaccia: l'implementazione si registra all'avvio in <see cref="SecretStore.Current"/>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Cifra i byte con l'entropia indicata (può essere null).</summary>
    byte[] Protect(byte[] data, byte[]? entropy);

    /// <exception cref="CryptographicException">Dato cifrato da un altro utente/PC o corrotto.</exception>
    byte[] Unprotect(byte[] data, byte[]? entropy);
}

/// <summary>Il deposito dei segreti in uso dal programma.</summary>
public static class SecretStore
{
    private static ISecretStore? _current;

    /// <summary>Da impostare all'avvio dall'applicazione; senza, la cifratura non è disponibile.</summary>
    public static ISecretStore Current
    {
        get => _current ?? throw new InvalidOperationException(
            "Nessun ISecretStore registrato: l'applicazione deve impostare SecretStore.Current all'avvio.");
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static bool IsConfigured => _current is not null;
}
