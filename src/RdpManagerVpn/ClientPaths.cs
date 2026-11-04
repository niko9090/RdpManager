namespace RdpManagerVpn;

/// <summary>Nomi e posizioni dei file del client VPN.</summary>
public static class ClientPaths
{
    public const string ServiceName = "RdpManagerVpnClient";
    public const string AdapterName = "RdpManagerVPN";
    public const string ProfileFileName = "vpn.conf";

    /// <summary>%ProgramData%\RdpManagerVPN: impostazioni, stato e log (scrivibile dagli utenti, vedi l'installazione del servizio).</summary>
    public static string DataDirectory(string programData) => Path.Combine(programData, "RdpManagerVPN");
    public static string Settings(string programData) => Path.Combine(DataDirectory(programData), "impostazioni.json");
    public static string Status(string programData) => Path.Combine(DataDirectory(programData), "stato.txt");
    public static string Log(string programData) => Path.Combine(DataDirectory(programData), "servizio.log");

    /// <summary>Dove cercare il profilo, in ordine: accanto all'exe, poi in %ProgramData%\RdpManagerVPN.</summary>
    public static IReadOnlyList<string> ProfileCandidates(string exeDirectory, string programData)
        => [Path.Combine(exeDirectory, ProfileFileName), Path.Combine(DataDirectory(programData), ProfileFileName)];
}
