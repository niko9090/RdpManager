using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Barra di passaggio fra le sessioni a schermo intero: compare portando il mouse sul bordo in alto.
/// Le sessioni connesse hanno il numero della scorciatoia Ctrl+Alt+N; le altre sono elencate con il loro stato.
/// </summary>
internal sealed class SwitchBar : Panel
{
    public sealed record Item(RdpSessionView View, int? Number, bool Selected);

    private static readonly Color Back = Color.FromArgb(30, 32, 38);
    private static readonly Color ButtonBack = Color.FromArgb(48, 51, 60);
    private static readonly Color SelectedBack = Color.FromArgb(40, 90, 160);

    private readonly FlowLayoutPanel _sessions = new()
    {
        Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Padding = new Padding(4, 4, 0, 0), BackColor = Back,
    };
    private readonly FlowLayoutPanel _commands = new()
    {
        Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(0, 4, 4, 0), BackColor = Back,
    };
    private readonly ToolTip _tips = new();

    public SwitchBar()
    {
        Height = 38;
        BackColor = Back;
        Controls.Add(_sessions);
        Controls.Add(_commands);
    }

    public event EventHandler<RdpSessionView>? SessionClicked;

    public void SetCommands(params (string Text, string Tip, Action OnClick)[] commands)
    {
        _commands.Controls.Clear();
        foreach (var (text, tip, onClick) in commands)
        {
            var b = MakeButton(text, ButtonBack);
            b.Click += (_, _) => onClick();
            _tips.SetToolTip(b, tip);
            _commands.Controls.Add(b);
        }
    }

    public void SetItems(IEnumerable<Item> items)
    {
        SuspendLayout();
        foreach (Control c in _sessions.Controls.Cast<Control>().ToList())
            c.Dispose();
        _sessions.Controls.Clear();
        foreach (var item in items)
        {
            var view = item.View;
            var c = view.Connection;
            var label = (item.Number is int n ? $"{n}  " : "") + c.DisplayName + (c.Label.Length > 0 ? $"  [{c.Label}]" : "");
            var b = MakeButton(label, item.Selected ? SelectedBack : ButtonBack);
            b.Tag = view;
            b.Padding = new Padding(14, 0, 6, 0);
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
            _sessions.Controls.Add(b);
        }
        if (_sessions.Controls.Count == 0)
            _sessions.Controls.Add(new Label { Text = "Nessuna sessione aperta", AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(8, 8, 0, 0) });
        ResumeLayout();
    }

    private static Button MakeButton(string text, Color back)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 28, MinimumSize = new Size(0, 28),
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Margin = new Padding(0, 0, 4, 0),
            UseVisualStyleBackColor = false, TabStop = false, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 110, 180);
        return b;
    }
}
