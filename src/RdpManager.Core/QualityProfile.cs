namespace RdpManager.Core;

/// <summary>Profilo di qualità della sessione.</summary>
public enum QualityProfile
{
    /// <summary>Il client misura la rete e sceglie da solo (predefinito, come prima della 1.4).</summary>
    Automatic,
    /// <summary>Rete locale: tutti gli effetti, 32 bit di colore.</summary>
    LocalNetwork,
    /// <summary>Connessione lenta: 16 bit, niente sfondo, animazioni, temi e trascinamento con contenuto; compressione.</summary>
    SlowConnection,
}

/// <summary>Impostazioni concrete di un profilo di qualità, per il controllo RDP e per i file .rdp.</summary>
/// <param name="ColorDepth">Bit per pixel.</param>
/// <param name="ConnectionType">"connection type" dei .rdp: 1 modem, 2 banda larga lenta, 6 LAN, 7 rilevamento automatico.</param>
/// <param name="PerformanceFlags">TS_PERF_* del controllo (sfondo, trascinamento, animazioni, temi…).</param>
public sealed record QualitySettings(
    int ColorDepth, int ConnectionType, bool NetworkAutoDetect, bool DisableWallpaper, bool DisableFullWindowDrag,
    bool DisableMenuAnimations, bool DisableThemes, bool AllowFontSmoothing, bool AllowDesktopComposition,
    bool Compression, bool BitmapCaching)
{
    // Valori TS_PERF_* del controllo RDP.
    public const int PerfDisableWallpaper = 0x1, PerfDisableFullWindowDrag = 0x2, PerfDisableMenuAnimations = 0x4,
        PerfDisableTheming = 0x8, PerfEnableFontSmoothing = 0x80, PerfEnableDesktopComposition = 0x100;

    public int PerformanceFlags =>
        (DisableWallpaper ? PerfDisableWallpaper : 0) | (DisableFullWindowDrag ? PerfDisableFullWindowDrag : 0)
        | (DisableMenuAnimations ? PerfDisableMenuAnimations : 0) | (DisableThemes ? PerfDisableTheming : 0)
        | (AllowFontSmoothing ? PerfEnableFontSmoothing : 0) | (AllowDesktopComposition ? PerfEnableDesktopComposition : 0);

    public static QualitySettings For(QualityProfile profile) => profile switch
    {
        QualityProfile.LocalNetwork => new(32, 6, false, false, false, false, false, true, true, true, true),
        QualityProfile.SlowConnection => new(16, 2, false, true, true, true, true, false, false, true, true),
        _ => new(32, 7, true, false, false, false, false, true, true, true, true),
    };

    /// <summary>Profilo da un file .rdp: "connection type" 6 = rete locale, 1-3 = lenta, altrimenti automatico.</summary>
    public static QualityProfile FromRdp(int? connectionType, bool? networkAutoDetect) =>
        connectionType switch
        {
            6 => QualityProfile.LocalNetwork,
            1 or 2 or 3 => QualityProfile.SlowConnection,
            _ => QualityProfile.Automatic,
        };
}

/// <summary>Sessione tenuta sveglia: il controllo manda un segnale periodico per evitare le chiusure per inattività.</summary>
public static class KeepAlive
{
    /// <summary>Intervallo in millisecondi (il controllo accetta da 10 secondi in su).</summary>
    public const int IntervalMs = 60_000;

    /// <summary>Intervallo da dare al controllo per la connessione (0 = spento).</summary>
    public static int IntervalFor(RdpConnection c) => c.KeepAlive ? IntervalMs : 0;
}
