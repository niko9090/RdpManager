using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Richiesta di un testo (nome cartella).</summary>
public sealed class TextPromptDialog : Form
{
    private readonly TextBox _text = new() { Width = 320 };

    private TextPromptDialog(string title, string label, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = label, AutoSize = true });
        layout.Controls.Add(_text);
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        _text.Text = initial;
        ThemeManager.Apply(this);
    }

    public static string? Ask(IWin32Window owner, string title, string label, string initial = "")
    {
        using var dlg = new TextPromptDialog(title, label, initial);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._text.Text.Trim() : null;
    }
}

/// <summary>Finestra separata che ospita una sessione staccata dalle schede.</summary>
public sealed class DetachedSessionForm : Form
{
    public DetachedSessionForm(RdpSessionView view, Icon? icon)
    {
        View = view;
        Text = $"{view.Connection.DisplayName} - RdpManager";
        Icon = icon;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        Size = new Size(1280, 800);
        Controls.Add(view);
        view.StateChanged += OnViewStateChanged;
        view.FullScreenRequested += OnFullScreenRequested;
    }

    /// <summary>Apre la finestra sul monitor indicato (quello della finestra principale, di solito).</summary>
    public void ShowOn(ScreenInfo? screen)
    {
        if (screen is not null)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = MonitorLayout.Center(Size, screen.WorkingArea);
        }
        Show();
    }

    public RdpSessionView View { get; }

    /// <summary>Toglie la vista dalla finestra senza chiuderla (prima di riagganciarla a una scheda).</summary>
    public void ReleaseView()
    {
        View.StateChanged -= OnViewStateChanged;
        View.FullScreenRequested -= OnFullScreenRequested;
        Controls.Remove(View);
    }

    // Nella finestra staccata lo schermo intero è quello del controllo RDP (si esce dalla sua barra di connessione).
    private void OnFullScreenRequested(object? sender, EventArgs e) => View.EnterControlFullScreen();

    private void OnViewStateChanged(object? sender, EventArgs e) =>
        Text = $"{View.Connection.DisplayName} ({View.StateText}) - RdpManager";
}
