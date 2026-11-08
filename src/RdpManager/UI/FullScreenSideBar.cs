using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Barra a schermo intero (dalla 1.5): a sinistra, a destra o in basso, mai in alto. Mostra le sezioni scelte
/// nelle impostazioni, nell'ordine scelto: sessioni connesse (con miniatura o compatte), preferiti, tutte le
/// connessioni con ricerca, spazi di lavoro, pulsanti rapidi (le stesse azioni delle scorciatoie), testi rapidi.
/// </summary>
internal sealed class FullScreenSideBar : Panel
{
    public sealed record SessionItem(RdpSessionView View, int? Number, bool Selected);

    /// <summary>Cosa mostrare; lo prepara la finestra principale.</summary>
    public sealed record Content(
        FullScreenBarSettings Settings,
        IReadOnlyList<SessionItem> Sessions,
        IReadOnlyList<RdpConnection> Favorites,
        IReadOnlyList<RdpConnection> Connections,
        IReadOnlyList<Workspace> Workspaces,
        IReadOnlyList<QuickText> QuickTexts,
        ShortcutMap Shortcuts,
        bool CanTypeText,
        bool MultipleMonitors);

    private static readonly Color Back = Color.FromArgb(30, 32, 38);
    private static readonly Color SectionBack = Color.FromArgb(36, 38, 45);
    private static readonly Color ButtonBack = Color.FromArgb(48, 51, 60);
    private static readonly Color SelectedBack = Color.FromArgb(40, 90, 160);
    private static readonly Color HeaderFore = Color.FromArgb(150, 160, 180);
    private const int SectionWidthBottom = 230;
    private static readonly Font InitialsFont = new(Control.DefaultFont.FontFamily, 16f, FontStyle.Bold);
    private static readonly Font HeaderFont = new((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 10.5f, FontStyle.Bold);
    private static readonly Font ItemFont = new((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 10.5f);
    private static readonly Font SelectedFont = new((SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily, 10.5f, FontStyle.Bold);
    private static readonly Font GlyphFont = new("Segoe UI Symbol", 13f);
    private static readonly Color Accent = Color.FromArgb(90, 150, 230);
    private const string HideGlyph = "✕", FavoriteGlyph = "★", WorkspaceGlyph = "▤", TextGlyph = "✎";

    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Back };
    private readonly ToolTip _tips = new();
    private readonly ResizeGrip _grip = new() { BackColor = Color.FromArgb(44, 47, 55) };
    private readonly Dictionary<RdpSessionView, Bitmap> _thumbnails = [];
    private FlowLayoutPanel? _sessionsBody;
    private ListBox? _connectionsList;
    private Content? _content;
    private readonly Panel _header = new() { Dock = DockStyle.Top, BackColor = Back, Visible = false, Padding = new Padding(6, 6, 6, 0) };
    private readonly BarButton _close = new("Chiudi il menu", ButtonBack) { Dock = DockStyle.Fill, Glyph = HideGlyph };
    private PanScroller? _pan;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _panHooked = new();
    private int _panLastY;
    private Control? _panTarget;

    public FullScreenSideBar()
    {
        BackColor = Back;
        _header.Controls.Add(_close);
        _close.Suppress = () => _pan?.Active == true;
        _close.Click += (_, _) => SessionActionClicked?.Invoke(this, SessionBarAction.HideBar);
        _tips.SetToolTip(_close, "Chiude il menu (si riapre dalla linguetta sul bordo)");
        _close.Margin = Padding.Empty;
        HookPan(_header);
        // Prima il riempimento, poi l'intestazione, poi il bordo: WinForms aggancia per primo l'ultimo aggiunto.
        Controls.Add(_flow);
        Controls.Add(_header);
        Controls.Add(_grip);
        _tips.SetToolTip(_grip, "Trascina per cambiare la misura della barra");
        _grip.Drag += (_, e) => ResizeDrag?.Invoke(this, e);
        _flow.Resize += (_, _) => FitSectionWidths();
        DarkScrollBars(_flow);
    }

    /// <summary>Barre di scorrimento scure come la barra (dove Windows lo permette).</summary>
    private static void DarkScrollBars(Control c)
    {
        void Apply() => NativeMethods.SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        if (c.IsHandleCreated)
            Apply();
        else
            c.HandleCreated += (_, _) => Apply();
    }

    public event EventHandler<RdpSessionView>? SessionClicked;
    public event EventHandler<RdpConnection>? ConnectionClicked;
    public event EventHandler<Workspace>? WorkspaceClicked;
    public event EventHandler<ShortcutAction>? ActionClicked;
    public event EventHandler<QuickText>? QuickTextClicked;
    public event EventHandler<SessionBarAction>? SessionActionClicked;

    /// <summary>Si trascina il bordo verso la sessione: la finestra calcola la nuova misura (dalla 1.5.1).</summary>
    public event EventHandler<GripDragEventArgs>? ResizeDrag;

    /// <summary>Bordo da trascinare per cambiare la misura.</summary>
    internal ResizeGrip Grip => _grip;

    /// <summary>Vero mentre si trascina il bordo.</summary>
    public bool IsResizing => _grip.IsDragging;

    /// <summary>Casella di ricerca della sezione «Tutte le connessioni» (null se la sezione non c'è).</summary>
    internal TextBox? SearchBox { get; private set; }

    /// <summary>Vero se si sta scrivendo nella ricerca: la barra a comparsa non si richiude.</summary>
    public bool IsSearching => SearchBox is { Focused: true, TextLength: > 0 };

    /// <summary>Sezioni mostrate, nell'ordine (per le prove automatiche).</summary>
    internal IReadOnlyList<BarSection> ShownSections { get; private set; } = [];

    private bool Vertical => _content?.Settings.Position != BarPosition.Bottom;

    /// <summary>Ricostruisce tutte le sezioni.</summary>
    public void Rebuild(Content content)
    {
        _content = content;
        string search = SearchBox?.Text ?? "";
        SuspendLayout();
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls.Cast<Control>().ToList())
            c.Dispose();
        _flow.Controls.Clear();
        _sessionsBody = null;
        _connectionsList = null;
        SearchBox = null;
        _flow.FlowDirection = Vertical ? FlowDirection.TopDown : FlowDirection.LeftToRight;
        // Il bordo da trascinare sta verso la sessione: a destra della barra sinistra, a sinistra della destra, sopra quella in basso.
        _grip.Edge = content.Settings.Position switch
        {
            BarPosition.Right => DockStyle.Left,
            BarPosition.Bottom => DockStyle.Top,
            _ => DockStyle.Right,
        };
        _flow.Padding = new Padding(6);
        _header.Visible = Vertical;
        var shown = new List<BarSection>();
        foreach (var section in content.Settings.VisibleSections)
        {
            var body = BuildSection(section, content, search);
            if (body is null)
                continue;
            shown.Add(section);
            _flow.Controls.Add(Section(FullScreenBarSettings.Title(section), body));
        }
        if (shown.Count == 0)
            _flow.Controls.Add(Note("Nessuna sezione scelta: Impostazioni › Schermo intero."));
        ShownSections = shown;
        FitSectionWidths();
        HookPan(_flow);
        _flow.ResumeLayout();
        ResumeLayout();
    }

    /// <summary>Aggiorna solo le sessioni (stato, numero, scheda scelta), senza toccare la ricerca.</summary>
    public void UpdateSessions(IReadOnlyList<SessionItem> sessions, bool canTypeText)
    {
        if (_content is null)
            return;
        _content = _content with { Sessions = sessions, CanTypeText = canTypeText };
        foreach (var b in Descendants(_flow).OfType<Button>().Where(b => b.Tag is QuickText))
            b.Enabled = canTypeText;
        foreach (var b in Descendants(_flow).OfType<ActionButton>().Where(b => b.Tag is SessionBarAction))
            b.Enabled = SessionActionEnabled((SessionBarAction)b.Tag!, _content);
        foreach (var view in _thumbnails.Keys.Where(v => v.IsDisposed || sessions.All(s => s.View != v)).ToList())
        {
            _thumbnails[view].Dispose();
            _thumbnails.Remove(view);
        }
        if (_sessionsBody is not { IsDisposed: false } body)
            return;
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList())
            c.Dispose();
        body.Controls.Clear();
        FillSessions(body, _content);
        body.ResumeLayout();
        FitSectionWidths();
        HookPan(body);
    }

    /// <summary>Nuova miniatura della sessione (la barra se ne occupa e la libera).</summary>
    public void UpdateThumbnail(RdpSessionView view, Bitmap? bitmap)
    {
        if (bitmap is null)
            return;
        if (_thumbnails.Remove(view, out var old))
            old.Dispose();
        _thumbnails[view] = bitmap;
        if (_sessionsBody is null)
            return;
        foreach (var pic in Descendants(_sessionsBody).OfType<PictureBox>().Where(p => p.Tag == view))
            pic.Image = bitmap;
    }

    private Control? BuildSection(BarSection section, Content content, string search)
    {
        switch (section)
        {
            case BarSection.ConnectedSessions:
                var sessions = Body();
                _sessionsBody = sessions;
                FillSessions(sessions, content);
                return sessions;
            case BarSection.Favorites:
                if (content.Favorites.Count == 0)
                    return null;
                var fav = Body();
                foreach (var c in content.Favorites)
                    fav.Controls.Add(ConnectionButton(c));
                return fav;
            case BarSection.AllConnections:
                return BuildConnections(content, search);
            case BarSection.Workspaces:
                if (content.Workspaces.Count == 0)
                    return null;
                var ws = Body();
                foreach (var w in content.Workspaces)
                {
                    var workspace = w;
                    var b = MakeButton(w.Name, ButtonBack, WorkspaceGlyph);
                    _tips.SetToolTip(b, w.Shortcut.Length > 0 ? $"Apri lo spazio di lavoro ({w.Shortcut})" : "Apri lo spazio di lavoro");
                    b.Click += (_, _) => WorkspaceClicked?.Invoke(this, workspace);
                    ws.Controls.Add(b);
                }
                return ws;
            case BarSection.QuickButtons:
                var actions = Body();
                foreach (var a in FullScreenBarLayout.QuickActions)
                {
                    var action = a;
                    var combo = content.Shortcuts[a];
                    var b = new ActionButton(ShortcutMap.Describe(a), combo.IsEmpty ? "" : combo.ToString()) { Tag = action, Glyph = GlyphFor(a) };
                    _tips.SetToolTip(b, ShortcutMap.Describe(a) + (combo.IsEmpty ? " (senza scorciatoia)" : $" ({combo})"));
                    b.Suppress = () => PanActive;
                    b.Click += (_, _) => ActionClicked?.Invoke(this, action);
                    actions.Controls.Add(b);
                }
                return actions;
            case BarSection.SessionActions:
                var sessionActions = Body();
                foreach (var a in FullScreenBarLayout.SessionActions)
                {
                    var action = a;
                    var b = new ActionButton(FullScreenBarSettings.Describe(a), "") { Tag = action, Glyph = GlyphFor(a) };
                    b.Enabled = SessionActionEnabled(a, content);
                    _tips.SetToolTip(b, a switch
                    {
                        SessionBarAction.SendCtrlAltDel => "Invia Ctrl+Alt+Canc alla sessione visibile",
                        SessionBarAction.Snapshot => "Foto della sessione visibile (Immagini\\RdpManager e appunti)",
                        SessionBarAction.ScreenKeyboard => "Apre la tastiera su schermo di Windows: i tasti vanno alla sessione visibile",
                        SessionBarAction.SendEscape => "Invia il tasto Esc alla sessione visibile",
                        SessionBarAction.SendWindows => "Invia il tasto Windows alla sessione visibile",
                        SessionBarAction.RightClick => "Il prossimo tocco sulla sessione vale come clic destro",
                        SessionBarAction.TouchMode => "Passa dal tocco al PC remoto al dito che fa da mouse (e viceversa)",
                        SessionBarAction.HideBar => "Nasconde la barra (si richiama dalla linguetta sul bordo)",
                        _ => content.MultipleMonitors ? "Schermo intero sul monitor successivo" : "C'è un solo monitor",
                    });
                    b.Suppress = () => PanActive;
                    b.Click += (_, _) => SessionActionClicked?.Invoke(this, action);
                    sessionActions.Controls.Add(b);
                }
                return sessionActions;
            case BarSection.QuickTexts:
                if (content.QuickTexts.Count == 0)
                    return null;
                var texts = Body();
                foreach (var t in content.QuickTexts)
                {
                    var text = t;
                    var b = MakeButton(t.DisplayName, ButtonBack, TextGlyph);
                    b.Tag = text;
                    b.Enabled = content.CanTypeText;
                    _tips.SetToolTip(b, QuickText.Preview(t.Text, 120));
                    b.Click += (_, _) => QuickTextClicked?.Invoke(this, text);
                    texts.Controls.Add(b);
                }
                return texts;
            default:
                return null;
        }
    }

    private Control BuildConnections(Content content, string search)
    {
        var body = Body();
        var box = new TextBox
        {
            PlaceholderText = "Cerca…", Text = search, BackColor = ButtonBack, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 4),
        };
        var list = new ListBox
        {
            BackColor = SectionBack, ForeColor = Color.White, BorderStyle = BorderStyle.None, IntegralHeight = false,
            Height = TouchUi.Px(this, Vertical ? 240 : 120), DisplayMember = nameof(RdpConnection.DisplayName), Margin = Padding.Empty,
        };
        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var c in FullScreenBarLayout.FilterConnections(box.Text, content.Connections))
                list.Items.Add(c);
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
            list.EndUpdate();
        }
        void OpenSelected()
        {
            if (list.SelectedItem is RdpConnection c)
                ConnectionClicked?.Invoke(this, c);
        }
        DarkScrollBars(list);
        box.TextChanged += (_, _) => Fill();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                OpenSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode is Keys.Down or Keys.Up && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1), 0, list.Items.Count - 1);
                e.Handled = true;
            }
        };
        list.DoubleClick += (_, _) => OpenSelected();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) OpenSelected(); };
        _tips.SetToolTip(list, "Doppio clic o Invio per connettersi");
        Fill();
        body.Controls.Add(box);
        body.Controls.Add(list);
        SearchBox = box;
        _connectionsList = list;
        return body;
    }

    private void FillSessions(FlowLayoutPanel body, Content content)
    {
        if (content.Sessions.Count == 0)
        {
            body.Controls.Add(Note("Nessuna sessione aperta"));
            return;
        }
        bool thumbnails = content.Settings.SessionStyle == SessionListStyle.Thumbnails;
        foreach (var item in content.Sessions)
        {
            var view = item.View;
            var c = view.Connection;
            var label = (item.Number is int n ? $"{n}  " : "") + c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
            var b = MakeButton(label, item.Selected ? SelectedBack : ButtonBack);
            b.Tag = view;
            b.Padding = new Padding(TouchUi.Px(this, 18), 0, TouchUi.Px(this, 10), 0);
            bool selected = item.Selected;
            if (selected)
                b.Font = SelectedFont;
            var stripe = ConnectionDialog.ToColor(c.Color);
            var state = RdpSessionView.StateColor(view.State);
            b.Paint += (_, e) =>
            {
                if (stripe is { } s)
                {
                    using var brush = new SolidBrush(s);
                    e.Graphics.FillRectangle(brush, 0, 0, 4, b.Height);
                }
                using var dot = new SolidBrush(state);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(dot, 6, b.Height / 2 - 3, 7, 7);
                if (selected)
                {
                    // Sessione visibile: barra chiara lungo il bordo destro.
                    using var mark = new SolidBrush(Accent);
                    e.Graphics.FillRectangle(mark, b.Width - 6, 0, 6, b.Height);
                }
            };
            if (view.State != SessionState.Connected)
                b.ForeColor = Color.FromArgb(170, 170, 175);
            _tips.SetToolTip(b, $"{RdpFile.FormatAddress(c.Host, c.Port)} — {RdpSessionView.StateWord(view.State)}" +
                                (item.Number is int k ? $"\nCtrl+Alt+{k}" : ""));
            b.Click += (_, _) => SessionClicked?.Invoke(this, view);
            if (!thumbnails)
            {
                body.Controls.Add(b);
                continue;
            }
            var pic = new PictureBox
            {
                Tag = view, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 22, 26), Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 0), Image = _thumbnails.GetValueOrDefault(view),
            };
            pic.Paint += (_, e) =>
            {
                if (pic.Image is not null)
                    return;
                // Ancora nessuna miniatura: il colore e le iniziali della connessione.
                using var brush = new SolidBrush(stripe ?? Color.FromArgb(70, 75, 90));
                e.Graphics.FillRectangle(brush, 0, 0, pic.Width, 4);
                var initials = string.Concat(c.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
                TextRenderer.DrawText(e.Graphics, initials, InitialsFont, pic.ClientRectangle,
                    Color.FromArgb(120, 125, 140), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            pic.Click += (_, _) => SessionClicked?.Invoke(this, view);
            var card = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 6),
                BackColor = item.Selected ? SelectedBack : SectionBack, Padding = new Padding(2),
            };
            card.Controls.Add(pic);
            card.Controls.Add(b);
            body.Controls.Add(card);
        }
    }

    private Button ConnectionButton(RdpConnection c)
    {
        var b = MakeButton(c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : ""), ButtonBack, FavoriteGlyph);
        b.Tag = c;
        var stripe = ConnectionDialog.ToColor(c.Color);
        if (stripe is { } s)
            b.Paint += (_, e) =>
            {
                using var brush = new SolidBrush(s);
                e.Graphics.FillRectangle(brush, 0, 0, 4, b.Height);
            };
        _tips.SetToolTip(b, RdpFile.FormatAddress(c.Host, c.Port));
        b.Click += (_, _) => ConnectionClicked?.Invoke(this, c);
        return b;
    }

    /// <summary>Larghezza di sezioni e pulsanti: tutta la barra (a lato) o una colonna fissa (in basso).</summary>
    private void FitSectionWidths()
    {
        if (_content is null)
            return;
        bool vertical = Vertical;
        // A lato si tiene sempre il posto della barra di scorrimento verticale: così non serve mai quella orizzontale.
        int sectionWidth = vertical
            ? Math.Max(80, _flow.Width - _flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth)
            : SectionWidthBottom;
        bool wide = !vertical && _flow.Controls.Count * (SectionWidthBottom + 6) + _flow.Padding.Horizontal > _flow.Width;
        int sectionHeight = Math.Max(60, _flow.Height - _flow.Padding.Vertical - (wide ? SystemInformation.HorizontalScrollBarHeight : 0));
        foreach (Control section in _flow.Controls)
        {
            section.Width = sectionWidth;
            section.Margin = vertical ? new Padding(0, 0, 0, 6) : new Padding(0, 0, 6, 0);
            if (section is FlowLayoutPanel sp && !vertical)
            {
                sp.AutoSize = false;
                sp.Height = sectionHeight;
                sp.AutoScroll = true;
                DarkScrollBars(sp);
            }
            int inner = sectionWidth - section.Padding.Horizontal - (vertical ? 0 : SystemInformation.VerticalScrollBarWidth);
            if (!vertical && _connectionsList is { } list && list.Parent?.Parent == section)
                list.Height = Math.Max(40, sectionHeight - section.Padding.Vertical - 60);
            foreach (var c in Descendants(section))
            {
                if (c is Button or TextBox or ListBox || c.Parent is FlowLayoutPanel { Tag: "body" } && c is Label)
                    c.Width = inner;
                if (c is FlowLayoutPanel { Tag: "body" } body)
                    body.Width = inner;
                if (c is PictureBox pic)
                {
                    pic.Width = inner - 4;
                    pic.Height = pic.Width * 9 / 16;
                }
            }
        }
        // Pulsanti, caselle ed elenco alti almeno quanto un bersaglio da dito (48 px logici), ben distanziati.
        int touch = TouchUi.Px(this, TouchMetrics.SessionTarget);
        int gap = TouchUi.Px(this, 6);
        foreach (var action in Descendants(_flow).OfType<ActionButton>())
        {
            action.MinTouchHeight = touch;
            action.Height = Math.Max(action.Height, action.CurrentLayout().Height);
        }
        foreach (var b in Descendants(_flow).OfType<BarButton>())
        {
            b.Margin = new Padding(0, 0, 0, gap);
            b.ApplyGlyphPadding();
        }
        foreach (var card in Descendants(_flow).OfType<FlowLayoutPanel>().Where(f => f.Tag is null && f.Parent is FlowLayoutPanel { Tag: "body" }))
            card.Margin = new Padding(0, 0, 0, gap);
        _close.MinimumSize = new Size(0, touch);
        _header.Height = touch + _header.Padding.Vertical + gap;
        _close.ApplyGlyphPadding();
        TouchUi.Apply(_flow, touch);
    }

    // ---------- scorrimento col dito ----------

    /// <summary>Si aggancia (una volta sola per controllo) ai tocchi di tutti i controlli sotto <paramref name="root"/>, liste escluse le caselle di testo.</summary>
    private void HookPan(Control root)
    {
        foreach (var c in new[] { root }.Concat(Descendants(root)))
        {
            if (c is TextBox || _panHooked.TryGetValue(c, out _))
                continue;
            _panHooked.Add(c, new object());
            c.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) PanBegin((Control)s!, Cursor.Position.Y); };
            c.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) PanMove(Cursor.Position.Y); };
            c.MouseUp += (_, _) => PanEnd();
        }
    }

    /// <summary>Elenco o pannello che scorre quando il dito si trascina su <paramref name="from"/> (null se niente scorre).</summary>
    private Control? PanTargetFor(Control from)
    {
        if (from is ListBox)
            return from;
        for (Control? c = from; c is not null && c != this; c = c.Parent)
            if (c is ScrollableControl { AutoScroll: true } s && s.VerticalScroll.Visible)
                return s;
        return null;
    }

    internal void PanBegin(Control from, int screenY)
    {
        _panTarget = PanTargetFor(from);
        _panLastY = screenY;
        int row = _panTarget is ListBox list ? Math.Max(1, list.ItemHeight) : 1;
        _pan = new PanScroller(row, TouchUi.Px(this, 8));
        _pan.Begin(screenY);
    }

    internal void PanMove(int screenY)
    {
        if (_pan is null)
            return;
        int steps = _pan.Move(screenY);
        if (steps == 0 || _panTarget is null)
            return;
        switch (_panTarget)
        {
            case ListBox list when list.Items.Count > 0:
                list.TopIndex = Math.Clamp(list.TopIndex + steps, 0, list.Items.Count - 1);
                break;
            case ScrollableControl s:
                var pos = s.AutoScrollPosition;
                s.AutoScrollPosition = new Point(-pos.X, -pos.Y + steps);
                break;
        }
    }

    internal void PanEnd() => _pan?.End();

    /// <summary>Vero se l'ultimo trascinamento ha scorso: il clic che segue va ignorato.</summary>
    internal bool PanActive => _pan?.Active == true;

    internal Button CloseButton => _close;

    private static string GlyphFor(ShortcutAction a) => a switch
    {
        ShortcutAction.NextSession => "⇥",
        ShortcutAction.PreviousSession => "⇤",
        ShortcutAction.ToggleFullScreen => "⛶",
        ShortcutAction.ToggleSidebar => "☰",
        ShortcutAction.Overview => "▦",
        ShortcutAction.QuickSearch => "⌕",
        ShortcutAction.ToggleSplitView => "◫",
        ShortcutAction.Lock => "⚿",
        _ => "▣",
    };

    private static string GlyphFor(SessionBarAction a) => a switch
    {
        SessionBarAction.SendCtrlAltDel => "⚿",
        SessionBarAction.Snapshot => "◉",
        SessionBarAction.NextMonitor => "⇨",
        SessionBarAction.ScreenKeyboard => "⌨",
        SessionBarAction.SendEscape => "⎋",
        SessionBarAction.SendWindows => "⊞",
        SessionBarAction.RightClick => "◨",
        SessionBarAction.TouchMode => "☝",
        SessionBarAction.HideBar => HideGlyph,
        _ => "•",
    };

    private static FlowLayoutPanel Body() => new()
    {
        Tag = "body", FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty,
        BackColor = SectionBack,
    };

    private static FlowLayoutPanel Section(string title, Control body)
    {
        var section = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = SectionBack,
            Padding = new Padding(6, 4, 6, 6), Margin = new Padding(0, 0, 6, 6),
        };
        var header = new Label
        {
            Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = Color.FromArgb(200, 210, 230), Margin = new Padding(0, 4, 0, 8),
            Padding = new Padding(10, 3, 0, 3), Font = HeaderFont,
        };
        // Intestazione di gruppo: segno colorato a sinistra e filo sotto, per separare i gruppi a colpo d'occhio.
        header.Paint += (_, e) =>
        {
            using var mark = new SolidBrush(Accent);
            e.Graphics.FillRectangle(mark, 0, 2, 4, header.Height - 4);
            using var line = new Pen(Color.FromArgb(70, 75, 90));
            e.Graphics.DrawLine(line, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        section.Controls.Add(header);
        section.Controls.Add(body);
        return section;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(2, 4, 0, 4) };

    private static bool SessionActionEnabled(SessionBarAction action, Content content) =>
        action switch
        {
            SessionBarAction.NextMonitor => content.MultipleMonitors,
            SessionBarAction.TouchMode or SessionBarAction.HideBar => true,
            SessionBarAction.ScreenKeyboard => true, // la tastiera si apre anche senza sessione: serve per cercare e scrivere
            _ => content.CanTypeText,
        };

    /// <summary>
    /// Pulsante con il nome a sinistra e la scorciatoia a destra, disegnati senza sovrapporsi
    /// (<see cref="FullScreenBarLayout.LayoutActionButton"/>): su due righe se non ci stanno su una, nome troncato con «…».
    /// </summary>
    internal sealed class ActionButton : BarButton
    {
        public ActionButton(string name, string shortcut) : base(name, ButtonBack)
        {
            ShortcutText = shortcut;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public string ShortcutText { get; }

        /// <summary>Altezza minima da dito (la imposta la barra: 40 px logici per il monitor in uso).</summary>
        public int MinTouchHeight { get; set; } = 28;

        private bool _hover;

        /// <summary>Disposizione per la larghezza attuale (per le prove automatiche).</summary>
        internal ActionButtonLayout CurrentLayout()
        {
            const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int nameWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, measure).Width;
            int scWidth = ShortcutText.Length > 0 ? TextRenderer.MeasureText(ShortcutText, Font, Size.Empty, measure).Width : 0;
            int indent = Indent;
            var l = FullScreenBarLayout.LayoutActionButton(Math.Max(0, Width - indent), nameWidth, scWidth, Font.Height, MinTouchHeight);
            if (indent == 0)
                return l;
            // Icona a sinistra: nome e scorciatoia si spostano a destra.
            return l with
            {
                Name = l.Name with { X = l.Name.X + indent },
                Shortcut = l.Shortcut.IsEmpty ? l.Shortcut : l.Shortcut with { X = l.Shortcut.X + indent },
            };
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ShortcutText is null)
                return; // ridimensionato dal costruttore base, prima che la scorciatoia sia impostata
            int h = CurrentLayout().Height;
            if (Height != h)
                Height = h;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(_hover && Enabled ? FlatAppearance.MouseOverBackColor : BackColor))
                g.FillRectangle(back, ClientRectangle);
            DrawGlyph(g);
            var l = CurrentLayout();
            var fore = Enabled ? ForeColor : Color.FromArgb(120, 125, 135);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                        (l.TwoLines ? TextFormatFlags.Top : TextFormatFlags.VerticalCenter);
            var nameRect = l.TwoLines ? l.Name : l.Name with { Height = Height };
            TextRenderer.DrawText(g, Text, Font, nameRect, fore, flags | TextFormatFlags.Left);
            if (ShortcutText.Length > 0)
            {
                var scRect = l.TwoLines ? l.Shortcut : l.Shortcut with { Height = Height };
                TextRenderer.DrawText(g, ShortcutText, Font, scRect, HeaderFore, flags | TextFormatFlags.Right);
            }
        }
    }

    private BarButton MakeButton(string text, Color back, string glyph = "")
    {
        var b = new BarButton(text, back) { Glyph = glyph };
        b.Suppress = () => PanActive;
        return b;
    }

    /// <summary>Pulsante della barra: icona a sinistra (se c'è), etichetta leggibile, nessun clic se il dito ha scorso.</summary>
    internal class BarButton : Button
    {
        public BarButton(string text, Color back)
        {
            Text = text;
            AutoSize = false;
            Height = 28;
            TextAlign = ContentAlignment.MiddleLeft;
            AutoEllipsis = true;
            FlatStyle = FlatStyle.Flat;
            BackColor = back;
            ForeColor = Color.White;
            Font = ItemFont;
            Margin = new Padding(0, 0, 0, 3);
            UseVisualStyleBackColor = false;
            TabStop = false;
            Cursor = Cursors.Hand;
            UseMnemonic = false;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 110, 180);
        }

        /// <summary>Icona (un carattere di Segoe UI Symbol); vuota = nessuna icona.</summary>
        public string Glyph { get; set; } = "";

        /// <summary>Se restituisce vero il clic viene ignorato (il dito stava scorrendo).</summary>
        public Func<bool>? Suppress { get; set; }

        /// <summary>Larghezza riservata all'icona, scalata col DPI.</summary>
        internal int Indent => Glyph.Length > 0 ? TouchMetrics.Scale(40, DeviceDpi) : 0;

        /// <summary>Sposta il testo a destra dell'icona (i pulsanti con disegno proprio non lo usano).</summary>
        internal void ApplyGlyphPadding()
        {
            if (Glyph.Length > 0 && this is not ActionButton)
                Padding = new Padding(Indent, 0, TouchMetrics.Scale(8, DeviceDpi), 0);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Suppress?.Invoke() == true)
                return;
            base.OnClick(e);
        }

        protected void DrawGlyph(Graphics g)
        {
            if (Glyph.Length == 0)
                return;
            var color = Enabled ? Color.FromArgb(170, 200, 245) : Color.FromArgb(110, 115, 125);
            TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(0, 0, Indent, Height), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);
            DrawGlyph(pevent.Graphics);
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var bmp in _thumbnails.Values)
                bmp.Dispose();
            _thumbnails.Clear();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
