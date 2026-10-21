using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Impostazioni dell'app: aspetto, area di notifica, monitor, barra laterale, avvio, cronologia, scorciatoie.</summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly Dictionary<ShortcutAction, ShortcutBox> _boxes = [];
    private readonly ComboBox _monitor = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RadioButton _pinned = new() { Text = "Fissa", AutoSize = true };
    private readonly RadioButton _autoHide = new() { Text = "A comparsa (bordo sinistro)", AutoSize = true };
    private readonly CheckBox _reopen = new() { Text = "Riapri all'avvio le sessioni lasciate aperte", AutoSize = true };
    private readonly ComboBox _theme = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _tray = new() { Text = "Icona nell'area di notifica con il menu delle connessioni", AutoSize = true };
    private readonly CheckBox _closeToTray = new() { Text = "Chiudendo la finestra resta nell'area di notifica", AutoSize = true };
    private readonly ShortcutBox _globalHotkey = new() { Width = 200 };
    private readonly CheckBox _history = new() { Text = "Annota inizio, fine e motivo di ogni sessione", AutoSize = true };
    private readonly RadioButton _updAuto = new() { Text = "Automatici con conferma: scarica e verifica in sottofondo, poi chiede", AutoSize = true };
    private readonly RadioButton _updNotify = new() { Text = "Solo avviso nella barra di stato", AutoSize = true };
    private readonly RadioButton _updOff = new() { Text = "Spenti (resta «Aiuto › Controlla aggiornamenti»)", AutoSize = true };
    private readonly CheckBox _verboseLog = new() { Text = "Log dettagliato (anche gli indirizzi dei PC; mai le password)", AutoSize = true };
    private readonly CheckBox _manualOrder = new() { Text = "Ordine delle connessioni scelto trascinandole (altrimenti per nome)", AutoSize = true };
    private readonly IReadOnlyList<ScreenInfo> _screens = Screens.All();

    private static readonly (AppTheme Theme, string Text)[] ThemeChoices =
    [
        (AppTheme.System, "Come Windows"),
        (AppTheme.Light, "Chiaro"),
        (AppTheme.Dark, "Scuro"),
    ];

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        Text = "Impostazioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        // ---------- Generale ----------
        var general = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

        _theme.Items.AddRange(ThemeChoices.Select(t => (object)t.Text).ToArray());
        _theme.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeChoices, t => t.Theme == settings.Theme));
        general.Controls.Add(Group("Aspetto", Flow(new Label { Text = "Tema", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, _theme)));

        _tray.Checked = settings.ShowTrayIcon;
        _closeToTray.Checked = settings.CloseToTray;
        _closeToTray.Enabled = _tray.Checked;
        _tray.CheckedChanged += (_, _) => _closeToTray.Enabled = _tray.Checked;
        _globalHotkey.Combo = GlobalHotkey.FromSettings(settings.GlobalHotkey);
        general.Controls.Add(Group("Area di notifica e richiamo dell'app", _tray, _closeToTray,
            Flow(new Label { Text = "Scorciatoia globale per richiamare l'app", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, _globalHotkey),
            Note("Funziona anche da altri programmi (RegisterHotKey). Canc la disattiva.")));

        _monitor.Items.Add("Principale");
        foreach (var s in MonitorLayout.Ordered(_screens))
            _monitor.Items.Add(Screens.Describe(s, _screens));
        var preferred = settings.PreferredMonitor is { } dev
            ? MonitorLayout.Ordered(_screens).FindIndex(s => s.DeviceName.Equals(dev, StringComparison.OrdinalIgnoreCase))
            : -1;
        _monitor.SelectedIndex = preferred + 1;
        general.Controls.Add(Group("Monitor per lo schermo intero", _monitor));

        _pinned.Checked = settings.SidebarMode == SidebarMode.Pinned;
        _autoHide.Checked = !_pinned.Checked;
        general.Controls.Add(Group("Barra laterale", Flow(_pinned, _autoHide)));

        _manualOrder.Checked = settings.ConnectionOrder == ConnectionOrder.Manual;
        general.Controls.Add(Group("Elenco", _manualOrder));

        // ---------- Avvio e aggiornamenti ----------
        var startup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        _reopen.Checked = settings.ReopenSessionsOnStartup;
        startup.Controls.Add(Group("Avvio", _reopen));
        _updAuto.Checked = settings.UpdateMode == UpdateMode.AutomaticWithConfirmation;
        _updNotify.Checked = settings.UpdateMode == UpdateMode.NotifyOnly;
        _updOff.Checked = settings.UpdateMode == UpdateMode.Off;
        startup.Controls.Add(Group("Aggiornamenti", _updAuto, _updNotify, _updOff,
            Note("Controllo all'avvio e ogni 24 ore. L'installer si usa solo se il suo SHA256 coincide con SHA256SUMS.txt della release; l'installazione chiede sempre conferma. La versione portatile apre la pagina della release.")));
        _history.Checked = settings.HistoryEnabled;
        _verboseLog.Checked = settings.VerboseLog;
        var openLogs = new Button { Text = "Apri la cartella dei log", AutoSize = true };
        openLogs.Click += (_, _) => OpenFolder(AppLog.Default?.Directory ?? AppLog.DefaultDirectory);
        startup.Controls.Add(Group("Cronologia e log", _history, _verboseLog,
            Note(@"Log a rotazione in %LOCALAPPDATA%\RdpManager\logs (5 file da 1 MB)."), openLogs));

        // ---------- Scorciatoie ----------
        var keys = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        var map = settings.GetShortcuts();
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var box = new ShortcutBox { Combo = map[action], Width = 170 };
            _boxes[action] = box;
            keys.Controls.Add(new Label { Text = ShortcutMap.Describe(action), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
            keys.Controls.Add(box);
        }
        var defaults = new Button { Text = "Predefinite", AutoSize = true };
        defaults.Click += (_, _) =>
        {
            foreach (var (action, box) in _boxes)
                box.Combo = ShortcutMap.Defaults[action];
        };
        var shortcuts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        shortcuts.Controls.Add(Group("Scorciatoie (valgono anche dentro la sessione remota)", keys,
            Note("Clic su una casella e premi la combinazione (con Ctrl o Alt). Canc la disattiva."),
            Note("Funzionano con RegisterHotKey solo mentre RdpManager è in primo piano: nessun hook di tastiera (dalla 1.3). Se un altro programma usa già una combinazione, la barra di stato lo dice."),
            defaults));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Generale", general));
        tabs.TabPages.Add(Page("Scorciatoie", shortcuts));
        tabs.TabPages.Add(Page("Avvio e aggiornamenti", startup));
        // Il TabControl non si adatta da solo al contenuto: la pagina più grande decide la dimensione.
        Control[] pages = [general, shortcuts, startup];
        var size = new Size(pages.Max(p => p.PreferredSize.Width) + 24, pages.Max(p => p.PreferredSize.Height) + 48);
        tabs.Size = size;
        tabs.Dock = DockStyle.None;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;
        ok.Click += OnOk;

        Controls.Add(tabs);
        Controls.Add(buttons);
        ThemeManager.Apply(this);
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(6), AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    private static FlowLayoutPanel Flow(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        f.Controls.AddRange(items);
        return f;
    }

    private static GroupBox Group(string title, params Control[] content)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.AddRange(content);
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(440, 0) };
        box.Controls.Add(flow);
        return box;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0) };

    private void OnOk(object? sender, EventArgs e)
    {
        var map = new ShortcutMap();
        foreach (var (action, box) in _boxes)
            map[action] = box.Combo;
        var conflicts = map.Conflicts();
        if (conflicts.Count > 0)
        {
            var lines = conflicts.Select(g => $"{map[g[0]]}: {string.Join(", ", g.Select(ShortcutMap.Describe))}");
            MessageBox.Show(this, "La stessa combinazione è usata più volte:\n\n" + string.Join('\n', lines), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        if (GlobalHotkey.ConflictWith(_globalHotkey.Combo, map) is { } clash)
        {
            MessageBox.Show(this, $"La scorciatoia globale {_globalHotkey.Combo} è già usata per «{ShortcutMap.Describe(clash)}».", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        _settings.SetShortcuts(map);
        _settings.PreferredMonitor = _monitor.SelectedIndex <= 0 ? null : MonitorLayout.Ordered(_screens)[_monitor.SelectedIndex - 1].DeviceName;
        _settings.SidebarMode = _pinned.Checked ? SidebarMode.Pinned : SidebarMode.AutoHide;
        _settings.ReopenSessionsOnStartup = _reopen.Checked;
        _settings.Theme = ThemeChoices[Math.Max(0, _theme.SelectedIndex)].Theme;
        _settings.ShowTrayIcon = _tray.Checked;
        _settings.CloseToTray = _tray.Checked && _closeToTray.Checked;
        _settings.GlobalHotkey = _globalHotkey.Combo.IsEmpty ? "" : _globalHotkey.Combo.ToString();
        _settings.HistoryEnabled = _history.Checked;
        _settings.VerboseLog = _verboseLog.Checked;
        _settings.UpdateMode = _updOff.Checked ? UpdateMode.Off : _updNotify.Checked ? UpdateMode.NotifyOnly : UpdateMode.AutomaticWithConfirmation;
        _settings.ConnectionOrder = _manualOrder.Checked ? ConnectionOrder.Manual : ConnectionOrder.Alphabetical;
    }

    /// <summary>Casella che registra una combinazione di tasti invece di testo.</summary>
    private sealed class ShortcutBox : TextBox
    {
        private KeyCombo _combo;

        public ShortcutBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
        }

        public KeyCombo Combo
        {
            get => _combo;
            set
            {
                _combo = value;
                Text = value.IsEmpty ? "(nessuna)" : value.ToString();
            }
        }

        /// <summary>
        /// Ogni combinazione arriva alla casella (anche Ctrl+Tab o Alt+lettera, che altrimenti farebbero
        /// navigazione o scorciatoie), tranne Tab ed Esc da soli: servono a passare oltre e ad annullare.
        /// </summary>
        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            bool plain = (keyData & Keys.Modifiers) == 0;
            if (key is Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0)
                return false;
            if (key is Keys.Escape && plain)
                return false;
            return true;
        }

        protected override bool ProcessDialogKey(Keys keyData) =>
            IsInputKey(keyData) || base.ProcessDialogKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = e.SuppressKeyPress = true;
            var key = e.KeyData & Keys.KeyCode;
            if (key is Keys.Delete or Keys.Back && (e.KeyData & Keys.Modifiers) == 0)
            {
                Combo = default;
                return;
            }
            var combo = KeyCombo.FromKeys(e.KeyData);
            if (!combo.IsEmpty && combo.HasCommandModifier)
                Combo = combo;
        }
    }
}
