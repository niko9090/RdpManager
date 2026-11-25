using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Colori di un tema dell'interfaccia. Oltre ai colori di base (fondi, testo, bordo, selezione) ci sono quelli con
/// un significato: <see cref="Accent"/> per link e testi cliccabili, <see cref="Hover"/>/<see cref="Pressed"/> per i
/// menu e i pulsanti piatti, <see cref="Success"/>/<see cref="Warning"/>/<see cref="Danger"/> per i testi di stato
/// (tutti almeno 4,5:1 sul fondo dei controlli, WCAG AA) e i due riquadri d'avviso (fondo + testo).
/// </summary>
internal sealed record ThemePalette(
    bool IsDark,
    Color Window,
    Color Control,
    Color Input,
    Color Text,
    Color GrayText,
    Color Border,
    Color Highlight,
    Color HighlightText,
    Color Accent,
    Color Hover,
    Color Pressed,
    Color Success,
    Color Warning,
    Color Danger,
    Color GoodBack,
    Color GoodText,
    Color BadBack,
    Color BadText,
    Color Rail);

/// <summary>
/// Colori che non cambiano col tema: pallini di stato delle sessioni, icone dell'elenco e superfici sempre scure
/// (barra a schermo intero, Panoramica, schermata di blocco). Gli stessi valori stanno nel programma Linux
/// (RdpManager.Linux/ViewModels/UiColors.cs e App.axaml).
/// </summary>
internal static class UiColors
{
    // Stato delle sessioni e raggiungibilità (pallini: si leggono su fondo chiaro e scuro).
    public static readonly Color Connected = Color.FromArgb(46, 160, 67);
    public static readonly Color Connecting = Color.FromArgb(210, 150, 20);
    public static readonly Color Disconnected = Color.FromArgb(200, 60, 50);
    public static readonly Color Idle = Color.Gray;
    public static readonly Color Unreachable = Color.FromArgb(150, 150, 150);

    // Icone dell'elenco delle connessioni.
    public static readonly Color FolderFill = Color.FromArgb(240, 196, 80);
    public static readonly Color FolderEdge = Color.FromArgb(190, 140, 40);
    public static readonly Color Star = Color.FromArgb(245, 180, 30);
    public static readonly Color Clock = Color.FromArgb(90, 110, 140);
    public static readonly Color ComputerFrame = Color.FromArgb(60, 60, 70);
    public static readonly Color ComputerScreen = Color.FromArgb(70, 140, 220);

    // Superfici sempre scure, dalla più profonda alla più in rilievo.
    public static readonly Color ImmersiveThumb = Color.FromArgb(20, 22, 26);
    public static readonly Color ImmersiveBackdrop = Color.FromArgb(24, 25, 29);
    public static readonly Color ImmersiveSurface = Color.FromArgb(30, 32, 38);
    public static readonly Color ImmersiveSection = Color.FromArgb(36, 38, 45);
    public static readonly Color ImmersiveTile = Color.FromArgb(40, 42, 48);
    public static readonly Color ImmersiveGrip = Color.FromArgb(44, 47, 55);
    public static readonly Color ImmersiveControl = Color.FromArgb(48, 51, 60);
    public static readonly Color ImmersiveHover = Color.FromArgb(70, 74, 84);
    public static readonly Color ImmersiveLine = Color.FromArgb(70, 75, 90);
    /// <summary>Fondo dell'elemento scelto: lo stesso blu della selezione del tema scuro.</summary>
    public static readonly Color ImmersiveSelected = Color.FromArgb(45, 95, 165);
    public static readonly Color ImmersiveSelectedHover = Color.FromArgb(70, 110, 180);
    /// <summary>Accento su fondo scuro (bordi della scelta, maniglie): un solo azzurro per tutte le superfici scure.</summary>
    public static readonly Color ImmersiveAccent = Color.FromArgb(90, 150, 230);
    public static readonly Color ImmersiveAccentText = Color.FromArgb(170, 200, 245);
    public static readonly Color ImmersiveLink = Color.FromArgb(120, 170, 255);
    public static readonly Color ImmersiveText = Color.FromArgb(235, 235, 240);
    public static readonly Color ImmersiveTitle = Color.FromArgb(200, 210, 230);
    public static readonly Color ImmersiveHeader = Color.FromArgb(150, 160, 180);
    public static readonly Color ImmersiveMuted = Color.FromArgb(160, 162, 170);
    public static readonly Color ImmersiveDim = Color.FromArgb(170, 170, 175);
    public static readonly Color ImmersiveDisabled = Color.FromArgb(120, 125, 135);
    public static readonly Color ImmersiveDanger = Color.FromArgb(240, 120, 110);
    /// <summary>Bordo trascinabile illuminato (larghezza delle barre laterali).</summary>
    public static readonly Color GripHighlight = Color.FromArgb(70, 130, 210);
    /// <summary>Fondo dietro la sessione remota (prima che il desktop compaia).</summary>
    public static readonly Color SessionBackdrop = Color.FromArgb(32, 32, 32);
}

/// <summary>
/// Misure comuni dell'interfaccia (in pixel a 96 DPI: Windows Forms le scala con la finestra) e caratteri derivati
/// da quello di sistema, così titoli, note e spaziature hanno la stessa gerarchia in tutte le finestre.
/// </summary>
internal static class UiMetrics
{
    /// <summary>Margine intorno ai pulsanti in fondo ai dialoghi.</summary>
    public static Padding ButtonBar => new(12, 10, 12, 10);

    /// <summary>Margine dell'intestazione (titolo e sottotitolo) di una pagina.</summary>
    public static Padding PageHeader => new(16, 12, 16, 6);

    /// <summary>Spazio sotto un gruppo di opzioni.</summary>
    public const int SectionGap = 18;

    /// <summary>Rientro del contenuto di un gruppo rispetto al suo titolo.</summary>
    public const int GroupIndent = 14;

    /// <summary>Larghezza minima dei pulsanti principali dei dialoghi.</summary>
    public const int DialogButtonWidth = 96;

    /// <summary>Raggio degli angoli delle evidenziazioni (menu, pulsanti piatti, voci scelte).</summary>
    public const int CornerRadius = 4;

    public static Font BaseFont => SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

    /// <summary>Titolo di pagina: 1,5 volte il carattere di sistema, grassetto.</summary>
    public static Font Title(Font font) => new(font.FontFamily, font.Size * 1.5f, FontStyle.Bold);

    /// <summary>Testo in evidenza (messaggi a centro finestra): un punto in più del carattere di sistema.</summary>
    public static Font Lead(Font font) => new(font.FontFamily, font.Size + 1.5f);

    /// <summary>Titolo di un gruppo di opzioni.</summary>
    public static Font Heading(Font font) => new(font, FontStyle.Bold);

    /// <summary>Rettangolo con gli angoli arrotondati (per evidenziazioni disegnate a mano).</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Tema chiaro o scuro dell'interfaccia. I colori si applicano ai controlli che usano i colori standard;
/// quelli con colori propri (barra di passaggio, sessione remota, campioni di colore) restano come sono.
/// L'impostazione «Sistema» legge, in sola lettura, la preferenza di Windows «Modalità app».
/// </summary>
internal static class ThemeManager
{
    // Chiaro: i colori di sistema di Windows; l'accento è il blu di Windows 11 per i testi (5,5:1 sul grigio dei controlli).
    public static readonly ThemePalette Light = new(false,
        Window: SystemColors.Window, Control: SystemColors.Control, Input: SystemColors.Window,
        Text: SystemColors.ControlText, GrayText: SystemColors.GrayText, Border: SystemColors.ControlDark,
        Highlight: SystemColors.Highlight, HighlightText: SystemColors.HighlightText,
        Accent: Color.FromArgb(0, 95, 184), Hover: Color.FromArgb(217, 234, 248), Pressed: Color.FromArgb(196, 222, 245),
        Success: Color.FromArgb(15, 123, 15), Warning: Color.FromArgb(138, 90, 0), Danger: Color.FromArgb(196, 43, 28),
        GoodBack: Color.FromArgb(214, 240, 220), GoodText: Color.FromArgb(16, 90, 36),
        BadBack: Color.FromArgb(248, 218, 214), BadText: Color.FromArgb(138, 28, 20),
        Rail: Color.FromArgb(200, 205, 215));

    public static readonly ThemePalette Dark = new(true,
        Window: Color.FromArgb(32, 33, 36), Control: Color.FromArgb(43, 45, 49), Input: Color.FromArgb(52, 54, 59),
        Text: Color.FromArgb(232, 232, 235), GrayText: Color.FromArgb(150, 152, 160), Border: Color.FromArgb(70, 72, 80),
        Highlight: Color.FromArgb(45, 95, 165), HighlightText: Color.White,
        Accent: Color.FromArgb(120, 170, 255), Hover: Color.FromArgb(66, 70, 78), Pressed: Color.FromArgb(80, 85, 96),
        Success: Color.FromArgb(110, 210, 120), Warning: Color.FromArgb(235, 190, 90), Danger: Color.FromArgb(240, 120, 110),
        GoodBack: Color.FromArgb(31, 58, 38), GoodText: Color.FromArgb(166, 227, 180),
        BadBack: Color.FromArgb(68, 32, 29), BadText: Color.FromArgb(245, 185, 177),
        Rail: Color.FromArgb(70, 72, 80));

    /// <summary>Passaggio del mouse sul pulsante principale scuro (blu un po' più chiaro della selezione).</summary>
    private static readonly Color DarkAccentHover = Color.FromArgb(58, 112, 190);

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
        ToolStripManager.Renderer = NewRenderer(Current);
    }

    /// <summary>
    /// Menu e barre piatti, con l'evidenziazione arrotondata (come i menu di Windows 11). Con il contrasto elevato
    /// di Windows restano quelli di serie, che usano i colori di sistema scelti dall'utente.
    /// </summary>
    private static ToolStripRenderer NewRenderer(ThemePalette p) =>
        !p.IsDark && SystemInformation.HighContrast ? new ToolStripProfessionalRenderer() : new FlatRenderer(p);

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
            if (form.AcceptButton is Button primary && !Excluded.TryGetValue(primary, out _))
                StylePrimary(primary, p);
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
                if (c is ListView or ListBox or TextBox)
                    UseExplorerLook(c); // caselle di testo: bordo e barre di scorrimento scuri nel tema scuro
                break;
            case TreeView tree:
                tree.BackColor = p.Window;
                tree.ForeColor = p.Text;
                tree.LineColor = p.GrayText;
                UseExplorerLook(tree);
                break;
            case Button button:
                if (p.IsDark)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = p.Border;
                    button.FlatAppearance.MouseOverBackColor = p.Hover;
                    button.FlatAppearance.MouseDownBackColor = p.Pressed;
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
                link.LinkColor = p.Accent;
                link.ActiveLinkColor = p.IsDark ? Color.FromArgb(170, 200, 255) : Color.FromArgb(0, 62, 146);
                link.BackColor = MapBack(link.BackColor, p);
                link.ForeColor = MapFore(link.ForeColor, p);
                break;
            default:
                c.BackColor = MapBack(c.BackColor, p);
                c.ForeColor = MapFore(c.ForeColor, p);
                if (c is ScrollableControl { AutoScroll: true } and not Form)
                    UseExplorerLook(c); // barre di scorrimento scure nel tema scuro
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
        if (p.IsDark || original.Value == ToolStripRenderMode.Custom)
            strip.Renderer = NewRenderer(p);
        else
            strip.RenderMode = original.Value; // ManagerRenderMode usa il renderer piatto di SetTheme
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
        bool wide = TouchUi.HasWideTabs(tabs); // modalità tocco: sempre disegnate a mano, con la X
        tabs.DrawMode = p.IsDark || wide ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;
        // Disegnate a mano le linguette hanno un margine in più: con quello predefinito il testo si tronca.
        if (!wide)
            tabs.Padding = p.IsDark ? new Point(14, 4) : new Point(6, 3);
        TouchUi.TouchifyTabs(tabs, TouchUi.MinTarget(tabs)); // linguette da dito
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
        var textArea = new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height);
        if (TouchUi.HasWideTabs(tabs))
        {
            // Linguetta da dito: X grande e lontana dal bordo; un filo separa una scheda dall'altra.
            textArea = TabCloseLayout.TextArea(e.Bounds, tabs.DeviceDpi, x - e.Bounds.X);
            var close = TabCloseLayout.CloseRect(e.Bounds, tabs.DeviceDpi);
            int arm = Math.Max(4, close.Width / 5);
            var c = new Point(close.X + close.Width / 2, close.Y + close.Height / 2);
            using var pen = new Pen(selected ? p.Text : p.GrayText, Math.Max(2f, close.Width / 14f));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLine(pen, c.X - arm, c.Y - arm, c.X + arm, c.Y + arm);
            e.Graphics.DrawLine(pen, c.X - arm, c.Y + arm, c.X + arm, c.Y - arm);
            using var edge = new Pen(p.Border);
            e.Graphics.DrawRectangle(edge, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, page.Text, tabs.Font, textArea,
            selected ? p.Text : p.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Rifà il disegno delle linguette col tema in uso (dopo che la modalità tocco le ha cambiate).</summary>
    internal static void RefreshTabs(TabControl tabs) => ThemeTabs(tabs, Current);

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
        if (Same(c, Light.Danger) || Same(c, Dark.Danger))
            return p.Danger;
        return c;
    }

    /// <summary>
    /// Pulsante principale del dialogo (quello di Invio): nel tema scuro pieno col colore della selezione, così si
    /// riconosce subito; nel tema chiaro resta quello di Windows, che lo segna già col bordo blu.
    /// </summary>
    private static void StylePrimary(Button button, ThemePalette p)
    {
        if (!p.IsDark)
            return;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = p.Highlight;
        button.ForeColor = p.HighlightText;
        button.FlatAppearance.BorderColor = p.Highlight;
        button.FlatAppearance.MouseOverBackColor = DarkAccentHover;
        button.FlatAppearance.MouseDownBackColor = p.Border;
    }

    private static readonly ConditionalWeakTable<Control, object> ExplorerLook = new();

    /// <summary>
    /// Elenchi e alberi con lo stile di Esplora file: riga evidenziata al passaggio del mouse e, nel tema scuro,
    /// barre di scorrimento e frecce scure. Solo aspetto: il comportamento del controllo non cambia.
    /// </summary>
    private static void UseExplorerLook(Control c)
    {
        if (c.IsHandleCreated)
            SetExplorerTheme(c);
        if (ExplorerLook.TryGetValue(c, out _))
            return;
        ExplorerLook.Add(c, true);
        c.HandleCreated += (_, _) => SetExplorerTheme(c);
    }

    private static void SetExplorerTheme(Control c)
    {
        // Le caselle di testo hanno il loro stile scuro (quello delle finestre «Apri file»), gli elenchi quello di Esplora file.
        string dark = c is TextBox ? "DarkMode_CFD" : "DarkMode_Explorer";
        try { NativeMethods.SetWindowTheme(c.Handle, Current.IsDark ? dark : "Explorer", null); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
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

    // ---------- menu e barre degli strumenti (piatti, nei due temi) ----------

    /// <summary>
    /// Menu e barre piatti: niente sfumature, evidenziazione arrotondata un po' rientrata dai bordi (come i menu di
    /// Windows 11), voce scelta col colore <see cref="ThemePalette.Hover"/>, testo disattivato grigio, link col
    /// colore d'accento e una linea sottile sopra la barra di stato che la separa dal contenuto.
    /// </summary>
    private sealed class FlatRenderer : ToolStripProfessionalRenderer
    {
        private readonly ThemePalette _p;

        public FlatRenderer(ThemePalette p) : base(new FlatColors(p))
        {
            _p = p;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
                e.TextColor = _p.GrayText;
            else if (e.Item is ToolStripLabel { IsLink: true })
                e.TextColor = _p.Accent;
            else if (_p.IsDark)
                e.TextColor = _p.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? _p.GrayText : _p.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            bool open = item is ToolStripMenuItem { IsOnDropDown: false } menu && menu.Pressed;
            if (!(item.Selected && item.Enabled) && !open)
                return;
            var r = new Rectangle(Point.Empty, item.Size);
            r = item.IsOnDropDown ? Rectangle.Inflate(r, -4, -1) : Rectangle.Inflate(r, -1, -2);
            Fill(e.Graphics, r, open ? _p.Pressed : _p.Hover);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            bool isChecked = item is ToolStripButton { Checked: true };
            if (!item.Enabled || (!item.Selected && !item.Pressed && !isChecked))
                return;
            var r = Rectangle.Inflate(new Rectangle(Point.Empty, item.Size), -1, -1);
            Fill(e.Graphics, r, item.Pressed || isChecked ? _p.Pressed : _p.Hover);
            if (isChecked)
            {
                using var pen = new Pen(_p.IsDark ? _p.Accent : _p.Highlight);
                using var path = UiMetrics.RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), UiMetrics.CornerRadius);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Enabled || (!item.Selected && !item.Pressed))
                return;
            Fill(e.Graphics, Rectangle.Inflate(new Rectangle(Point.Empty, item.Size), -1, -1), item.Pressed ? _p.Pressed : _p.Hover);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            switch (e.ToolStrip)
            {
                case ToolStripDropDown:
                    base.OnRenderToolStripBorder(e); // bordo sottile intorno ai menu a discesa
                    break;
                case StatusStrip status:
                    using (var pen = new Pen(_p.Border))
                        e.Graphics.DrawLine(pen, 0, 0, status.Width, 0);
                    break;
                // Barre e menu principali: piatti, senza la linea in fondo.
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new Rectangle(e.ImageRectangle.X - 1, e.ImageRectangle.Y - 1, e.ImageRectangle.Width + 2, e.ImageRectangle.Height + 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(_p.Highlight))
            using (var path = UiMetrics.RoundedRect(r, UiMetrics.CornerRadius - 1))
                e.Graphics.FillPath(back, path);
            using var pen = new Pen(_p.HighlightText, 1.6f);
            e.Graphics.DrawLines(pen, new Point[] { new(r.Left + 3, r.Top + r.Height / 2), new(r.Left + r.Width / 2 - 1, r.Bottom - 4), new(r.Right - 3, r.Top + 3) });
        }

        private static void Fill(Graphics g, Rectangle r, Color color)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var path = UiMetrics.RoundedRect(r, UiMetrics.CornerRadius);
            g.FillPath(brush, path);
            g.SmoothingMode = old;
        }
    }

    /// <summary>Colori delle barre: tutti piatti (inizio = fine delle sfumature), dal tema in uso.</summary>
    private sealed class FlatColors(ThemePalette p) : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => p.Control;
        public override Color ToolStripGradientMiddle => p.Control;
        public override Color ToolStripGradientEnd => p.Control;
        public override Color ToolStripBorder => p.Control;
        public override Color ToolStripDropDownBackground => p.Input;
        public override Color MenuStripGradientBegin => p.Control;
        public override Color MenuStripGradientEnd => p.Control;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => p.Hover;
        public override Color MenuItemSelected => p.Hover;
        public override Color MenuItemSelectedGradientBegin => p.Hover;
        public override Color MenuItemSelectedGradientEnd => p.Hover;
        public override Color MenuItemPressedGradientBegin => p.Pressed;
        public override Color MenuItemPressedGradientMiddle => p.Pressed;
        public override Color MenuItemPressedGradientEnd => p.Pressed;
        public override Color ImageMarginGradientBegin => p.Input;
        public override Color ImageMarginGradientMiddle => p.Input;
        public override Color ImageMarginGradientEnd => p.Input;
        public override Color ButtonSelectedHighlight => p.Hover;
        public override Color ButtonSelectedHighlightBorder => p.Hover;
        public override Color ButtonSelectedGradientBegin => p.Hover;
        public override Color ButtonSelectedGradientMiddle => p.Hover;
        public override Color ButtonSelectedGradientEnd => p.Hover;
        public override Color ButtonSelectedBorder => p.Hover;
        public override Color ButtonPressedHighlight => p.Pressed;
        public override Color ButtonPressedHighlightBorder => p.Pressed;
        public override Color ButtonPressedGradientBegin => p.Pressed;
        public override Color ButtonPressedGradientMiddle => p.Pressed;
        public override Color ButtonPressedGradientEnd => p.Pressed;
        public override Color ButtonPressedBorder => p.Pressed;
        public override Color ButtonCheckedHighlight => p.Pressed;
        public override Color ButtonCheckedHighlightBorder => p.Pressed;
        public override Color ButtonCheckedGradientBegin => p.Pressed;
        public override Color ButtonCheckedGradientMiddle => p.Pressed;
        public override Color ButtonCheckedGradientEnd => p.Pressed;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.IsDark ? p.Border : p.Input;
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
        public override Color RaftingContainerGradientBegin => p.Control;
        public override Color RaftingContainerGradientEnd => p.Control;
    }
}
