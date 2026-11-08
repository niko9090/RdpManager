using RdpManager.Core;

namespace RdpManager.Linux.Platform;

/// <summary>
/// Registrazione minima delle parti di piattaforma per Linux: oggi solo il deposito dei segreti
/// (<see cref="FileKeySecretStore"/>). Il lavoro sui servizi Linux del Core (libsecret) potrà sostituire
/// <see cref="Register"/> o impostare prima <see cref="SecretStore.Current"/>: se è già configurato qui non si tocca.
/// </summary>
public static class LinuxPlatformInit
{
    public static void Register()
    {
        if (!SecretStore.IsConfigured)
            SecretStore.Current = new FileKeySecretStore();
    }
}
