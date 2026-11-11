using RdpManager.Core;

namespace RdpManager.Platform.Linux;

/// <summary>Equivalente Linux di PlatformInit (Windows): l'app Linux lo chiama all'avvio, prima di usare il nucleo.</summary>
public static class LinuxPlatformInit
{
    /// <summary>Registra il custode delle password: Secret Service se c'è, altrimenti file cifrato 0600. Non sostituisce uno già registrato.</summary>
    public static void Register(IProcessRunner? runner = null)
    {
        if (!SecretStore.IsConfigured)
            SecretStore.Current = CreateSecretStore(runner);
    }

    public static ISecretStore CreateSecretStore(IProcessRunner? runner = null, string? keyFilePath = null) =>
        new AesGcmSecretStore(new FallbackMasterKeyProvider(
            new SecretToolMasterKeyProvider(runner), new FileMasterKeyProvider(keyFilePath)));

    /// <summary>Il gestore VPN per Linux (NetworkManager se disponibile, altrimenti wg-quick con pkexec).</summary>
    public static LinuxWireGuard CreateVpn(IProcessRunner? runner = null) => new(runner);

    private static readonly Lazy<LinuxWireGuard> SharedVpn = new(() => new LinuxWireGuard());

    /// <summary>Il gestore VPN unico del programma: sessioni, VPN automatica e pannello lo condividono (un solo semaforo).</summary>
    public static LinuxWireGuard Vpn => SharedVpn.Value;
}
