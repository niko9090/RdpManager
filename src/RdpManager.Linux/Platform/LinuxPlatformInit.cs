using RdpManager.Core;

namespace RdpManager.Linux.Platform;

/// <summary>
/// Registrazione delle parti di piattaforma per Linux. Il custode delle password è quello del nucleo
/// (<c>RdpManager.Platform.Linux</c>: Secret Service se c'è, altrimenti file cifrato 0600). Se qualcuno ha già
/// impostato <see cref="SecretStore.Current"/> (per esempio le prove) non si tocca.
/// </summary>
public static class LinuxPlatformInit
{
    public static void Register()
    {
        if (!SecretStore.IsConfigured)
            global::RdpManager.Platform.Linux.LinuxPlatformInit.Register();
    }
}
