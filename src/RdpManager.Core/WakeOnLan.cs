using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RdpManager.Core;

/// <summary>Accensione via rete (Wake-on-LAN): il "magic packet" in UDP broadcast.</summary>
public static class WakeOnLan
{
    public const int DefaultPort = 9;
    public const int MagicPacketLength = 6 + 16 * 6;

    /// <summary>
    /// Normalizza un indirizzo MAC in "AA:BB:CC:DD:EE:FF". Accetta separatori ':' e '-' ("aa-bb-..."),
    /// gruppi Cisco ("aabb.ccdd.eeff") e 12 cifre esadecimali di fila. Rifiuta gli indirizzi tutti a zero o broadcast.
    /// </summary>
    public static bool TryNormalizeMac(string? text, out string normalized)
    {
        normalized = "";
        if (!TryParseMac(text, out var bytes))
            return false;
        normalized = string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        return true;
    }

    public static bool IsValidMac(string? text) => TryParseMac(text, out _);

    /// <exception cref="FormatException">Indirizzo MAC non valido.</exception>
    public static byte[] ParseMac(string? text) =>
        TryParseMac(text, out var bytes) ? bytes : throw new FormatException($"Indirizzo MAC non valido: «{text}». Usa la forma AA:BB:CC:DD:EE:FF.");

    public static bool TryParseMac(string? text, out byte[] bytes)
    {
        bytes = [];
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return false;

        string hex;
        if (t.Length == 17 && (t.Split(':') is { Length: 6 } || t.Split('-') is { Length: 6 }))
        {
            var parts = t.Split(t[2]);
            if (parts.Any(p => p.Length != 2))
                return false;
            hex = string.Concat(parts);
        }
        else if (t.Length == 14 && t.Split('.') is { Length: 3 } dotted && dotted.All(p => p.Length == 4))
        {
            hex = string.Concat(dotted);
        }
        else if (t.Length == 12)
        {
            hex = t;
        }
        else
        {
            return false;
        }

        if (!hex.All(Uri.IsHexDigit))
            return false;
        var result = Convert.FromHexString(hex);
        if (result.All(b => b == 0x00) || result.All(b => b == 0xFF))
            return false;
        bytes = result;
        return true;
    }

    /// <summary>6 byte 0xFF seguiti dal MAC ripetuto 16 volte (102 byte).</summary>
    public static byte[] BuildMagicPacket(byte[] mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        if (mac.Length != 6)
            throw new ArgumentException("Il MAC deve essere di 6 byte.", nameof(mac));
        var packet = new byte[MagicPacketLength];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++)
            Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    /// <exception cref="FormatException">Indirizzo MAC non valido.</exception>
    public static byte[] BuildMagicPacket(string mac) => BuildMagicPacket(ParseMac(mac));

    /// <summary>
    /// Invia il magic packet in UDP broadcast (255.255.255.255 se non indicato) sulla porta 9 e sulla 7,
    /// le due più usate dalle schede di rete.
    /// </summary>
    /// <exception cref="FormatException">Indirizzo MAC non valido.</exception>
    /// <exception cref="SocketException">Invio non riuscito (rete assente).</exception>
    public static async Task SendAsync(string mac, IPAddress? broadcast = null, int port = DefaultPort,
        CancellationToken cancellationToken = default)
    {
        var packet = BuildMagicPacket(mac);
        var address = broadcast ?? IPAddress.Broadcast;
        using var udp = new UdpClient(address.AddressFamily) { EnableBroadcast = true };
        foreach (var p in port == DefaultPort ? new[] { DefaultPort, 7 } : [port])
            await udp.SendAsync(packet, new IPEndPoint(address, p), cancellationToken).ConfigureAwait(false);
    }
}
