namespace RdpManager.Core;

/// <summary>
/// Regole comuni su cosa fa la chiusura della finestra principale (indipendenti dall'interfaccia, riusabili da Windows e Linux).
/// </summary>
public static class ClosePolicy
{
    public const string TrayHintTitle = "RdpManager";
    public const string TrayHintText = "RdpManager resta attivo qui. Per uscire usa Esci.";

    /// <summary>
    /// Vero se la chiusura deve solo nascondere la finestra nell'area di notifica: l'utente chiude con la X, l'opzione è
    /// accesa, l'icona c'è e non è stata chiesta l'uscita vera («Esci»). Spegnimento e logoff non si riducono mai.
    /// </summary>
    public static bool ShouldHideToTray(bool closeToTray, bool trayAvailable, bool userClosing, bool exitRequested)
        => closeToTray && trayAvailable && userClosing && !exitRequested;

    /// <summary>
    /// Vero se uscendo RdpManager deve spegnere la VPN: solo con la VPN integrata. Con marshmelloVPN installata col suo
    /// programma la VPN resta accesa e la gestisce lui. Con lo spegnimento o il logoff di Windows non si fa nulla (ci pensa il sistema).
    /// </summary>
    public static bool ShouldStopVpnOnExit(bool marshmelloVpnInstalled, bool windowsEnding)
        => !marshmelloVpnInstalled && !windowsEnding;
}
