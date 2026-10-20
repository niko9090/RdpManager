using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core;

/// <summary>Cifratura delle password con DPAPI, ambito utente Windows corrente.</summary>
public static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RdpManager/connections/v1");

    public static string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <exception cref="CryptographicException">Dato cifrato da un altro utente/PC o corrotto.</exception>
    public static string Unprotect(string protectedBase64)
    {
        ArgumentNullException.ThrowIfNull(protectedBase64);
        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    public static bool TryUnprotect(string? protectedBase64, out string? plainText)
    {
        plainText = null;
        if (string.IsNullOrEmpty(protectedBase64))
            return false;
        try
        {
            plainText = Unprotect(protectedBase64);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Formato "password 51:b:" dei file .rdp: DPAPI senza entropia su UTF-16, in esadecimale.</summary>
    public static string ProtectForRdpFile(string plainText)
    {
        var data = ProtectedData.Protect(Encoding.Unicode.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
        return Convert.ToHexString(data);
    }

    public static string? TryUnprotectFromRdpFile(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromHexString(hex.Trim()), null, DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(data).TrimEnd('\0');
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
