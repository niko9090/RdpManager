using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace RdpManager.Linux.Dialogs;

/// <summary>Finestra semplice costruita in codice per messaggio, conferma (Sì/No) e richiesta di una riga di testo.</summary>
public sealed class TextDialog : Window
{
    private readonly TextBox? _input;

    private TextDialog(string title, string message, string? inputInitial, string okText, string? cancelText, bool secret = false, bool defaultNo = false)
    {
        Title = title;
        Width = 440;
        MinWidth = 320;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var ok = new Button { Content = okText, IsDefault = !defaultNo, MinWidth = 96 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, IsDefault = defaultNo, MinWidth = 96 };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
            if (defaultNo)
                Opened += (_, _) => cancel.Focus();
        }
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (inputInitial is not null)
        {
            _input = new TextBox { Text = inputInitial };
            if (secret)
                _input.PasswordChar = '●';
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter)
                    return;
                e.Handled = true;
                Close(true);
            };
            panel.Children.Add(_input);
            Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
        }
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        await new TextDialog(title, message, null, "Sì", "No").ShowDialogTracked<bool>(owner);

    /// <summary>Conferma di un'azione che distrugge dati: il pulsante predefinito (Invio) è «No».</summary>
    public static async Task<bool> ConfirmDestructiveAsync(Window owner, string title, string message) =>
        await new TextDialog(title, message, null, "Sì", "No", defaultNo: true).ShowDialogTracked<bool>(owner);

    public static async Task MessageAsync(Window owner, string title, string message) =>
        await new TextDialog(title, message, null, "OK", null).ShowDialogTracked<bool>(owner);

    /// <summary>Come <see cref="PromptAsync"/> ma il testo scritto è nascosto (password del file).</summary>
    public static async Task<string?> PromptSecretAsync(Window owner, string title, string label)
    {
        var dialog = new TextDialog(title, label, "", "OK", "Annulla", secret: true);
        return await dialog.ShowDialogTracked<bool>(owner) ? dialog._input!.Text ?? "" : null;
    }

    /// <summary>Sceglie una voce da un elenco, anche scrivendone una nuova se <paramref name="editable"/>; null se annullato.</summary>
    public static async Task<string?> ChooseAsync(Window owner, string title, string label, IReadOnlyList<string> options, string initial, bool editable)
    {
        var dialog = new TextDialog(title, label, null, "OK", "Annulla");
        Control input;
        if (editable)
        {
            input = new AutoCompleteBox { ItemsSource = options, Text = initial, MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        }
        else
        {
            input = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(initial) ? initial : options.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch };
        }
        ((StackPanel)dialog.Content!).Children.Insert(1, input);
        if (!await dialog.ShowDialogTracked<bool>(owner))
            return null;
        return input is AutoCompleteBox box ? box.Text ?? "" : (input as ComboBox)?.SelectedItem as string;
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var dialog = new TextDialog(title, label, initial, "OK", "Annulla");
        return await dialog.ShowDialogTracked<bool>(owner) ? dialog._input!.Text : null;
    }
}
