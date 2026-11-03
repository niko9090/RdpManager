using System.Security.Cryptography;
using System.Text;

namespace RdpmSync;

/// <summary>Hash del segreto di accesso con sale casuale: PBKDF2-SHA256.</summary>
public static class PasswordHasher
{
    public static (byte[] Hash, byte[] Salt) Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Compute(password, salt, iterations), salt);
    }

    public static bool Verify(string password, byte[] hash, byte[] salt, int iterations) =>
        CryptographicOperations.FixedTimeEquals(Compute(password, salt, iterations), hash);

    private static byte[] Compute(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
}
