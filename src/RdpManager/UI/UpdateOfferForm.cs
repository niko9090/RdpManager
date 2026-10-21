using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Avviso non modale di un aggiornamento già scaricato e verificato: novità della release e tre scelte
/// («Aggiorna ora», «Alla chiusura», «Più tardi»). Chiuderlo con la X vale «Più tardi».
/// </summary>
internal sealed class UpdateOfferForm : Form
{
    private readonly TextBox _notes = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
    };

    public UpdateOfferForm(PendingUpdate update, string currentVersion)
    {
        Text = "Aggiornamento di RdpManager";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Size = new Size(560, 420);
        Padding = new Padding(10);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var release = update.Release;
        var heading = new Label
        {
            Text = $"RdpManager {release.Version.ToString(3)} è pronto (in uso: {currentVersion}).",
            Font = new Font(Font, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top,
        };
        var verified = new Label
        {
            Text = $"Installer scaricato e verificato (SHA256 {update.Sha256[..12]}… uguale a {Sha256Sums.FileName} della release).",
            AutoSize = true, Dock = DockStyle.Top, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 6),
        };
        _notes.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "Le novità sono nella pagina della release."
            : release.Notes.ReplaceLineEndings("\r\n");

        var now = new Button { Text = "Aggiorna ora", AutoSize = true };
        var onExit = new Button { Text = "Alla chiusura", AutoSize = true };
        var later = new Button { Text = "Più tardi", AutoSize = true };
        var page = new LinkLabel { Text = "Pagina della release", AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([later, onExit, now, page]);
        var tips = new ToolTip();
        tips.SetToolTip(now, "Chiude RdpManager, installa e lo riapre con le stesse sessioni");
        tips.SetToolTip(onExit, "Installa quando chiudi RdpManager, senza riaprirlo");
        tips.SetToolTip(later, "Te lo ripropone tra 24 ore o al prossimo avvio");

        Controls.Add(_notes);
        Controls.Add(verified);
        Controls.Add(heading);
        Controls.Add(buttons);

        now.Click += (_, _) => Finish(UpdateChoice.Now);
        onExit.Click += (_, _) => Finish(UpdateChoice.OnExit);
        later.Click += (_, _) => Finish(UpdateChoice.Later);
        page.LinkClicked += (_, _) => PageRequested?.Invoke(this, EventArgs.Empty);
        FormClosed += (_, _) =>
        {
            if (Choice is null)
                Chosen?.Invoke(this, UpdateChoice.Later);
        };
        ThemeManager.Apply(this);
    }

    /// <summary>Scelta fatta (anche chiudendo la finestra: «Più tardi»).</summary>
    public event EventHandler<UpdateChoice>? Chosen;
    public event EventHandler? PageRequested;

    public UpdateChoice? Choice { get; private set; }

    /// <summary>Sceglie (anche per le prove automatiche) e chiude l'avviso.</summary>
    internal void Finish(UpdateChoice choice)
    {
        Choice = choice;
        Close();
        Chosen?.Invoke(this, choice);
    }

    /// <summary>In basso a destra della finestra principale.</summary>
    public void PlaceOver(Rectangle owner) =>
        Location = new Point(Math.Max(owner.Left, owner.Right - Width - 24), Math.Max(owner.Top, owner.Bottom - Height - 48));
}
