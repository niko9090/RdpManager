using System.Globalization;

namespace RdpmSync;

/// <summary>Impostazioni del server, lette dalle variabili d'ambiente (vedi LEGGIMI.md).</summary>
public sealed class SyncOptions
{
    public string DatabasePath { get; set; } = "rdpm-sync.db";

    /// <summary>Chiave per gli endpoint /v1/admin. Vuota = endpoint di amministrazione spenti (resta il comando crea-account).</summary>
    public string AdminKey { get; set; } = "";

    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8787;

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public int MaxDocumentBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Iterazioni PBKDF2-SHA256 con cui il server custodisce il segreto di accesso (già derivato dal cliente).</summary>
    public int ServerHashIterations { get; set; } = 210_000;

    /// <summary>Accessi sbagliati ammessi nella finestra, per la coppia indirizzo+utente.</summary>
    public int MaxFailuresPerIpUser { get; set; } = 5;
    public int MaxFailuresPerIp { get; set; } = 30;
    public int MaxFailuresPerUser { get; set; } = 20;
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Registrazione dell'account senza chiave di amministrazione (<c>POST /v1/register</c>).</summary>
    public bool OpenRegistration { get; set; }

    /// <summary>Se non vuoto, la registrazione richiede questo codice (e resta attiva anche senza <see cref="OpenRegistration"/>).</summary>
    public string RegistrationCode { get; set; } = "";

    /// <summary>Percorso di wg0.conf. Vuoto = funzione VPN spenta. Il server gestisce solo il blocco tra i marcatori rdpm-sync.</summary>
    public string VpnConfigPath { get; set; } = "";
    public string VpnInterface { get; set; } = "wg0";
    public string WgPath { get; set; } = "wg";

    /// <summary>Chiave pubblica del server; se vuota si legge server.pub accanto a wg0.conf.</summary>
    public string VpnServerPublicKey { get; set; } = "";
    public string VpnEndpoint { get; set; } = "syncnetpro.com:51820";
    public string VpnDns { get; set; } = "";
    public int MaxDevicesPerAccount { get; set; } = 20;

    public static SyncOptions FromEnvironment()
    {
        var o = new SyncOptions();
        static string? V(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } s ? s : null;
        o.DatabasePath = V("RDPM_SYNC_DB") ?? o.DatabasePath;
        o.AdminKey = V("RDPM_SYNC_ADMIN_KEY") ?? "";
        o.ListenAddress = V("RDPM_SYNC_LISTEN") ?? o.ListenAddress;
        if (int.TryParse(V("RDPM_SYNC_PORT"), out var port) && port is > 0 and < 65536)
            o.Port = port;
        if (double.TryParse(V("RDPM_SYNC_TOKEN_DAYS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0)
            o.TokenLifetime = TimeSpan.FromDays(days);
        if (int.TryParse(V("RDPM_SYNC_MAX_DOC_MB"), out var mb) && mb is > 0 and <= 256)
            o.MaxDocumentBytes = mb * 1024 * 1024;
        o.OpenRegistration = V("RDPM_SYNC_OPEN_REGISTRATION") is "1" or "true" or "si";
        o.RegistrationCode = V("RDPM_SYNC_REGISTRATION_CODE") ?? "";
        o.VpnConfigPath = V("RDPM_VPN_CONF") ?? "";
        o.VpnInterface = V("RDPM_VPN_INTERFACE") ?? o.VpnInterface;
        o.WgPath = V("RDPM_VPN_WG") ?? o.WgPath;
        o.VpnServerPublicKey = V("RDPM_VPN_SERVER_PUBLIC_KEY") ?? "";
        o.VpnEndpoint = V("RDPM_VPN_ENDPOINT") ?? o.VpnEndpoint;
        o.VpnDns = V("RDPM_VPN_DNS") ?? "";
        if (int.TryParse(V("RDPM_VPN_MAX_DEVICES"), out var md) && md is > 0 and <= 250)
            o.MaxDevicesPerAccount = md;
        return o;
    }
}
