using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Impostazioni dell'app: scorciatoie, monitor, barra laterale, riapertura delle sessioni.</summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly Dictionary<ShortcutAction, ShortcutBox> _boxes = [];
    private readonly ComboBox _monitor = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RadioButton _pinned = new() { Text = "Fissa", AutoSize = true };
    private readonly RadioButton _autoHide = new() { Text = "A comparsa (bordo sinistro)", AutoSize = true };
    private readonly CheckBox _reopen = new() { Text = "Riapri all'avvio le sessioni lasciate aperte", AutoSize = true };
    private readonly IReadOnlyList<ScreenInfo> _screens = Screens.All();

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        Text = "Impostazioni";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

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
        layout.Controls.Add(Group("Scorciatoie (valgono anche dentro la sessione remota)", keys,
            Note("Clic su una casella e premi la combinazione (con Ctrl o Alt). Canc la disattiva."), defaults));

        _monitor.Items.Add("Principale");
        foreach (var s in MonitorLayout.Ordered(_screens))
            _monitor.Items.Add(Screens.Describe(s, _screens));
        var preferred = settings.PreferredMonitor is { } dev
            ? MonitorLayout.Ordered(_screens).FindIndex(s => s.DeviceName.Equals(dev, StringComparison.OrdinalIgnoreCase))
            : -1;
        _monitor.SelectedIndex = preferred + 1;
        layout.Controls.Add(Group("Monitor per lo schermo intero", _monitor));

        _pinned.Checked = settings.SidebarMode == SidebarMode.Pinned;
        _autoHide.Checked = !_pinned.Checked;
        var sidebar = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        sidebar.Controls.AddRange([_pinned, _autoHide]);
        layout.Controls.Add(Group("Barra laterale", sidebar));

        _reopen.Checked = settings.ReopenSessionsOnStartup;
        layout.Controls.Add(Group("Avvio", _reopen));

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        CancelButton = cancel;
        ok.Click += OnOk;

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private static GroupBox Group(string title, params Control[] content)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.AddRange(content);
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(380, 0) };
        box.Controls.Add(flow);
        return box;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(360, 0) };

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
        _settings.SetShortcuts(map);
        _settings.PreferredMonitor = _monitor.SelectedIndex <= 0 ? null : MonitorLayout.Ordered(_screens)[_monitor.SelectedIndex - 1].DeviceName;
        _settings.SidebarMode = _pinned.Checked ? SidebarMode.Pinned : SidebarMode.AutoHide;
        _settings.ReopenSessionsOnStartup = _reopen.Checked;
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

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0)
                return base.ProcessCmdKey(ref msg, keyData); // Tab per passare alla casella successiva
            if (key is Keys.Delete or Keys.Back && (keyData & Keys.Modifiers) == 0)
            {
                Combo = default;
                return true;
            }
            if (key is Keys.Escape && (keyData & Keys.Modifiers) == 0)
                return base.ProcessCmdKey(ref msg, keyData);
            var combo = KeyCombo.FromKeys(keyData);
            if (!combo.IsEmpty && combo.HasCommandModifier)
                Combo = combo;
            return true;
        }
    }
}
