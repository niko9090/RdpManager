using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Colori di un tema dell'interfaccia.</summary>
internal sealed record ThemePalette(
    bool IsDark,
    Color Window,
    Color Control,
    Color Input,
    Color Text,
    Color GrayText,
    Color Border,
    Color Highlight,
    Color HighlightText);

/// <summary>
/// Tema chiaro o scuro dell'interfaccia. I colori si applicano ai controlli che usano i colori standard;
/// quelli con colori propri (barra di passaggio, sessione remota, campioni di colore) restano come sono.
/// L'impostazione «Sistema» legge, in sola lettura, la preferenza di Windows «Modalità app».
/// </summary>
internal static class ThemeManager
{
    public static readonly ThemePalette Light = new(false,
        Window: SystemColors.Window, Control: SystemColors.Control, Input: SystemColors.Window,
        Text: SystemColors.ControlText, GrayText: SystemColors.GrayText, Border: SystemColors.ControlDark,
        Highlight: SystemColors.Highlight, HighlightText: SystemColors.HighlightText);

    public static readonly ThemePalette Dark = new(true,
        Window: Color.FromArgb(32, 33, 36), Control: Color.FromArgb(43, 45, 49), Input: Color.FromArgb(52, 54, 59),
        Text: Color.FromArgb(232, 232, 235), GrayText: Color.FromArgb(150, 152, 160), Border: Color.FromArgb(70, 72, 80),
        Highlight: Color.FromArgb(45, 95, 165), HighlightText: Color.White);

    private static readonly ConditionalWeakTable<Control, object> Excluded = new();
    private static readonly ConditionalWeakTable<ToolStrip, Box<ToolStripRenderMode>> OriginalRenderMode = new();

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }

    public static ThemePalette Current { get; private set; } = Light;

    /// <summary>Tema scelto nelle impostazioni (Sistema = segue Windows).</summary>
    public static AppTheme Setting { get; private set; } = AppTheme.System;

    /// <summary>Vero se Windows usa la modalità scura per le app (HKCU, sola lettura; in caso di dubbio: chiaro).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static ThemePalette Resolve(AppTheme theme) => theme switch
    {
        AppTheme.Dark => Dark,
        AppTheme.Light => Light,
        _ => SystemPrefersDark() ? Dark : Light,
    };

    /// <summary>Sceglie il tema; i menu (ToolStripManager) lo usano subito, i controlli dopo <see cref="Apply"/>.</summary>
    public static void SetTheme(AppTheme theme)
    {
        Setting = theme;
        Current = Resolve(theme);
        ToolStripManager.Renderer = Current.IsDark ? new DarkRenderer(Current) : new ToolStripProfessionalRenderer();
    }

    /// <summary>Il controllo (e tutto ciò che contiene) tiene i suoi colori.</summary>
    public static void Exclude(Control control) => Excluded.AddOrUpdate(control, true);

    /// <summary>Applica il tema corrente al controllo e ai figli (e ai menu contestuali indicati).</summary>
    public static void Apply(Control root, params ContextMenuStrip[] menus)
    {
        var p = Current;
        ApplyTo(root, p);
        foreach (var m in menus)
            ApplyToStrip(m, p);
        if (root is Form form)
        {
            if (form.IsHandleCreated)
                SetDarkTitleBar(form.Handle, p.IsDark);
            else
                form.HandleCreated += (_, _) => SetDarkTitleBar(form.Handle, Current.IsDark);
        }
    }

    private static void ApplyTo(Control c, ThemePalette p)
    {
        if (Excluded.TryGetValue(c, out _))
            return;
        switch (c)
        {
            case AxHost:
                return; // controllo ActiveX (sessione remota): colori suoi
            case ToolStrip strip:
                ApplyToStrip(strip, p);
                return; // gli elementi del menu non sono controlli figli
            case TextBoxBase or ComboBox or ListBox or NumericUpDown or ListView:
                c.BackColor = p.Input;
                c.ForeColor = p.Text;
                if (c is ComboBox combo)
                    combo.FlatStyle = p.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                break;
            case TreeView tree:
                tree.BackColor = p.Window;
                tree.ForeColor = p.Text;
                tree.LineColor = p.GrayText;
                break;
            case Button button:
                if (p.IsDark)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = p.Border;
                    button.BackColor = p.Input;
                    button.ForeColor = p.Text;
                }
                else
                {
                    button.FlatStyle = FlatStyle.Standard;
                    button.BackColor = SystemColors.Control;
                    button.UseVisualStyleBackColor = true;
                    button.ForeColor = SystemColors.ControlText;
                }
                break;
            case TabPage page:
                page.BackColor = p.Control;
                page.ForeColor = p.Text;
                break;
            case TabControl tabs:
                ThemeTabs(tabs, p);
                break;
            case LinkLabel link:
                link.LinkColor = p.IsDark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(0, 0, 255);
                link.ActiveLinkColor = p.IsDark ? Color.FromArgb(170, 200, 255) : Color.Red;
                link.BackColor = MapBack(link.BackColor, p);
                link.ForeColor = MapFore(link.ForeColor, p);
                break;
            default:
                c.BackColor = MapBack(c.BackColor, p);
                c.ForeColor = MapFore(c.ForeColor, p);
                break;
        }
        foreach (Control child in c.Controls)
            ApplyTo(child, p);
    }

    private static void ApplyToStrip(ToolStrip strip, ThemePalette p)
    {
        if (Excluded.TryGetValue(strip, out _))
            return;
        if (!OriginalRenderMode.TryGetValue(strip, out var original))
            OriginalRenderMode.Add(strip, original = new Box<ToolStripRenderMode>(strip.RenderMode));
        if (p.IsDark)
            strip.Renderer = new DarkRenderer(p);
        else if (original.Value == ToolStripRenderMode.Custom)
            strip.Renderer = new ToolStripProfessionalRenderer();
        else
            strip.RenderMode = original.Value;
        strip.BackColor = p.IsDark ? p.Control : SystemColors.Control;
        strip.ForeColor = p.Text;
    }

    private static readonly ConditionalWeakTable<TabControl, TabPainter> TabPainters = new();

    /// <summary>
    /// Nel tema scuro le intestazioni si disegnano qui (il controllo nativo le farebbe chiare) e un piccolo
    /// sottoclassamento ripassa lo sfondo della striscia e il bordo intorno alle pagine.
    /// </summary>
    private static void ThemeTabs(TabControl tabs, ThemePalette p)
    {
        if (!TabPainters.TryGetValue(tabs, out _))
        {
            var painter = new TabPainter(tabs);
            TabPainters.Add(tabs, painter);
            tabs.DrawItem += DrawTabItem;
        }
        tabs.DrawMode = p.IsDark ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;
        // Disegnate a mano le linguette hanno un margine in più: con quello predefinito il testo si tronca.
        tabs.Padding = p.IsDark ? new Point(14, 4) : new Point(6, 3);
        tabs.Invalidate();
    }

    private static void DrawTabItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabCount)
            return;
        var p = Current;
        var page = tabs.TabPages[e.Index];
        bool selected = e.Index == tabs.SelectedIndex;
        using (var back = new SolidBrush(selected ? p.Input : p.Control))
            e.Graphics.FillRectangle(back, e.Bounds);
        int x = e.Bounds.X + 6;
        if (tabs.ImageList is { } images && page.ImageIndex >= 0 && page.ImageIndex < images.Images.Count)
        {
            var img = images.Images[page.ImageIndex];
            e.Graphics.DrawImage(img, x, e.Bounds.Y + (e.Bounds.Height - img.Height) / 2);
            x += img.Width + 4;
        }
        TextRenderer.DrawText(e.Graphics, page.Text, tabs.Font, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height),
            selected ? p.Text : p.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Dopo il disegno nativo ricolora ciò che resta chiaro: striscia accanto alle schede e bordo delle pagine.</summary>
    private sealed class TabPainter : NativeWindow
    {
        private const int WM_PAINT = 0x000F;
        private readonly TabControl _tabs;

        public TabPainter(TabControl tabs)
        {
            _tabs = tabs;
            if (tabs.IsHandleCreated)
                AssignHandle(tabs.Handle);
            tabs.HandleCreated += (_, _) => AssignHandle(tabs.Handle);
            tabs.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT || !Current.IsDark || _tabs.IsDisposed)
                return;
            using var g = Graphics.FromHwnd(_tabs.Handle);
            using var region = new Region(_tabs.ClientRectangle);
            region.Exclude(_tabs.DisplayRectangle);
            for (int i = 0; i < _tabs.TabCount; i++)
                region.Exclude(_tabs.GetTabRect(i));
            using var back = new SolidBrush(Current.Control);
            g.FillRegion(back, region);
        }
    }

    /// <summary>Colori di sfondo standard → quelli del tema; gli altri restano.</summary>
    private static Color MapBack(Color c, ThemePalette p)
    {
        if (Same(c, SystemColors.Control) || Same(c, Dark.Control))
            return p.Control;
        if (Same(c, SystemColors.Window) || Same(c, Dark.Window))
            return p.Window;
        return c;
    }

    private static Color MapFore(Color c, ThemePalette p)
    {
        if (Same(c, SystemColors.ControlText) || Same(c, SystemColors.WindowText) || Same(c, Dark.Text))
            return p.Text;
        if (Same(c, SystemColors.GrayText) || Same(c, Dark.GrayText))
            return p.GrayText;
        return c;
    }

    private static bool Same(Color a, Color b) => a.ToArgb() == b.ToArgb();

    // ---------- barra del titolo scura (Windows 10 2004 e successivi) ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    // ---------- menu e barre degli strumenti scure ----------

    private sealed class DarkRenderer(ThemePalette p) : ToolStripProfessionalRenderer(new DarkColors(p))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? p.Text : p.GrayText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = p.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using var back = new SolidBrush(p.Highlight);
            var r = new Rectangle(e.ImageRectangle.X - 1, e.ImageRectangle.Y - 1, e.ImageRectangle.Width + 2, e.ImageRectangle.Height + 2);
            e.Graphics.FillRectangle(back, r);
            using var pen = new Pen(p.HighlightText, 1.6f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new Point[] { new(r.Left + 3, r.Top + r.Height / 2), new(r.Left + r.Width / 2 - 1, r.Bottom - 4), new(r.Right - 3, r.Top + 3) });
        }
    }

    private sealed class DarkColors(ThemePalette p) : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => p.Control;
        public override Color ToolStripGradientMiddle => p.Control;
        public override Color ToolStripGradientEnd => p.Control;
        public override Color ToolStripBorder => p.Control;
        public override Color ToolStripDropDownBackground => p.Input;
        public override Color MenuStripGradientBegin => p.Control;
        public override Color MenuStripGradientEnd => p.Control;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => p.Highlight;
        public override Color MenuItemSelected => p.Highlight;
        public override Color MenuItemSelectedGradientBegin => p.Highlight;
        public override Color MenuItemSelectedGradientEnd => p.Highlight;
        public override Color MenuItemPressedGradientBegin => p.Input;
        public override Color MenuItemPressedGradientMiddle => p.Input;
        public override Color MenuItemPressedGradientEnd => p.Input;
        public override Color ImageMarginGradientBegin => p.Input;
        public override Color ImageMarginGradientMiddle => p.Input;
        public override Color ImageMarginGradientEnd => p.Input;
        public override Color ButtonSelectedHighlight => p.Highlight;
        public override Color ButtonSelectedGradientBegin => p.Highlight;
        public override Color ButtonSelectedGradientMiddle => p.Highlight;
        public override Color ButtonSelectedGradientEnd => p.Highlight;
        public override Color ButtonSelectedBorder => p.Highlight;
        public override Color ButtonPressedGradientBegin => p.Border;
        public override Color ButtonPressedGradientMiddle => p.Border;
        public override Color ButtonPressedGradientEnd => p.Border;
        public override Color ButtonCheckedGradientBegin => p.Border;
        public override Color ButtonCheckedGradientMiddle => p.Border;
        public override Color ButtonCheckedGradientEnd => p.Border;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Border;
        public override Color StatusStripGradientBegin => p.Control;
        public override Color StatusStripGradientEnd => p.Control;
        public override Color GripDark => p.Border;
        public override Color GripLight => p.Control;
        public override Color CheckBackground => p.Highlight;
        public override Color CheckSelectedBackground => p.Highlight;
        public override Color CheckPressedBackground => p.Highlight;
        public override Color OverflowButtonGradientBegin => p.Control;
        public override Color OverflowButtonGradientMiddle => p.Control;
        public override Color OverflowButtonGradientEnd => p.Control;
    }
}
