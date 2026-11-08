using System.Security.AccessControl;
using System.Security.Principal;
using RdpManager.Core;

namespace RdpManager.Vpn;

/// <summary>
/// Cartella d'ingresso della VPN (<see cref="VpnPaths.Inbox"/>): l'utente, senza UAC, ci lascia la nuova configurazione e il servizio,
/// all'avvio, la sposta nella cartella protetta e la svuota. L'utente può solo scrivere e cancellare file, non leggerli.
/// </summary>
internal static class VpnInbox
{
    /// <summary>Crea la cartella d'ingresso (serve l'amministratore, quindi si fa dall'assistente) e dà agli utenti il solo diritto di scrivervi.</summary>
    public static void Prepare(string programData)
    {
        var inbox = VpnPaths.Inbox(programData);
        Directory.CreateDirectory(inbox);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
                | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(inbox).SetAccessControl(security);
    }

    /// <summary>
    /// Se nella cartella d'ingresso c'è una configurazione valida la installa (file protetto e impronta) e svuota la cartella;
    /// una non valida si scarta. Restituisce true se ha installato qualcosa.
    /// </summary>
    public static bool Collect(string programData, string tunnelName, Action<string> restrict)
    {
        var inbox = VpnPaths.Inbox(programData);
        if (!Directory.Exists(inbox))
            return false;
        var source = VpnPaths.InboxConf(programData, tunnelName);
        string? text = null;
        try
        {
            if (File.Exists(source))
                text = File.ReadAllText(source);
        }
        catch (IOException)
        {
        }
        Clear(inbox);
        if (text is null)
            return false;
        try
        {
            WireGuardConfig.Parse(text);
        }
        catch (Exception)
        {
            return false;
        }
        var target = VpnPaths.Conf(programData, tunnelName);
        Directory.CreateDirectory(VpnPaths.Directory(programData));
        File.WriteAllText(target, text);
        restrict(target);
        File.WriteAllText(VpnPaths.InstalledHash(programData), VpnHelper.HashConfig(text));
        return true;
    }

    private static void Clear(string inbox)
    {
        foreach (var file in Directory.GetFiles(inbox))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
