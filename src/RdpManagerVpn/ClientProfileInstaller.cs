using System.Security.AccessControl;
using System.Security.Principal;
using RdpManager.Core;

namespace RdpManagerVpn;

/// <summary>
/// Installa il profilo di questo PC: scrive <c>%ProgramData%\RdpManagerVPN\vpn.conf</c> leggibile solo da SYSTEM e amministratori
/// (come il vpn.conf dell'installer per-PC), registra il servizio se manca e lo avvia. Senza i diritti di amministratore
/// il profilo passa da un file temporaneo nella cartella privata dell'utente e lo mette a posto
/// <c>RdpManagerVpn.exe --applica-profilo</c> rilanciato con UAC (una sola richiesta).
/// </summary>
internal sealed class ClientProfileInstaller(string programData, string exePath, string workFolder,
    Func<bool> isAdministrator, Func<string, int> runElevated, Action<string>? startService = null) : IClientProfileInstaller
{
    public const string TempProfileName = "profilo-temp.conf";
    public const int UacDeclined = 1223;

    public string? Install(WireGuardConfig config)
    {
        try
        {
            if (isAdministrator())
            {
                ApplyProfile(programData, exePath, config.ToConf(), service: new ClientServiceInstaller(new ProcessCommandRunner()));
            }
            else
            {
                Directory.CreateDirectory(workFolder);
                var temp = Path.Combine(workFolder, TempProfileName);
                File.WriteAllText(temp, config.ToConf());
                int code = runElevated(temp);
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (code == UacDeclined)
                    return "serve l'autorizzazione di amministratore per installare il profilo e il servizio: riprova e rispondi «Sì».";
                if (code != 0)
                    return $"l'installazione del profilo e del servizio non è riuscita (codice {code}).";
            }
            (startService ?? (name => ServiceStarter.StartManual(name)))(ClientPaths.ServiceName);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Da amministratore: scrive il profilo protetto, toglie un vecchio vpn.conf accanto all'exe (avrebbe la precedenza)
    /// e registra il servizio se non c'è. <paramref name="service"/> null = non tocca il servizio (prove).
    /// </summary>
    internal static void ApplyProfile(string programData, string exePath, string confText, ClientServiceInstaller? service)
    {
        var dir = ClientPaths.DataDirectory(programData);
        ClientServiceInstaller.PrepareDataDirectory(dir);
        WriteProtected(Path.Combine(dir, ClientPaths.ProfileFileName), confText, protect: true);
        var beside = Path.Combine(Path.GetDirectoryName(exePath)!, ClientPaths.ProfileFileName);
        if (File.Exists(beside))
            File.Delete(beside);
        if (service is not null)
            service.Install(exePath); // sc create se manca, sc config se c'è già: idempotente
    }

    /// <summary>Scrive il profilo e lo rende leggibile solo da SYSTEM e amministratori (SID noti: ogni lingua di Windows).</summary>
    internal static void WriteProtected(string path, string text, bool protect)
    {
        if (!protect)
        {
            File.WriteAllText(path, text);
            return;
        }
        // Il file nasce già con i permessi giusti (nessun momento in cui la chiave privata è leggibile dagli utenti).
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        File.Delete(path); // un profilo precedente (con altri permessi) si sostituisce
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, security);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    /// <summary>Comando <c>--applica-profilo &lt;file&gt;</c> (già amministratore): copia un profilo preparato dall'icona.</summary>
    internal static int RunApplyCommand(string tempFile, string exePath, string programData)
    {
        if (!string.Equals(Path.GetFileName(tempFile), TempProfileName, StringComparison.OrdinalIgnoreCase) || !File.Exists(tempFile))
            return 3;
        var text = File.ReadAllText(tempFile);
        if (!ClientProfile.Check(text).Ok)
            return 4; // non è un profilo valido: non si installa niente
        ApplyProfile(programData, exePath, text, new ClientServiceInstaller(new ProcessCommandRunner()));
        try { File.Delete(tempFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }
}
