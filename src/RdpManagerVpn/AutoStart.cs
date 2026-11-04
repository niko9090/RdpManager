using Microsoft.Win32;

namespace RdpManagerVpn;

/// <summary>Un posto dove registrare i programmi da avviare all'accesso (nella versione vera: HKCU\...\Run).</summary>
public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string command);
    void Remove(string name);
}

/// <summary>Avvio dell'icona all'accesso a Windows: attivabile e rimovibile dal menu e dalla disinstallazione, senza UAC.</summary>
public sealed class AutoStartManager(IRunKeyStore store, string exePath)
{
    public const string ValueName = "RdpManagerVPN";

    public string Command => $"\"{exePath}\"";

    public bool IsEnabled => !string.IsNullOrEmpty(store.Get(ValueName));

    /// <summary>Attiva (riscrivendo anche il percorso, se il programma è stato spostato) o toglie l'avvio automatico.</summary>
    public void Apply(bool enabled)
    {
        if (enabled)
            store.Set(ValueName, Command);
        else
            store.Remove(ValueName);
    }

    /// <summary>Voce presente ma che punta altrove (programma spostato o reinstallato).</summary>
    public bool IsStale => store.Get(ValueName) is { Length: > 0 } current && !current.Equals(Command, StringComparison.OrdinalIgnoreCase);
}

public sealed class RegistryRunStore : IRunKeyStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
