using RdpManager.Core;

namespace RdpManager.Linux.Services;

/// <summary>Numero di iterazioni PBKDF2 usate dal programma. I valori predefiniti sono quelli per gli utenti; solo i test li abbassano.</summary>
internal static class KdfSettings
{
    internal static int ExportIterations { get; set; } = RdpmFile.DefaultIterations;
    internal static int PinIterations { get; set; } = PinLock.DefaultIterations;
    internal static int SyncIterations { get; set; } = SyncCrypto.Iterations;
}
