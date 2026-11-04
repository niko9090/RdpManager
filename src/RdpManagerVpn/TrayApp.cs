using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using RdpManager.Vpn;

namespace RdpManagerVpn;

/// <summary>L'icona nell'area di notifica con il menu: stato, IP, Connetti/Disconnetti, due impostazioni, Esci.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly string _programData;
    private readonly string _exePath;
    private readonly ScControl _control = new();
    private readonly AutoStartManager _autoStart;
    private readonly NotifyIcon _icon = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly ToolStripMenuItem _statusItem = new("Stato: …") { Enabled = false };
    private readonly ToolStripMenuItem _ipItem = new("Indirizzo IP: —") { Enabled = false };
    private readonly ToolStripMenuItem _toggleItem = new("Connetti");
    private readonly ToolStripMenuItem _autoStartItem = new("Avvia con Windows") { CheckOnClick = false };
    private readonly ToolStripMenuItem _reconnectItem = new("Riconnetti da solo se cade") { CheckOnClick = false };
    private ClientSettings _settings;
    private Icon? _currentIcon;
    private string _lastStateKey = "";

    public TrayApp(string programData, string exePath)
    {
        _programData = programData;
        _exePath = exePath;
        _autoStart = new AutoStartManager(new RegistryRunStore(), exePath);
        _settings = ClientSettings.Load(ClientPaths.Settings(programData));

        // la voce di avvio automatico segue sempre le impostazioni (e si corregge se il programma è stato spostato)
        TryApplyAutoStart(_settings.AvviaConWindows);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_statusItem, _ipItem, new ToolStripSeparator(), _toggleItem, new ToolStripSeparator(),
            _autoStartItem, _reconnectItem, new ToolStripSeparator(), new ToolStripMenuItem("Esci", null, (_, _) => ExitApp())]);
        _toggleItem.Click += (_, _) => ToggleConnection();
        _autoStartItem.Click += (_, _) => SetAutoStart(!_settings.AvviaConWindows);
        _reconnectItem.Click += (_, _) => SetReconnect(!_settings.RiconnettiDaSolo);
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => ToggleConnection();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // all'accesso: se l'utente vuole l'avvio automatico e il servizio è fermo, lo accende
        if (_settings.AvviaConWindows && _control.Exists(ClientPaths.ServiceName) && _control.Query(ClientPaths.ServiceName) == TunnelState.Spento)
            TryStart();
        Refresh();
    }

    private bool ServiceInstalled => _control.Exists(ClientPaths.ServiceName);

    private void Refresh()
    {
        _settings = ClientSettings.Load(ClientPaths.Settings(_programData)); // riletto: lo può cambiare anche altro
        _autoStartItem.Checked = _settings.AvviaConWindows;
        _reconnectItem.Checked = _settings.RiconnettiDaSolo;

        bool installed = ServiceInstalled;
        var state = installed ? _control.Query(ClientPaths.ServiceName) : TunnelState.Spento;
        VpnStatus? details = null;
        if (state == TunnelState.Acceso)
        {
            try
            {
                details = VpnStatus.Parse(File.ReadAllText(ClientPaths.Status(_programData)));
            }
            catch (IOException)
            {
            }
        }
        var text = ClientStatusText.Describe(state, details, DateTime.UtcNow, installed);
        var profile = ClientProfile.Load(ClientPaths.ProfileCandidates(Path.GetDirectoryName(_exePath)!, _programData));

        _statusItem.Text = "Stato: " + text;
        _ipItem.Text = "Indirizzo IP: " + ClientStatusText.Address(state == TunnelState.Acceso ? profile.IpAddress : "");
        bool on = state is TunnelState.Acceso or TunnelState.InAvvio;
        _toggleItem.Text = on ? "Disconnetti" : "Connetti";
        _toggleItem.Enabled = state is not (TunnelState.InArresto);
        _icon.Text = Truncate("RdpManager VPN - " + text, 63);

        var key = text;
        if (key != _lastStateKey)
        {
            _lastStateKey = key;
            var old = _currentIcon;
            _currentIcon = MakeIcon(text switch
            {
                "Connesso" => Color.FromArgb(46, 160, 67),
                "Non connesso" or "Servizio non installato" => Color.Gray,
                "Errore del servizio" => Color.Firebrick,
                _ => Color.Orange,
            });
            _icon.Icon = _currentIcon;
            old?.Dispose();
        }
    }

    private void ToggleConnection()
    {
        try
        {
            if (!ServiceInstalled)
            {
                var risposta = MessageBox.Show("Il servizio della VPN non è installato su questo PC. Installarlo ora? (serve l'autorizzazione di amministratore)",
                    "RdpManager VPN", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (risposta == DialogResult.Yes)
                    RunElevated("--installa-servizio");
                Refresh();
                return;
            }
            var state = _control.Query(ClientPaths.ServiceName);
            if (state is TunnelState.Acceso or TunnelState.InAvvio)
                _control.Stop(ClientPaths.ServiceName);
            else
                ServiceStarter.StartManual(ClientPaths.ServiceName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(ex.Message, "RdpManager VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Refresh();
    }

    private void SetAutoStart(bool enabled)
    {
        if (!Save(_settings with { AvviaConWindows = enabled }))
            return;
        TryApplyAutoStart(enabled);
        Refresh();
    }

    private void SetReconnect(bool enabled)
    {
        if (Save(_settings with { RiconnettiDaSolo = enabled }))
            Refresh();
    }

    private bool Save(ClientSettings settings)
    {
        try
        {
            settings.Save(ClientPaths.Settings(_programData));
            _settings = settings;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Le impostazioni non si riescono a salvare: " + ex.Message, "RdpManager VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void TryApplyAutoStart(bool enabled)
    {
        try
        {
            _autoStart.Apply(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private void TryStart()
    {
        try
        {
            ServiceStarter.StartManual(ClientPaths.ServiceName);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RunElevated(string argument)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_exePath, argument) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit();
        }
        catch (Win32Exception) // UAC rifiutato
        {
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawLine(pen, 9, 17, 14, 22);
            g.DrawLine(pen, 14, 22, 23, 10);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
