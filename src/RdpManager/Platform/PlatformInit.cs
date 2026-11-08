using System.Runtime.CompilerServices;

namespace RdpManager.Core;

/// <summary>
/// Collega al nucleo (RdpManager.Core) le parti legate a Windows: cifratura DPAPI e controllo dei tasti delle scorciatoie.
/// Parte al caricamento della dll e, per sicurezza, da Program.Main e dai test (che non passano da lì).
/// </summary>
public static class PlatformInit
{
    [ModuleInitializer]
    public static void Register()
    {
        if (!SecretStore.IsConfigured)
            SecretStore.Current = new DpapiSecretStore();
        HotkeyRules.Validator = text => GlobalHotkey.TryParse(text, out _);
    }
}