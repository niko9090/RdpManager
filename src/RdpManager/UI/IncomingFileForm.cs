using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>
/// Avviso «File in arrivo»: mittente, nome e dimensione, con «Accetta» (si sceglie dove salvare, di predefinito Scaricati)
/// e «Rifiuta». Chiuderlo senza scegliere lascia il file sul server (decisione «più tardi»).
/// </summary>
internal sealed class IncomingFileForm : TouchForm
{
    private readonly TextBox _folder = new() { Width = 330 };

    public IncomingFileForm(IncomingFile file)
    {
        DialogStyle.Setup(this, VpnFileModel.Title);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = true;
        TopMost = true; // l'avviso arriva da solo: deve vedersi anche se il programma è in secondo piano
        StartPosition = FormStartPosition.CenterScreen;

        var text = new Label
        {
            Text = VpnFileModel.PromptText(file), AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 3, 3, 12),
        };
        var browse = DialogStyle.Button("Sfoglia…");
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Dove salvare il file", SelectedPath = _folder.Text, UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _folder.Text = dlg.SelectedPath;
        };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange([_folder, browse]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        layout.Controls.Add(text);
        layout.Controls.Add(new Label { Text = "Salva in:", AutoSize = true });
        layout.Controls.Add(row);

        var accept = DialogStyle.Button("Accetta", DialogResult.OK);
        var reject = DialogStyle.Button("Rifiuta", DialogResult.No);
        Controls.Add(layout);
        Controls.Add(DialogStyle.Buttons(reject, accept));
        AcceptButton = accept;
        _folder.Text = VpnFileModel.DefaultFolder();
        ThemeManager.Apply(this);
    }

    public string Folder => _folder.Text.Trim();

    /// <summary>Mostra l'avviso e traduce la scelta (la X della finestra vale «più tardi»).</summary>
    public static IncomingFileDecision Ask(IWin32Window? owner, IncomingFile file)
    {
        using var form = new IncomingFileForm(file);
        var result = form.ShowDialog(owner);
        return result switch
        {
            DialogResult.OK => new IncomingFileDecision(IncomingFileChoice.Accept, form.Folder.Length > 0 ? form.Folder : null),
            DialogResult.No => new IncomingFileDecision(IncomingFileChoice.Reject),
            _ => new IncomingFileDecision(IncomingFileChoice.Later),
        };
    }
}
