using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Account di sincronizzazione: accesso (server, utente, password), stato, «Sincronizza ora», esci.</summary>
internal sealed class SyncAccountDialog : TouchForm
{
    private readonly SyncService _sync;
    private readonly Func<Task<SyncOutcome>> _syncNow;
    private readonly Func<SyncOutcome?> _lastOutcome;
    private readonly TextBox _server = new() { Width = 360 };
    private readonly TextBox _user = new() { Width = 360 };
    private readonly TextBox _password = new() { Width = 360, UseSystemPasswordChar = true };
    private readonly Label _passwordLabel = new() { Text = "Password dell'account", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 12, 3, 3) };
    private readonly Button _signIn = new() { Text = "Accedi", AutoSize = true };
    private readonly Button _create = new() { Text = "Registrati", AutoSize = true };
    private readonly Button _syncButton = new() { Text = "Sincronizza ora", AutoSize = true };
    private readonly Button _signOut = new() { Text = "Esci dall'account", AutoSize = true };
    private readonly Func<Task<string>>? _afterSignIn;
    private readonly Button _import = new() { Text = "Importa configurazione…", AutoSize = true };
    private string? _registrationCode;
    private bool _busy;

    public SyncAccountDialog(SyncService sync, Func<Task<SyncOutcome>> syncNow, Func<SyncOutcome?> lastOutcome,
        Func<Task<string>>? afterSignIn = null)
    {
        _afterSignIn = afterSignIn;
        _sync = sync;
        _syncNow = syncNow;
        _lastOutcome = lastOutcome;

        Text = "Account e sincronizzazione";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var close = new Button { Text = "Chiudi", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
            layout.Controls.Add(c);
        }
        layout.Controls.Add(new Label
        {
            Text = "Con lo stesso account, tutti i tuoi PC vedono le stesse connessioni, cartelle e profili di credenziali.\n"
                   + "I dati sono cifrati su questo PC con la password dell'account: il server non li legge e,\n"
                   + "se perdi la password, non si recuperano.",
            AutoSize = true,
        });
        Row("Indirizzo del server (quello del tuo server, per esempio https://vpn.example.com:8443)", _server);
        Row("Nome utente", _user);
        layout.Controls.Add(_passwordLabel);
        layout.Controls.Add(_password);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([_signIn, _create, _syncButton, _signOut, _import]);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_status);
        var bottom = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
        bottom.Controls.Add(close);
        layout.Controls.Add(bottom);
        Controls.Add(layout);
        CancelButton = close;
        AcceptButton = _signIn;

        _signIn.Click += async (_, _) => await SignInAsync(create: false);
        _create.Click += async (_, _) => await SignInAsync(create: true);
        _syncButton.Click += async (_, _) => await SyncNowAsync();
        _signOut.Click += async (_, _) => await SignOutAsync();
        _import.Click += (_, _) => ImportConfig();

        _server.Text = sync.State.ServerUrl;
        _user.Text = sync.State.Username;
        if (!sync.SignedIn && ClientConfig.FindDefault() is { } found)
            Apply(found);
        Refresh_();
    }

    private void Refresh_()
    {
        bool inside = _sync.SignedIn;
        _server.Enabled = _user.Enabled = !inside && !_busy;
        _password.Visible = _passwordLabel.Visible = !inside;
        _password.Enabled = !_busy;
        _signIn.Visible = _create.Visible = _import.Visible = !inside;
        _syncButton.Visible = _signOut.Visible = inside;
        _signIn.Enabled = _create.Enabled = _import.Enabled = _syncButton.Enabled = _signOut.Enabled = !_busy;
        AcceptButton = inside ? _syncButton : _signIn;
        if (_busy)
            return;
        if (!inside)
        {
            _status.Text = "Non hai fatto l'accesso: la sincronizzazione è spenta.";
            return;
        }
        var text = $"Collegato come «{_sync.State.Username}» su {_sync.State.ServerUrl}.";
        if (_sync.State.LastSyncUtc is { } last)
            text += $"\nUltima sincronizzazione: {last.ToLocalTime():dd/MM/yyyy HH:mm:ss}.";
        if (_lastOutcome() is { Ok: false } bad)
            text += "\n" + bad.Message;
        _status.Text = text;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        Refresh_();
        if (busy && text is not null)
            _status.Text = text;
    }

    private static string Explain(Exception ex) => ex switch
    {
        SyncUnreachableException { ServerDown: true } => "Il server è spento o si sta riavviando: riprova fra qualche minuto.",
        SyncUnreachableException => "Il server non risponde. Controlla l'indirizzo e che la VPN sia accesa.",
        SyncServerException { Code: "invalid_credentials" } => "Utente o password sbagliati.",
        SyncServerException { Code: "too_many_attempts", RetryAfterSeconds: { } s } =>
            $"Troppi tentativi sbagliati: riprova fra {Math.Max(1, (s + 59) / 60)} minuti.",
        SyncServerException { Code: "invalid_registration_code" } => "Codice di registrazione sbagliato.",
        SyncServerException { Code: "account_exists" } => "Esiste già un account con questo nome: usa «Accedi».",
        SyncServerException { Code: "registration_disabled" } => "Questo server non accetta registrazioni dall'app: chiedi a chi lo gestisce.",
        SyncServerException s => s.Message,
        _ => ex.Message,
    };

    private async Task SignInAsync(bool create)
    {
        if (_busy)
            return;
        SetBusy(true, create ? "Creo l'account…" : "Accedo…");
        try
        {
            if (create)
                await RegisterAsync();
            await _sync.SignInAsync(_server.Text, _user.Text, _password.Text);
            _password.Clear();
            SetBusy(true, "Sincronizzo…");
            var outcome = await _syncNow();
            SetBusy(false);
            if (!outcome.Ok)
                _status.Text += "
" + outcome.Message;
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or ArgumentException)
        {
            SetBusy(false);
            _status.Text = Explain(ex);
        }
    }

    /// <summary>
    /// Registrazione con codice (o senza, se il server è aperto). Usa il codice del file di configurazione; se il server
    /// ne vuole uno e non c'è (o è sbagliato) lo chiede all'utente.
    /// </summary>
    private async Task RegisterAsync()
    {
        while (true)
        {
            try
            {
                await _sync.RegisterAsync(_server.Text, _user.Text, _password.Text, _registrationCode);
                return;
            }
            catch (SyncServerException ex) when (ex.Code == "invalid_registration_code")
            {
                SetBusy(false);
                var code = TextPromptDialog.Ask(this, "Registrazione",
                    "Codice di registrazione (te lo dà chi gestisce il server):", secret: true);
                if (string.IsNullOrWhiteSpace(code))
                    throw new ArgumentException("Registrazione annullata: serve il codice di registrazione.");
                _registrationCode = code.Trim();
                SetBusy(true, "Creo l'account…");
            }
        }
    }

    private void ImportConfig()
    {
        if (_busy)
            return;
        using var open = new OpenFileDialog
        {
            Title = "Importa configurazione",
            Filter = "Configurazione RdpManager (*.json)|*.json|Tutti i file (*.*)|*.*",
            FileName = ClientConfig.FileName,
        };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            Apply(ClientConfig.Load(open.FileName));
            _status.Text = "Configurazione importata: ora scrivi nome utente e password e premi «Registrati» (o «Accedi»).";
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException)
        {
            _status.Text = "Configurazione non importata: " + ex.Message;
        }
    }

    private void Apply(ClientConfig config)
    {
        if (config.ServerUrl.Length > 0)
            _server.Text = config.ServerUrl;
        if (config.RegistrationCode.Length > 0)
            _registrationCode = config.RegistrationCode;
    }

    private async Task SyncNowAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "Sincronizzo…");
        var outcome = await _syncNow();
        SetBusy(false);
        _status.Text += "\n" + outcome.Message;
    }

    private async Task SignOutAsync()
    {
        if (_busy)
            return;
        if (MessageBox.Show(this, "Uscire dall'account? Le connessioni di questo PC restano dove sono, ma non si sincronizzano più.",
                "Account", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        SetBusy(true, "Esco…");
        await _sync.SignOutAsync();
        SetBusy(false);
    }
}
