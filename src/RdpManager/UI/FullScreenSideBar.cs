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
    private static readonly Font HeaderFont = new(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold);

    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Back };
    private readonly ToolTip _tips = new();
    private readonly Dictionary<RdpSessionView, Bitmap> _thumbnails = [];
    private FlowLayoutPanel? _sessionsBody;
    private ListBox? _connectionsList;
    private Content? _content;

    public FullScreenSideBar()
    {
        BackColor = Back;
        Controls.Add(_flow);
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
        _flow.Padding = new Padding(6);
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
                    var b = MakeButton(w.Name, ButtonBack);
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
                    var b = new ActionButton(ShortcutMap.Describe(a), combo.IsEmpty ? "" : combo.ToString()) { Tag = action };
                    _tips.SetToolTip(b, ShortcutMap.Describe(a) + (combo.IsEmpty ? " (senza scorciatoia)" : $" ({combo})"));
                    b.Click += (_, _) => ActionClicked?.Invoke(this, action);
                    actions.Controls.Add(b);
                }
                return actions;
            case BarSection.SessionActions:
                var sessionActions = Body();
                foreach (var a in FullScreenBarLayout.SessionActions)
                {
                    var action = a;
                    var b = new ActionButton(FullScreenBarSettings.Describe(a), "") { Tag = action };
                    b.Enabled = SessionActionEnabled(a, content);
                    _tips.SetToolTip(b, a switch
                    {
                        SessionBarAction.SendCtrlAltDel => "Invia Ctrl+Alt+Canc alla sessione visibile",
                        SessionBarAction.Snapshot => "Foto della sessione visibile (Immagini\\RdpManager e appunti)",
                        _ => content.MultipleMonitors ? "Schermo intero sul monitor successivo" : "C'è un solo monitor",
                    });
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
                    var b = MakeButton(t.DisplayName, ButtonBack);
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
            Height = Vertical ? 180 : 120, DisplayMember = nameof(RdpConnection.DisplayName), Margin = Padding.Empty,
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
            b.Padding = new Padding(14, 0, 4, 0);
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
        var b = MakeButton(c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : ""), ButtonBack);
        b.Tag = c;
        b.Padding = new Padding(10, 0, 4, 0);
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
    }

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
        section.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = HeaderFore, Margin = new Padding(0, 2, 0, 4),
            Font = HeaderFont,
        });
        section.Controls.Add(body);
        return section;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(2, 4, 0, 4) };

    private static bool SessionActionEnabled(SessionBarAction action, Content content) =>
        action == SessionBarAction.NextMonitor ? content.MultipleMonitors : content.CanTypeText;

    /// <summary>
    /// Pulsante con il nome a sinistra e la scorciatoia a destra, disegnati senza sovrapporsi
    /// (<see cref="FullScreenBarLayout.LayoutActionButton"/>): su due righe se non ci stanno su una, nome troncato con «…».
    /// </summary>
    internal sealed class ActionButton : Button
    {
        public ActionButton(string name, string shortcut)
        {
            Text = name;
            ShortcutText = shortcut;
            AutoSize = false;
            Height = 28;
            FlatStyle = FlatStyle.Flat;
            BackColor = ButtonBack;
            ForeColor = Color.White;
            Margin = new Padding(0, 0, 0, 3);
            UseVisualStyleBackColor = false;
            TabStop = false;
            Cursor = Cursors.Hand;
            UseMnemonic = false;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 110, 180);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public string ShortcutText { get; }

        private bool _hover;

        /// <summary>Disposizione per la larghezza attuale (per le prove automatiche).</summary>
        internal ActionButtonLayout CurrentLayout()
        {
            const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int nameWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, measure).Width;
            int scWidth = ShortcutText.Length > 0 ? TextRenderer.MeasureText(ShortcutText, Font, Size.Empty, measure).Width : 0;
            return FullScreenBarLayout.LayoutActionButton(Width, nameWidth, scWidth, Font.Height);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
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

    private static Button MakeButton(string text, Color back)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Margin = new Padding(0, 0, 0, 3),
            UseVisualStyleBackColor = false, TabStop = false, Cursor = Cursors.Hand, UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 110, 180);
        return b;
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
