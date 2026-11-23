using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RdpManager.Core;
using RdpManager.Linux.Dialogs.Tools;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Avviso «File in arrivo»: mittente, nome e dimensione, «Accetta» (con la cartella dove salvare, di predefinito
/// Scaricati) e «Rifiuta». Chiuderlo senza scegliere vale «più tardi».
/// </summary>
internal sealed class IncomingFileWindow : Window
{
    private readonly TextBox _folder = new() { Text = VpnFileModel.DefaultFolder() };

    public IncomingFileWindow(IncomingFile file, Func<string, Task<string?>> pickFolder)
    {
        Title = VpnFileModel.Title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock { Text = VpnFileModel.PromptText(file), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var browse = DialogKit.Button("Sfoglia…", async (_, _) =>
        {
            if (await pickFolder(_folder.Text ?? "") is { Length: > 0 } picked)
                _folder.Text = picked;
        });
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(browse);
        row.Children.Add(_folder);

        var accept = DialogKit.Button("Accetta", (_, _) => Close(Accept), accent: true, isDefault: true);
        var reject = DialogKit.Button("Rifiuta", (_, _) => Close(Reject));
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                text,
                DialogKit.Label("Salva in:", "muted"),
                row,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { reject, accept } },
            },
        };
    }

    public const int Later = 0, Accept = 1, Reject = 2;

    public string Folder => (_folder.Text ?? "").Trim();

    public IncomingFileDecision ToDecision(int result) => result switch
    {
        Accept => new IncomingFileDecision(IncomingFileChoice.Accept, Folder.Length > 0 ? Folder : null),
        Reject => new IncomingFileDecision(IncomingFileChoice.Reject),
        _ => new IncomingFileDecision(IncomingFileChoice.Later),
    };
}
