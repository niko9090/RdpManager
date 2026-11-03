using System.Numerics;
using System.Security.Cryptography;

namespace RdpManager.Core;

/// <summary>
/// Chiavi WireGuard (Curve25519, 32 byte in base64). .NET 8 non ha X25519 nativo: la scalare-moltiplicazione è
/// implementata qui secondo RFC 7748 (scala di Montgomery) e provata con i suoi vettori. Non è a tempo costante:
/// va bene per generare una chiave sul PC locale, non per esporre il calcolo a un osservatore remoto.
/// </summary>
public static class WireGuardKeys
{
    public const int KeySize = 32;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;

    /// <summary>Nuova coppia di chiavi (privata già «clamped» come fa <c>wg genkey</c>).</summary>
    public static (string PrivateKey, string PublicKey) Generate()
    {
        var priv = RandomNumberGenerator.GetBytes(KeySize);
        Clamp(priv);
        return (Convert.ToBase64String(priv), PublicKeyFromPrivate(priv));
    }

    public static string PublicKeyFromPrivate(string privateKeyBase64)
    {
        if (!TryDecode(privateKeyBase64, out var priv))
            throw new FormatException("La chiave privata WireGuard non è valida (servono 32 byte in base64).");
        return PublicKeyFromPrivate(priv);
    }

    public static string PublicKeyFromPrivate(byte[] privateKey)
    {
        var basePoint = new byte[KeySize];
        basePoint[0] = 9;
        return Convert.ToBase64String(X25519(privateKey, basePoint));
    }

    /// <summary>Vero se è una chiave WireGuard ben formata: base64 di esattamente 32 byte.</summary>
    public static bool IsValidKey(string? key) => TryDecode(key, out _);

    internal static bool TryDecode(string? key, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(key) || key.Length != 44)
            return false;
        try
        {
            bytes = Convert.FromBase64String(key);
            return bytes.Length == KeySize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Clamp(byte[] k)
    {
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
    }

    /// <summary>X25519 di RFC 7748 §5: <paramref name="scalar"/> e <paramref name="u"/> di 32 byte, risultato di 32 byte.</summary>
    public static byte[] X25519(byte[] scalar, byte[] u)
    {
        if (scalar.Length != KeySize || u.Length != KeySize)
            throw new ArgumentException("Scalare e coordinata u devono essere di 32 byte.");
        var k = (byte[])scalar.Clone();
        Clamp(k);
        var kn = new BigInteger(k, isUnsigned: true, isBigEndian: false);
        var uc = (byte[])u.Clone();
        uc[31] &= 127; // il bit alto di u si ignora
        var x1 = new BigInteger(uc, isUnsigned: true, isBigEndian: false) % P;

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        bool swap = false;
        for (int t = 254; t >= 0; t--)
        {
            bool kt = !((kn >> t) & 1).IsZero;
            if (swap != kt)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }
            swap = kt;
            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            var s = Mod(da + cb);
            var df = Mod(da - cb);
            x3 = Mod(s * s);
            z3 = Mod(x1 * Mod(df * df));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + A24 * e));
        }
        if (swap)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }
        var result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: false);
        var output = new byte[KeySize];
        Array.Copy(bytes, output, Math.Min(bytes.Length, KeySize));
        return output;
    }

    private static BigInteger Mod(BigInteger v)
    {
        var r = v % P;
        return r.Sign < 0 ? r + P : r;
    }
}
