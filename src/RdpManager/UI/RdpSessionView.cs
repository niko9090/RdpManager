using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.UI;

public enum SessionState { Idle, Connecting, Connected, Disconnected }

/// <summary>
/// Una sessione RDP: barra comandi + controllo ActiveX. È un UserControl (ContainerControl) così
/// il controllo ActiveX resta nello stesso contenitore quando la vista viene spostata fra
/// scheda e finestra separata.
/// </summary>
public sealed class RdpSessionView : UserControl
{
    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
    private readonly ToolStripLabel _stateLabel = new();
    private readonly ToolStripButton _reconnectButton = new("Riconnetti");
    private readonly ToolStripButton _detachButton = new("Stacca");
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f),
    };
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 700 };

    private RdpClientHost? _rdp;
    private bool _pendingConnect;
    private bool _resizeUnsupported;
    private Size _lastSentSize;

    public RdpSessionView(RdpConnection connection)
    {
        Connection = connection;
        Dock = DockStyle.Fill;

        _bar.Items.Add(_stateLabel);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_reconnectButton);
        _bar.Items.Add(new ToolStripButton("Schermo intero", null, (_, _) => EnterFullScreen()) { ToolTipText = "Schermo intero (F11). Per uscire: barra di connessione o Ctrl+Alt+Pausa" });
        _bar.Items.Add(_detachButton);
        _bar.Items.Add(new ToolStripButton("Apri con mstsc", null, (_, _) => OpenWithMstsc()) { ToolTipText = "Apre la connessione con il client Desktop remoto di Windows" });
        _bar.Items.Add(new ToolStripButton("Chiudi", null, (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty)) { Alignment = ToolStripItemAlignment.Right });
        _reconnectButton.Click += (_, _) => Connect();
        _detachButton.Click += (_, _) => DetachToggleRequested?.Invoke(this, EventArgs.Empty);

        _host.Controls.Add(_message);
        Controls.Add(_host);
        Controls.Add(_bar);

        _host.Resize += (_, _) => { if (State == SessionState.Connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyFitToWindow(); };
        VisibleChanged += (_, _) => TryStartPendingConnect();

        SetState(SessionState.Idle, "Pronta");
    }

    public RdpConnection Connection { get; }

    /// <summary>Controllo ActiveX corrente (null prima della connessione); per le prove automatiche.</summary>
    internal RdpClientHost? Client => _rdp;

    /// <summary>
    /// Se falso, il controllo non mostra mai la propria finestra di richiesta credenziali
    /// (senza password l'accesso fallisce invece di chiederla). Predefinito: vero.
    /// </summary>
    public bool AllowCredentialPrompt { get; set; } = true;

    /// <summary>Vero se, con <see cref="AllowCredentialPrompt"/> falso, il controllo ha confermato PromptForCredentials = false.</summary>
    internal bool PromptForCredentialsDisabledVerified { get; private set; }

    /// <summary>Ultimo codice di disconnessione ricevuto dal controllo (null se mai disconnessa).</summary>
    public int? LastDisconnectReason { get; private set; }

    /// <summary>Codice esteso (ExtendedDisconnectReason) dell'ultima disconnessione, se disponibile.</summary>
    public int? LastExtendedDisconnectReason { get; private set; }

    /// <summary>Vero mentre il controllo mostra l'avviso sull'identità/certificato del server.</summary>
    public bool AuthenticationWarningVisible { get; private set; }

    /// <summary>Quante volte il controllo ha mostrato l'avviso certificato dall'ultima Connect().</summary>
    internal int AuthenticationWarningCount { get; private set; }
    public SessionState State { get; private set; }
    public string StateText { get; private set; } = "";
    public bool IsDetached { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? DetachToggleRequested;

    /// <summary>
    /// Il controllo sta per mostrare una finestra modale (avviso certificato o richiesta credenziali):
    /// chi ospita la vista deve renderla visibile e portarla in primo piano.
    /// </summary>
    public event EventHandler? AttentionRequired;

    public const string AuthenticationWarningText =
        "In attesa di conferma: certificato non verificabile o credenziali richieste";

    public void SetDetached(bool detached)
    {
        IsDetached = detached;
        _detachButton.Text = detached ? "Riaggancia" : "Stacca";
    }

    /// <summary>Avvia (o riavvia) la connessione ricreando il controllo ActiveX.</summary>
    public void Connect()
    {
        DisposeClient();
        _resizeUnsupported = false;
        AuthenticationWarningCount = 0;
        try
        {
            _rdp = RdpClientHost.Create();
        }
        catch (Exception ex)
        {
            SetState(SessionState.Disconnected, ex.Message + " Usa «Apri con mstsc».");
            return;
        }
        _rdp.Dock = DockStyle.Fill;
        _rdp.Connecting += (_, _) => SetState(SessionState.Connecting, "Connessione in corso…");
        _rdp.Connected += (_, _) => SetState(SessionState.Connecting, "Accesso in corso…");
        _rdp.LoginComplete += (_, _) => { SetState(SessionState.Connected, "Connessa"); ApplyFitToWindow(); };
        _rdp.Disconnected += (_, reason) => OnDisconnected(reason);
        _rdp.AuthenticationWarningDisplayed += (_, _) =>
        {
            AuthenticationWarningVisible = true;
            AuthenticationWarningCount++;
            // Lo stesso evento arriva sia per l'avviso sul certificato sia per la richiesta credenziali.
            SetState(SessionState.Connecting, AuthenticationWarningText);
            AttentionRequired?.Invoke(this, EventArgs.Empty);
        };
        _rdp.AuthenticationWarningDismissed += (_, _) =>
        {
            AuthenticationWarningVisible = false;
            if (State == SessionState.Connecting)
                SetState(SessionState.Connecting, "Connessione in corso…");
        };
        _rdp.FatalError += (_, code) => SetState(SessionState.Disconnected, $"Errore irreversibile del controllo RDP (codice {code}). Prova «Apri con mstsc».");

        _host.Controls.Add(_rdp);
        _rdp.BringToFront();
        _pendingConnect = true;
        SetState(SessionState.Connecting, "Connessione in corso…");
        TryStartPendingConnect();
    }

    public void Disconnect()
    {
        _pendingConnect = false;
        if (_rdp is { IsOcxReady: true } && State is SessionState.Connected or SessionState.Connecting)
        {
            try { _rdp.Ocx.Disconnect(); } catch (Exception) { }
        }
    }

    public void EnterFullScreen()
    {
        if (_rdp is null || State != SessionState.Connected)
            return;
        try { _rdp.Ocx.FullScreen = true; }
        catch (Exception ex) { MessageBox.Show(this, "Schermo intero non disponibile: " + ex.Message, "RdpManager"); }
    }

    public void OpenWithMstsc()
    {
        try { MstscLauncher.Launch(Connection); }
        catch (Exception ex) { MessageBox.Show(this, "Impossibile avviare mstsc.exe: " + ex.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Il controllo ActiveX richiede una finestra visibile: la connessione parte appena c'è.</summary>
    private void TryStartPendingConnect()
    {
        if (!_pendingConnect || _rdp is null || !Visible || !IsHandleCreated)
            return;
        if (!_rdp.Created)
            _rdp.CreateControl();
        if (!_rdp.IsOcxReady)
            return;
        _pendingConnect = false;
        try
        {
            ConfigureClient(_rdp.Ocx);
            _rdp.Ocx.Connect();
        }
        catch (Exception ex)
        {
            SetState(SessionState.Disconnected, "Impossibile avviare la connessione: " + ex.Message);
        }
    }

    private void ConfigureClient(dynamic ocx)
    {
        var c = Connection;
        ocx.Server = c.Host;
        if (!string.IsNullOrEmpty(c.Username))
            ocx.UserName = c.Username;
        if (!string.IsNullOrEmpty(c.Domain))
            ocx.Domain = c.Domain;

        var size = c.ResolutionMode == ResolutionMode.Fixed
            ? new Size(c.DesktopWidth, c.DesktopHeight)
            : ClampSize(_host.ClientSize);
        ocx.DesktopWidth = size.Width;
        ocx.DesktopHeight = size.Height;
        _lastSentSize = size;
        Try(() => ocx.ColorDepth = 32);
        Try(() => ocx.ConnectingText = $"Connessione a {c.Host}…");
        Try(() => ocx.FullScreenTitle = $"{c.DisplayName} - RdpManager");

        dynamic adv = ocx.AdvancedSettings2;
        adv.RDPPort = c.Port;
        adv.RedirectDrives = c.RedirectDrives;
        Try(() => adv.EnableAutoReconnect = true);
        Try(() => adv.SmartSizing = false);
        if (!string.IsNullOrEmpty(c.Password))
            adv.ClearTextPassword = c.Password;

        // Proprietà delle versioni più recenti dell'interfaccia: se mancano si prosegue.
        Try(() =>
        {
            dynamic adv8 = ocx.AdvancedSettings8;
            adv8.RedirectClipboard = c.RedirectClipboard;
            adv8.EnableCredSspSupport = true;  // NLA
            adv8.AuthenticationLevel = 2;      // avvisa se il server non è verificabile
        });
        if (AllowCredentialPrompt)
        {
            Try(() => _rdp!.NonScriptable?.put_PromptForCredentials(string.IsNullOrEmpty(c.Password)));
        }
        else
        {
            // Richiesta disattivata esplicitamente: se non si riesce a garantirlo, non ci si connette.
            var ns = _rdp!.NonScriptable ?? throw new InvalidOperationException("impossibile disattivare la richiesta di credenziali");
            ns.put_PromptForCredentials(false);
            if (ns.get_PromptForCredentials())
                throw new InvalidOperationException("la richiesta di credenziali risulta ancora attiva");
            // Con NLA serve anche questo, altrimenti Windows mostra comunque «Immettere le credenziali».
            var ns5 = _rdp.Ocx as IMsRdpClientNonScriptable5 ?? throw new InvalidOperationException("impossibile vietare la richiesta di credenziali (IMsRdpClientNonScriptable5 assente)");
            ns5.put_AllowPromptingForCredentials(false);
            if (ns5.get_AllowPromptingForCredentials())
                throw new InvalidOperationException("la richiesta di credenziali risulta ancora permessa");
            PromptForCredentialsDisabledVerified = true;
        }
    }

    private void ApplyFitToWindow()
    {
        if (_rdp is null || State != SessionState.Connected || Connection.ResolutionMode != ResolutionMode.FitToWindow || _resizeUnsupported)
            return;
        var size = ClampSize(_host.ClientSize);
        if (size == _lastSentSize)
            return;
        try
        {
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u, 100u, 100u);
            _lastSentSize = size;
        }
        catch (Exception)
        {
            // Server o controllo senza ridimensionamento dinamico: si scala l'immagine.
            _resizeUnsupported = true;
            Try(() => _rdp.Ocx.AdvancedSettings2.SmartSizing = true);
        }
    }

    private void OnDisconnected(int reason)
    {
        LastDisconnectReason = reason;
        LastExtendedDisconnectReason = null;
        AuthenticationWarningVisible = false;
        if (_rdp is not null)
        {
            try { LastExtendedDisconnectReason = (int)_rdp.Ocx.ExtendedDisconnectReason; } catch (Exception) { }
        }
        string text = DisconnectReasons.Describe(reason);
        if (!DisconnectReasons.IsUserInitiated(reason) && _rdp is not null)
        {
            // Descrizione localizzata fornita dal controllo stesso, se disponibile.
            try
            {
                dynamic ocx = _rdp.Ocx;
                string? native = ocx.GetErrorDescription((uint)reason, (uint)ocx.ExtendedDisconnectReason);
                if (!string.IsNullOrWhiteSpace(native))
                    text = native.Trim();
            }
            catch (Exception) { }
        }
        SetState(SessionState.Disconnected, $"Disconnessa: {text}");
    }

    private void SetState(SessionState state, string text)
    {
        State = state;
        StateText = text;
        // Le descrizioni del controllo possono essere su più righe: nella barra va una riga sola.
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        _stateLabel.Text = $"{Connection.DisplayName} — {(oneLine.Length > 110 ? oneLine[..110] + "…" : oneLine)}";
        _stateLabel.ToolTipText = text;
        _stateLabel.ForeColor = state switch
        {
            SessionState.Connected => Color.DarkGreen,
            SessionState.Disconnected => Color.Firebrick,
            SessionState.Connecting => Color.DarkGoldenrod,
            _ => SystemColors.ControlText,
        };
        _reconnectButton.Enabled = state != SessionState.Connecting;
        _message.Text = state == SessionState.Disconnected ? text + "\n\nPremi «Riconnetti» (F5) oppure «Apri con mstsc»." : "";
        if (_rdp is not null)
            _rdp.Visible = state != SessionState.Disconnected;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Size ClampSize(Size s) =>
        new(Math.Clamp(s.Width - s.Width % 2, 640, 8192), Math.Clamp(s.Height, 480, 8192));

    private static void Try(Action action)
    {
        try { action(); } catch (Exception) { }
    }

    private void DisposeClient()
    {
        if (_rdp is null)
            return;
        Disconnect();
        var old = _rdp;
        _rdp = null;
        _host.Controls.Remove(old);
        try { old.Dispose(); } catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resizeTimer.Dispose();
            DisposeClient();
        }
        base.Dispose(disposing);
    }
}
