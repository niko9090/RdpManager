namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Colori di stato in un posto solo, gli stessi del programma Windows (RdpSessionView.StateColor e le immagini
/// dell'albero di MainForm). I ViewModel li espongono come testo «#RRGGBB»; i colori delle icone fisse stanno
/// in App.axaml (chiavi «Icon…Brush»).
/// </summary>
public static class UiColors
{
    /// <summary>Collegata / raggiungibile (verde).</summary>
    public const string Connected = "#2EA043";

    /// <summary>Disconnessa (rosso).</summary>
    public const string Disconnected = "#C83C32";

    /// <summary>Non raggiungibile (grigio).</summary>
    public const string Unreachable = "#969696";

    /// <summary>Schermo del PC nell'albero quando la connessione non ha un'etichetta colorata.</summary>
    public const string DefaultScreen = "#4688DC";

    /// <summary>Stato della VPN nella ricerca reti: collegata (fondo e testo).</summary>
    public const string VpnUpBackground = "#D6F0DC";
    public const string VpnUpForeground = "#105A24";

    /// <summary>Stato della VPN nella ricerca reti: non collegata (fondo e testo).</summary>
    public const string VpnDownBackground = "#F8DAD6";
    public const string VpnDownForeground = "#8A1C14";
}
