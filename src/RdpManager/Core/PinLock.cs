using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core;

/// <summary>
/// PIN del blocco dell'app: si salva solo l'impronta PBKDF2-SHA256 con un salt casuale, mai il PIN.
/// Il PIN è di 4-12 cifre.
/// </summary>
public static class PinLock
{
    public const int MinLength = 4, MaxLength = 12;
    public const int DefaultIterations = 200_000;
    private const int SaltSize = 16, HashSize = 32;

    /// <summary>Messaggio d'errore per un PIN non accettabile, oppure null.</summary>
    public static string? Validate(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinLength || pin.Length > MaxLength)
            return $"Il PIN deve avere da {MinLength} a {MaxLength} cifre.";
        if (!pin.All(char.IsAsciiDigit))
            return "Il PIN può contenere solo cifre.";
        if (pin.Distinct().Count() == 1)
            return "Il PIN non può essere una cifra ripetuta.";
        return null;
    }

    /// <summary>Impronta da salvare per <paramref name="pin"/> (salt nuovo a ogni chiamata).</summary>
    public static PinHash Create(string pin, int iterations = DefaultIterations)
    {
        if (Validate(pin) is { } error)
            throw new ArgumentException(error, nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(pin, salt, iterations);
        return new PinHash(Convert.ToBase64String(salt), Convert.ToBase64String(hash), iterations);
    }

    /// <summary>Vero se <paramref name="pin"/> corrisponde all'impronta (confronto a tempo costante).</summary>
    public static bool Verify(string? pin, PinHash? stored)
    {
        if (string.IsNullOrEmpty(pin) || stored is null || !stored.IsValid)
            return false;
        try
        {
            var salt = Convert.FromBase64String(stored.Salt);
            var expected = Convert.FromBase64String(stored.Hash);
            var actual = Derive(pin, salt, stored.Iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashSize);
}

/// <summary>Impronta PBKDF2 del PIN salvata nelle impostazioni (base64).</summary>
public sealed record PinHash(string Salt, string Hash, int Iterations)
{
    public bool IsValid =>
        !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash) && Iterations is >= 10_000 and <= 10_000_000;
}

public sealed partial class ConnectionsDocument
{
    /// <summary>
    /// Toglie dalla memoria le password in chiaro di connessioni e profili, tenendole solo cifrate con DPAPI
    /// (blocco dell'app). Restituisce quante password ha cifrato.
    /// </summary>
    public int SealPasswords()
    {
        int n = 0;
        foreach (var c in Connections.Where(c => !string.IsNullOrEmpty(c.Password)))
        {
            c.ProtectedPassword = PasswordProtector.Protect(c.Password!);
            c.Password = null;
            n++;
        }
        foreach (var p in CredentialProfiles.Where(p => !string.IsNullOrEmpty(p.Password)))
        {
            p.ProtectedPassword = PasswordProtector.Protect(p.Password!);
            p.Password = null;
            n++;
        }
        return n;
    }

    /// <summary>Decifra le password DPAPI (dopo lo sblocco). Quelle non decifrabili restano come sono.</summary>
    public int UnsealPasswords()
    {
        int n = 0;
        foreach (var c in Connections)
            if (c.Password is null && PasswordProtector.TryUnprotect(c.ProtectedPassword, out var plain))
            {
                c.Password = plain;
                c.ProtectedPassword = null;
                n++;
            }
        foreach (var p in CredentialProfiles)
            if (p.Password is null && PasswordProtector.TryUnprotect(p.ProtectedPassword, out var plain))
            {
                p.Password = plain;
                p.ProtectedPassword = null;
                n++;
            }
        return n;
    }

    /// <summary>
    /// PIN dimenticato: le connessioni (e i profili) restano, ma tutte le password salvate si cancellano e il PIN si
    /// toglie. Restituisce quante password sono state cancellate.
    /// </summary>
    public int ForgetPinAndPasswords()
    {
        int n = 0;
        foreach (var c in Connections.Where(c => c.HasPassword))
        {
            c.Password = c.ProtectedPassword = null;
            n++;
        }
        foreach (var p in CredentialProfiles.Where(p => p.HasPassword))
        {
            p.Password = p.ProtectedPassword = null;
            n++;
        }
        Settings.LockPin = null;
        return n;
    }
}

/// <summary>Quando bloccare per inattività.</summary>
public static class LockPolicy
{
    /// <summary>Vero se il blocco è attivo (PIN impostato) e l'utente è inattivo da almeno i minuti scelti (0 = mai).</summary>
    public static bool ShouldLock(AppSettings settings, TimeSpan idle) =>
        settings.LockPin is { IsValid: true } && settings.LockAfterMinutes > 0
        && idle >= TimeSpan.FromMinutes(settings.LockAfterMinutes);
}
