using System.Runtime.InteropServices;
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
    private readonly ToolStripButton _monitorButton = new("Monitor ›") { ToolTipText = "Sposta la finestra sul monitor successivo", Visible = false };
    private readonly ToolStripDropDownButton _sendButton = new("Invia") { ToolTipText = "Comandi verso il PC remoto" };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gainsboro, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f),
    };
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _reconnectTimer = new();

    private RdpClientHost? _rdp;
    private bool _pendingConnect;
    private bool _resizeUnsupported;
    private Size _lastSentSize;
    private int _lastSentScale;
    private int _reconnectAttempts;
    private bool _closing;

    public RdpSessionView(RdpConnection connection)
    {
        Connection = connection;
        Dock = DockStyle.Fill;

        _sendButton.DropDownItems.Add("Incolla appunti come digitazione", null, async (_, _) => await PasteClipboardAsTypingAsync());
        _sendButton.DropDownItems.Add("Ctrl+Alt+Canc", null, (_, _) => SendCtrlAltDel());
        _sendButton.DropDownItems.Add(new ToolStripSeparator());
        _sendButton.DropDownItems.Add("Foto della sessione", null, (_, _) => SnapshotRequested?.Invoke(this, EventArgs.Empty));
        _sendButton.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripItem item in _sendButton.DropDownItems)
                item.Enabled = State == SessionState.Connected;
        };

        _bar.Items.Add(_stateLabel);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_reconnectButton);
        _bar.Items.Add(new ToolStripButton("Schermo intero", null, (_, _) => FullScreenRequested?.Invoke(this, EventArgs.Empty)) { ToolTipText = "Schermo intero (Ctrl+Alt+Invio)" });
        _bar.Items.Add(_detachButton);
        _bar.Items.Add(_monitorButton);
        _bar.Items.Add(_sendButton);
        _bar.Items.Add(new ToolStripButton("Apri con mstsc", null, (_, _) => OpenWithMstsc()) { ToolTipText = "Apre la connessione con il client Desktop remoto di Windows" });
        _bar.Items.Add(new ToolStripButton("Chiudi", null, (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty)) { Alignment = ToolStripItemAlignment.Right });
        _reconnectButton.Click += (_, _) => Connect();
        _detachButton.Click += (_, _) => DetachToggleRequested?.Invoke(this, EventArgs.Empty);
        _monitorButton.Click += (_, _) => { if (FindForm() is { } f) Screens.MoveFormToNext(f); };

        _host.Controls.Add(_message);
        Controls.Add(_host);
        Controls.Add(_bar);

        _host.Resize += (_, _) => { if (State == SessionState.Connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyFitToWindow(); };
        _reconnectTimer.Tick += (_, _) => { _reconnectTimer.Stop(); Connect(keepAttempts: true); };
        VisibleChanged += (_, _) => TryStartPendingConnect();
        DpiChangedAfterParent += (_, _) => { if (State == SessionState.Connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };

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

    /// <summary>Tentativi di riconnessione automatica fatti dall'ultima connessione riuscita.</summary>
    public int ReconnectAttempts => _reconnectAttempts;

    /// <summary>Vero mentre si aspetta il prossimo tentativo di riconnessione automatica.</summary>
    public bool ReconnectPending => _reconnectTimer.Enabled;

    /// <summary>Barra comandi della sessione: nascosta a schermo intero, dove c'è la barra di passaggio.</summary>
    public bool ToolbarVisible
    {
        get => _bar.Visible;
        set => _bar.Visible = value;
    }

    public event EventHandler? StateChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? DetachToggleRequested;
    public event EventHandler? FullScreenRequested;
    public event EventHandler? SnapshotRequested;

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
        _monitorButton.Visible = detached && Screen.AllScreens.Length > 1;
    }

    /// <summary>Avvia (o riavvia) la connessione ricreando il controllo ActiveX.</summary>
    public void Connect() => Connect(keepAttempts: false);

    private void Connect(bool keepAttempts)
    {
        _reconnectTimer.Stop();
        if (!keepAttempts)
            _reconnectAttempts = 0;
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
        _rdp.LoginComplete += (_, _) =>
        {
            _reconnectAttempts = 0;
            SetState(SessionState.Connected, "Connessa");
            ApplyFitToWindow();
        };
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
        SetState(SessionState.Connecting, keepAttempts && _reconnectAttempts > 0
            ? $"Riconnessione automatica (tentativo {_reconnectAttempts} di {MaxAttempts})…"
            : "Connessione in corso…");
        TryStartPendingConnect();
    }

    private int MaxAttempts => Math.Clamp(Connection.AutoReconnectMaxAttempts, 0, ReconnectPolicy.MaxAllowedAttempts);

    public void Disconnect()
    {
        _pendingConnect = false;
        _reconnectTimer.Stop();
        if (_rdp is { IsOcxReady: true } && State is SessionState.Connected or SessionState.Connecting)
        {
            try { _rdp.Ocx.Disconnect(); } catch (Exception) { }
        }
    }

    /// <summary>Schermo intero del controllo RDP stesso (usato dalle finestre staccate).</summary>
    public void EnterControlFullScreen()
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

    /// <summary>Porta il fuoco della tastiera dentro la sessione remota.</summary>
    public void FocusSession()
    {
        if (_rdp is { IsHandleCreated: true, Visible: true })
            _rdp.Focus();
    }

    // ---------- comandi verso il PC remoto ----------

    /// <summary>Invia Ctrl+Alt+Canc alla sessione (il tasto vero resterebbe al PC locale).</summary>
    public bool SendCtrlAltDel()
    {
        if (_rdp?.NonScriptable is not { } ns || State != SessionState.Connected)
            return false;
        // Scan code: Ctrl 0x1D, Alt 0x38, Canc 0x53; prima le pressioni poi i rilasci in ordine inverso.
        short[] up = [0, 0, 0, -1, -1, -1];
        int[] keys = [0x1D, 0x38, 0x53, 0x53, 0x38, 0x1D];
        try
        {
            FocusSession();
            ns.SendKeys(keys.Length, ref up[0], ref keys[0]);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            MessageBox.Show(this, "Invio di Ctrl+Alt+Canc non riuscito: " + ex.Message, "RdpManager",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public async Task PasteClipboardAsTypingAsync()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (ExternalException) { text = ""; }
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Negli appunti non c'è testo.", "Incolla come digitazione", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (text.Length > TypingLimit &&
            MessageBox.Show(this, $"Il testo è lungo {text.Length} caratteri: la digitazione richiede un po' di tempo. Continuare?",
                "Incolla come digitazione", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        await TypeTextAsync(text);
    }

    private const int TypingLimit = 2000;

    /// <summary>
    /// Digita il testo nella sessione come se arrivasse dalla tastiera (caratteri Unicode, Invio per gli a capo):
    /// utile dove gli appunti non passano, per esempio nella schermata di accesso. Si ferma se l'app perde il primo piano.
    /// </summary>
    public async Task<int> TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_rdp is null || State != SessionState.Connected)
            return 0;
        FocusSession();
        await Task.Delay(50, cancellationToken);
        int typed = 0;
        var batch = new List<NativeMethods.INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (!NativeMethods.IsOwnProcessForeground() || _rdp is null || State != SessionState.Connected)
                break;
            batch.Clear();
            switch (ch)
            {
                case '\n': AddVirtualKey(batch, 0x0D); break; // VK_RETURN
                case '\t': AddVirtualKey(batch, 0x09); break; // VK_TAB
                default:
                    if (char.IsControl(ch))
                        continue;
                    AddUnicode(batch, ch);
                    break;
            }
            var inputs = batch.ToArray();
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            typed++;
            if (typed % 16 == 0)
                await Task.Delay(15, cancellationToken); // la sessione remota non deve perdere tasti
        }
        return typed;
    }

    private static void AddUnicode(List<NativeMethods.INPUT> list, char ch)
    {
        foreach (var up in new[] { false, true })
            list.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT
                {
                    wScan = ch, dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
                    dwExtraInfo = NativeMethods.OwnInputMarker,
                } },
            });
    }

    private static void AddVirtualKey(List<NativeMethods.INPUT> list, ushort vk)
    {
        foreach (var up in new[] { false, true })
            list.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0, dwExtraInfo = NativeMethods.OwnInputMarker,
                } },
            });
    }

    /// <summary>Immagine della sessione così come è mostrata (null se la sessione non è visibile).</summary>
    public Bitmap? CaptureSession()
    {
        if (_rdp is not { IsHandleCreated: true, Visible: true } rdp || rdp.Width <= 0 || rdp.Height <= 0)
            return null;
        var bmp = new Bitmap(rdp.Width, rdp.Height);
        using var g = Graphics.FromImage(bmp);
        if (FindForm() is { } form && Form.ActiveForm == form)
        {
            // In primo piano: copia diretta dallo schermo, la più fedele.
            g.CopyFromScreen(rdp.PointToScreen(Point.Empty), Point.Empty, rdp.Size);
        }
        else
        {
            var hdc = g.GetHdc();
            try { NativeMethods.PrintWindow(rdp.Handle, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    // ---------- connessione ----------

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

        // Scala del desktop remoto uguale a quella del monitor su cui è la sessione.
        _lastSentScale = MonitorLayout.DesktopScaleFactor(DeviceDpi);
        Try(() => ocx.SetExtendedProperty("DesktopScaleFactor", (uint)_lastSentScale));
        Try(() => ocx.SetExtendedProperty("DeviceScaleFactor", (uint)DeviceScaleFactor(_lastSentScale)));

        dynamic adv = ocx.AdvancedSettings2;
        adv.RDPPort = c.Port;
        adv.RedirectDrives = c.RedirectDrives;
        Try(() => adv.RedirectPrinters = c.RedirectPrinters);
        // La riconnessione del controllo stesso (stessa sessione, pochi secondi) resta sempre attiva;
        // quella dell'app (nuova Connect dopo una disconnessione) dipende da AutoReconnect.
        Try(() => adv.EnableAutoReconnect = true);
        Try(() => adv.SmartSizing = false);
        if (!string.IsNullOrEmpty(c.Password))
            adv.ClearTextPassword = c.Password;

        Try(() => ocx.SecuredSettings2.AudioRedirectionMode = (int)c.AudioMode);

        // Proprietà delle versioni più recenti dell'interfaccia: se mancano si prosegue.
        Try(() =>
        {
            dynamic adv8 = ocx.AdvancedSettings8;
            adv8.RedirectClipboard = c.RedirectClipboard;
            adv8.EnableCredSspSupport = true;  // NLA
            adv8.AuthenticationLevel = 2;      // avvisa se il server non è verificabile
        });
        Try(() => ocx.AdvancedSettings8.AudioCaptureRedirectionMode = c.RedirectMicrophone);
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

    /// <summary>Il controllo accetta come fattore del dispositivo solo 100, 140 o 180.</summary>
    private static int DeviceScaleFactor(int desktopScale) => desktopScale switch
    {
        < 125 => 100,
        < 175 => 140,
        _ => 180,
    };

    /// <summary>
    /// Adatta la risoluzione remota alla scheda o finestra (e alla scala del monitor) senza riconnettere,
    /// se il server lo permette; altrimenti l'immagine viene scalata.
    /// </summary>
    private void ApplyFitToWindow()
    {
        if (_rdp is null || State != SessionState.Connected || Connection.ResolutionMode != ResolutionMode.FitToWindow || _resizeUnsupported)
            return;
        var size = ClampSize(_host.ClientSize);
        int scale = MonitorLayout.DesktopScaleFactor(DeviceDpi);
        if (size == _lastSentSize && scale == _lastSentScale)
            return;
        try
        {
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u,
                (uint)scale, (uint)DeviceScaleFactor(scale));
            _lastSentSize = size;
            _lastSentScale = scale;
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

        if (!_closing && ReconnectPolicy.ShouldReconnect(Connection, reason, _reconnectAttempts))
        {
            _reconnectAttempts++;
            var delay = ReconnectPolicy.DelayBefore(_reconnectAttempts);
            SetState(SessionState.Disconnected,
                $"Disconnessa: {text} — nuovo tentativo tra {delay.TotalSeconds:0} s ({_reconnectAttempts} di {MaxAttempts})");
            _reconnectTimer.Interval = (int)delay.TotalMilliseconds;
            _reconnectTimer.Start();
            return;
        }
        SetState(SessionState.Disconnected, $"Disconnessa: {text}");
    }

    /// <summary>Solo per le prove automatiche: simula uno stato senza un server vero.</summary>
    internal void SetStateForTests(SessionState state, string text) => SetState(state, text);

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

    /// <summary>Parola breve dello stato, la stessa usata in barra laterale e barra di passaggio.</summary>
    public static string StateWord(SessionState state) => state switch
    {
        SessionState.Connected => "Connessa",
        SessionState.Connecting => "In connessione",
        SessionState.Disconnected => "Disconnessa",
        _ => "Pronta",
    };

    public static Color StateColor(SessionState state) => state switch
    {
        SessionState.Connected => Color.FromArgb(46, 160, 67),
        SessionState.Connecting => Color.FromArgb(210, 150, 20),
        SessionState.Disconnected => Color.FromArgb(200, 60, 50),
        _ => Color.Gray,
    };

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
            _closing = true;
            _resizeTimer.Dispose();
            _reconnectTimer.Dispose();
            DisposeClient();
        }
        base.Dispose(disposing);
    }
}
