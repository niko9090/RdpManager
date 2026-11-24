using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>Cosa fa la X della finestra principale.</summary>
public enum CloseAction
{
    /// <summary>Esce davvero.</summary>
    Exit,

    /// <summary>Nasconde la finestra: il programma resta attivo con l'icona nella barra.</summary>
    HideToTray,

    /// <summary>Il desktop non ha l'area delle icone: riduce a icona nella barra delle applicazioni, così la finestra non si perde.</summary>
    Minimize,
}

/// <summary>
/// Chiusura della finestra (k-159): resta attivo nella barra invece di uscire, così sessioni e VPN restano accese;
/// all'uscita vera la VPN integrata si spegne, quella gestita dal programma di riferimento (ruolo «Gestore VPN») resta.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Testo dell'avviso mostrato la prima volta che la finestra si nasconde.</summary>
    public const string TrayHintText = "RdpManager resta attivo nella barra. Per uscire usa Esci.";

    private string StateDirectory => Path.GetDirectoryName(RolePath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    private string TrayDefaultMarker => Path.Combine(StateDirectory, "chiusura-barra-predefinito.flag");

    private string TrayHintMarker => Path.Combine(StateDirectory, "chiusura-barra-avviso.flag");

    /// <summary>
    /// «Chiudendo la finestra, resta attivo nella barra» è acceso di serie (come su Windows): la prima volta su questo PC
    /// accende l'opzione (se l'icona è attiva) e lo ricorda, poi la scelta dell'utente resta com'è. Vero se ha cambiato qualcosa.
    /// </summary>
    public bool ApplyCloseToTrayDefaultOnce()
    {
        if (File.Exists(TrayDefaultMarker))
            return false;
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(TrayDefaultMarker, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // niente promemoria: non si tocca l'opzione ogni volta
        }
        var settings = Document.Settings;
        if (!settings.ShowTrayIcon || settings.CloseToTray)
            return false;
        settings.CloseToTray = true;
        Save();
        return true;
    }

    /// <summary>
    /// Decide cosa fa la X. <paramref name="trayReady"/> = l'icona nella barra c'è ed è visibile.
    /// Con «resta attivo nella barra» acceso: nasconde se l'icona c'è, altrimenti riduce a icona.
    /// </summary>
    public CloseAction DecideClose(bool trayReady, bool exiting)
    {
        var closeToTray = Document.Settings.CloseToTray;
        if (ClosePolicy.ShouldHideToTray(closeToTray, trayReady && Document.Settings.ShowTrayIcon, userClosing: true, exitRequested: exiting))
            return CloseAction.HideToTray;
        return closeToTray && !exiting ? CloseAction.Minimize : CloseAction.Exit;
    }

    /// <summary>L'avviso «resta attivo nella barra» la prima volta; null dopo (si ricorda in un file accanto al ruolo).</summary>
    public string? TakeTrayHint()
    {
        if (File.Exists(TrayHintMarker))
            return null;
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(TrayHintMarker, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // si mostra comunque, al massimo la prossima volta lo rivede
        }
        return TrayHintText;
    }

    /// <summary>
    /// Uscita vera: con il ruolo «Desktop remoto + marshmelloVPN» la VPN è quella integrata e si spegne (se è accesa);
    /// con «Gestore marshmelloVPN» ha il suo programma di riferimento e resta com'è. Vero se l'ha spenta.
    /// </summary>
    public async Task<bool> StopIntegratedVpnOnExitAsync(CancellationToken ct = default)
    {
        if (AppRoleStore.Load(RolePath) == AppRole.VpnManager)
            return false;
        try
        {
            var vpn = VpnFactory();
            if (!await vpn.IsConnectedAsync(ct).ConfigureAwait(false))
                return false;
            return (await vpn.DisconnectAsync(ct).ConfigureAwait(false)).Success;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Default?.Info("Spegnimento della VPN all'uscita non riuscito: " + ex.Message);
            return false;
        }
    }
}
