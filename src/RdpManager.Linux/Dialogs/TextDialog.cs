using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace RdpManager.Linux.Dialogs;

/// <summary>Finestra semplice costruita in codice per messaggio, conferma (Sì/No) e richiesta di una riga di testo.</summary>
public sealed class TextDialog : Window
{
    private readonly TextBox? _input;

    private TextDialog(string title, string message, string? inputInitial, string okText, string? cancelText)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var ok = new Button { Content = okText, IsDefault = true };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (inputInitial is not null)
        {
            _input = new TextBox { Text = inputInitial };
            _input.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Close(true); };
            panel.Children.Add(_input);
            Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
        }
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        await new TextDialog(title, message, null, "Sì", "No").ShowDialog<bool>(owner);

    public static async Task MessageAsync(Window owner, string title, string message) =>
        await new TextDialog(title, message, null, "OK", null).ShowDialog<bool>(owner);

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var dialog = new TextDialog(title, label, initial, "OK", "Annulla");
        return await dialog.ShowDialog<bool>(owner) ? dialog._input!.Text : null;
    }
}
