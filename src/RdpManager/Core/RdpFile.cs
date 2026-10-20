using System.Globalization;
using System.Text;

namespace RdpManager.Core;

/// <summary>Lettura e generazione di file .rdp (formato "chiave:tipo:valore" di mstsc).</summary>
public static class RdpFile
{
    public static RdpConnection Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static RdpConnection Parse(string content, string? name = null)
    {
        var settings = ReadSettings(content);
        var c = new RdpConnection();

        if (settings.TryGetValue("full address", out var address))
        {
            var (host, port) = SplitAddress(address);
            c.Host = host;
            if (port is int p)
                c.Port = p;
            else if (TryInt(settings, "server port", out var sp) && sp is > 0 and < 65536)
                c.Port = sp;
        }

        if (settings.TryGetValue("username", out var user))
        {
            var slash = user.IndexOf('\\');
            if (slash > 0)
            {
                c.Domain = user[..slash];
                c.Username = user[(slash + 1)..];
            }
            else
            {
                c.Username = user;
            }
        }
        if (settings.TryGetValue("domain", out var domain) && domain.Length > 0)
            c.Domain = domain;

        bool hasSize = TryInt(settings, "desktopwidth", out var w) & TryInt(settings, "desktopheight", out var h)
                       && w > 0 && h > 0;
        if (hasSize)
        {
            c.DesktopWidth = w;
            c.DesktopHeight = h;
        }
        bool windowed = TryInt(settings, "screen mode id", out var mode) && mode == 1;
        bool dynamic = TryInt(settings, "dynamic resolution", out var dyn) && dyn == 1;
        c.ResolutionMode = windowed && hasSize && !dynamic ? ResolutionMode.Fixed : ResolutionMode.FitToWindow;

        if (TryInt(settings, "redirectclipboard", out var clip))
            c.RedirectClipboard = clip != 0;
        c.RedirectDrives =
            (settings.TryGetValue("drivestoredirect", out var drives) && drives.Length > 0)
            || (TryInt(settings, "redirectdrives", out var rd) && rd != 0);

        if (settings.TryGetValue("password 51", out var pwd))
            c.Password = PasswordProtector.TryUnprotectFromRdpFile(pwd);

        c.Name = string.IsNullOrWhiteSpace(name) ? c.Host : name.Trim();
        return c;
    }

    /// <param name="includePassword">Se vero scrive la password cifrata DPAPI ("password 51:b:"), mai in chiaro.</param>
    public static string Generate(RdpConnection c, bool includePassword = false)
    {
        var sb = new StringBuilder();
        void Line(string key, char type, object value) =>
            sb.Append(key).Append(':').Append(type).Append(':')
              .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");

        Line("full address", 's', FormatAddress(c.Host, c.Port));
        if (c.Port != RdpConnection.DefaultPort)
            Line("server port", 'i', c.Port);
        if (!string.IsNullOrEmpty(c.Username))
            Line("username", 's', string.IsNullOrEmpty(c.Domain) ? c.Username : $"{c.Domain}\\{c.Username}");
        if (!string.IsNullOrEmpty(c.Domain))
            Line("domain", 's', c.Domain);

        Line("screen mode id", 'i', 1);
        Line("use multimon", 'i', 0);
        if (c.ResolutionMode == ResolutionMode.Fixed)
        {
            Line("desktopwidth", 'i', c.DesktopWidth);
            Line("desktopheight", 'i', c.DesktopHeight);
            Line("dynamic resolution", 'i', 0);
        }
        else
        {
            Line("dynamic resolution", 'i', 1);
        }
        Line("session bpp", 'i', 32);
        Line("redirectclipboard", 'i', c.RedirectClipboard ? 1 : 0);
        if (c.RedirectDrives)
            Line("drivestoredirect", 's', "*");
        Line("authentication level", 'i', 2);
        Line("enablecredsspsupport", 'i', 1);
        Line("autoreconnection enabled", 'i', 1);
        Line("prompt for credentials", 'i', 0);
        if (includePassword && !string.IsNullOrEmpty(c.Password))
            Line("password 51", 'b', PasswordProtector.ProtectForRdpFile(c.Password));
        return sb.ToString();
    }

    /// <summary>Scrive il file in UTF-16 LE con BOM, come fa mstsc.</summary>
    public static void Save(string path, RdpConnection c, bool includePassword = false) =>
        File.WriteAllText(path, Generate(c, includePassword), Encoding.Unicode);

    /// <summary>Separa host e porta: "pc", "pc:3390", "[::1]:3390", "::1" (IPv6 senza porta).</summary>
    public static (string Host, int? Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']');
            if (end > 0)
            {
                var host = address[1..end];
                var rest = address[(end + 1)..];
                return rest.StartsWith(':') && TryPort(rest[1..], out var p6) ? (host, p6) : (host, null);
            }
        }
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && TryPort(address[(colon + 1)..], out var port))
            return (address[..colon], port);
        return (address, null);
    }

    public static string FormatAddress(string host, int port)
    {
        if (port == RdpConnection.DefaultPort)
            return host;
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    private static Dictionary<string, string> ReadSettings(string content)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().Trim('\0', '﻿');
            int first = line.IndexOf(':');
            if (first <= 0)
                continue;
            int second = line.IndexOf(':', first + 1);
            if (second < 0)
                continue;
            dict[line[..first].Trim()] = line[(second + 1)..].Trim();
        }
        return dict;
    }

    private static bool TryInt(Dictionary<string, string> s, string key, out int value)
    {
        value = 0;
        return s.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
}
