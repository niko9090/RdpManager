using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.UI;

/// <summary>Voce «VPN (WireGuard)…» del menu Strumenti.</summary>
public sealed partial class MainForm
{
    private VpnForm? _vpn;

    internal void ShowVpn()
    {
        if (_vpn is { IsDisposed: false } open)
        {
            open.Activate();
            return;
        }
        _vpn = new VpnForm(CreateTunnel(), importFile: (owner, path) => ImportConfigFileAsync(path, owner));
        _vpn.FormClosed += (_, _) => _vpn = null;
        _vpn.Show(this);
    }

    private static WireGuardNtTunnel CreateTunnel()
    {
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "vpn");
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RdpManager");
        return new WireGuardNtTunnel(new ProcessElevator(exe), new ScControl(), staging,
            VpnPaths.InstalledHash(programData), VpnPaths.Status(programData),
            VpnPaths.InboxConf(programData, VpnDefaults.TunnelName));
    }

    /// <summary>File di configurazione dato sulla riga di comando (<c>RdpManager.exe file.rdpmconfig</c>, doppio clic): si importa all'apertura.</summary>
    internal string? PendingConfigFile { get; set; }

    /// <summary>Chiede il file con la finestra «Apri» e lo importa; null se si annulla.</summary>
    internal static string? PickConfigFile(IWin32Window owner)
    {
        using var open = new OpenFileDialog
        {
            Title = "Carica file di configurazione",
            Filter = "File di configurazione RdpManager (*.rdpmconfig)|*.rdpmconfig|Configurazione WireGuard (*.conf)|*.conf|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
        };
        return open.ShowDialog(owner) == DialogResult.OK ? open.FileName : null;
    }

    private void RunPendingConfigFile()
    {
        if (PendingConfigFile is not { } path)
            return;
        PendingConfigFile = null;
        BeginInvoke(async () => await ImportConfigFileAsync(path, this));
    }

    /// <summary>
    /// Importa un file di configurazione: salva tutto, registra il PC col server se serve, accende la VPN
    /// (servizio o inbox, nessun UAC in più di quelli già previsti) e mostra il riepilogo «Fatto».
    /// </summary>
    internal async Task ImportConfigFileAsync(string path, IWin32Window? owner = null)
    {
        owner ??= this;
        ConfigImportReport report;
        try
        {
            UseWaitCursor = true;
            report = await ConfigFileImporter.ImportFileAsync(path, _sync, ConnectTunnelAsync, ct: _shutdownCts.Token);
        }
        catch (ConfigFileException ex)
        {
            MessageBox.Show(owner, "Il file di configurazione non è valido.\n\n" + ex.Message, "Configurazione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (FileNotFoundException ex)
        {
            MessageBox.Show(owner, ex.Message, "Configurazione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!IsDisposed)
                UseWaitCursor = false;
        }
        AppLog.Default?.Info("File di configurazione importato: " + (report.Complete ? "completo" : "da completare"));
        if (_sync.SignedIn && !IsDisposed)
            _ = RunSyncAsync(manual: false);
        MessageBox.Show(owner, report.Text, "Configurazione: " + report.Title, MessageBoxButtons.OK,
            report.Complete ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static Task<string?> ConnectTunnelAsync(WireGuardConfig config, CancellationToken ct) => Task.Run<string?>(() =>
    {
        if (!Vpn.WireGuardNative.DllPresent)
            return "manca wireguard.dll accanto al programma.";
        try
        {
            CreateTunnel().Accendi(config);
            return null;
        }
        catch (Exception ex) when (ex is Vpn.VpnException or ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
    }, ct);

    /// <summary>
    /// VPN automatica: se questo PC non ha ancora un profilo e si è nell'account, lo ottiene dal server (chiavi create qui,
    /// al server va solo la pubblica) e accende il tunnel. Restituisce una riga di stato per chi l'ha chiamata ("" = niente da fare).
    /// </summary>
    internal async Task<string> EnsureVpnAsync()
    {
        try
        {
            if (IsDisposed || !_sync.SignedIn || _shutdownCts.IsCancellationRequested || !VpnProvisioning.NeedsProvisioning(VpnProfile.Load()))
                return "";
            var profile = await VpnProvisioning.EnsureAsync(_sync, ct: _shutdownCts.Token);
            if (profile is null)
                return "";
            AppLog.Default?.Info("VPN: profilo ottenuto dal server, indirizzo " + profile.Address);
            if (!Vpn.WireGuardNative.DllPresent)
                return "VPN configurata. Manca wireguard.dll accanto al programma: la VPN non si può accendere.";
            var config = profile.ToConfig();
            await Task.Run(() => CreateTunnel().Accendi(config));
            return "VPN configurata e accesa (indirizzo " + profile.Address + ").";
        }
        catch (SyncServerException ex) when (ex.Code == "vpn_disabled")
        {
            return ""; // il server non offre la VPN automatica: niente avviso a ogni avvio
        }
        catch (OperationCanceledException) when (IsDisposed || _shutdownCts.IsCancellationRequested)
        {
            return ""; // il programma si sta chiudendo: non è un errore della VPN
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or Vpn.VpnException
                                       or ArgumentException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            AppLog.Default?.Info("VPN automatica non riuscita: " + ex.Message);
            return "VPN non configurata: " + ex.Message;
        }
    }
}
