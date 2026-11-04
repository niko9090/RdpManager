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
        Row("Indirizzo del server", _server);
        Row("Nome utente", _user);
        layout.Controls.Add(_passwordLabel);
        layout.Controls.Add(_password);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([_signIn, _create, _syncButton, _signOut]);
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

        _server.Text = sync.State.ServerUrl;
        _user.Text = sync.State.Username;
        Refresh_();
    }

    private void Refresh_()
    {
        bool inside = _sync.SignedIn;
        _server.Enabled = _user.Enabled = !inside && !_busy;
        _password.Visible = _passwordLabel.Visible = !inside;
        _password.Enabled = !_busy;
        _signIn.Visible = _create.Visible = !inside;
        _syncButton.Visible = _signOut.Visible = inside;
        _signIn.Enabled = _create.Enabled = _syncButton.Enabled = _signOut.Enabled = !_busy;
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
        SyncUnreachableException => "Il server non risponde. Controlla l'indirizzo e che la VPN sia accesa.",
        SyncServerException { Code: "invalid_credentials" } => "Utente o password sbagliati.",
        SyncServerException { Code: "too_many_attempts", RetryAfterSeconds: { } s } =>
            $"Troppi tentativi sbagliati: riprova fra {Math.Max(1, (s + 59) / 60)} minuti.",
        SyncServerException { Code: "invalid_registration_code" } => "Codice di registrazione sbagliato.",
        SyncServerException { Code: "account_exists" } => "Esiste già un account con questo nome: usa «Accedi».",
        SyncServerException { Code: "unauthorized" } => "Chiave di amministrazione sbagliata.",
        SyncServerException { Code: "admin_disabled" } => "Sul server la creazione di account via rete è spenta: crealo sul server (vedi LEGGIMI di rdpm-sync).",
        SyncServerException s => s.Message,
        _ => ex.Message,
    };

    private async Task SignInAsync(bool create)
    {
        if (_busy)
            return;
        string? adminKey = null;
        if (create)
        {
            adminKey = TextPromptDialog.Ask(this, "Crea account",
                "Chiave di amministrazione del server (RDPM_SYNC_ADMIN_KEY):", secret: true);
            if (string.IsNullOrEmpty(adminKey))
                return;
        }
        SetBusy(true, create ? "Creo l'account…" : "Accedo…");
        try
        {
            if (adminKey is not null)
                await _sync.CreateAccountAsync(_server.Text, adminKey, _user.Text, _password.Text);
            await _sync.SignInAsync(_server.Text, _user.Text, _password.Text);
            _password.Clear();
            SetBusy(true, "Sincronizzo…");
            var outcome = await _syncNow();
            SetBusy(false);
            if (!outcome.Ok)
                _status.Text += "\n" + outcome.Message;
        }
        catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or ArgumentException)
        {
            SetBusy(false);
            _status.Text = Explain(ex);
        }
    }

    /// <summary>
    /// Registrazione dall'app. Se il server vuole un codice lo chiede; se la registrazione aperta è spenta
    /// ripiega sulla vecchia via con la chiave di amministrazione.
    /// </summary>
    private async Task RegisterAsync()
    {
        string? code = null;
        while (true)
        {
            try
            {
                await _sync.RegisterAsync(_server.Text, _user.Text, _password.Text, code);
                return;
            }
            catch (SyncServerException ex) when (ex.Code == "invalid_registration_code")
            {
                code = TextPromptDialog.Ask(this, "Registrazione", "Codice di registrazione del server:", secret: true);
                if (string.IsNullOrEmpty(code))
                    throw new ArgumentException("Registrazione annullata.");
            }
            catch (SyncServerException ex) when (ex.Code == "registration_disabled")
            {
                var adminKey = TextPromptDialog.Ask(this, "Crea account",
                    "La registrazione libera è spenta. Chiave di amministrazione del server (RDPM_SYNC_ADMIN_KEY):", secret: true);
                if (string.IsNullOrEmpty(adminKey))
                    throw new ArgumentException("Registrazione annullata.");
                await _sync.CreateAccountAsync(_server.Text, adminKey, _user.Text, _password.Text);
                return;
            }
        }
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
