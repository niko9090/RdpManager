using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

/// <summary>Le finestre del blocco B, aperte sopra la finestra principale.</summary>
public sealed class AvaloniaShellUi : IShellUi
{
    public Window? Owner { get; set; }

    private Window RequireOwner() => Owner ?? throw new InvalidOperationException("Finestra principale non ancora creata.");

    public Task ShowAccountAsync(AccountViewModel account) => new AccountWindow { DataContext = account }.ShowDialog(RequireOwner());

    public Task ShowVpnAsync(VpnViewModel vpn)
    {
        // Lo stato del tunnel si aggiorna mentre il pannello è aperto (come ogni 2 secondi su Windows).
        var window = new VpnWindow { DataContext = vpn };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await vpn.RefreshStateAsync();
        window.Opened += (_, _) => timer.Start();
        window.Closed += (_, _) => timer.Stop();
        return window.ShowDialog(RequireOwner());
    }

    public Task ShowWorkspacesAsync(WorkspacesViewModel workspaces)
    {
        var window = new WorkspacesWindow { DataContext = workspaces };
        return window.ShowDialog(RequireOwner());
    }

    public Task ShowTextAsync(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("monospace"),
            TextWrapping = TextWrapping.NoWrap,
        };
        var close = new Button { Content = "Chiudi", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96 };
        var window = new Window
        {
            Title = title,
            Width = 640,
            Height = 560,
            MinWidth = 420,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        close.Click += (_, _) => window.Close();
        var dock = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 12, 0, 0);
        dock.Children.Add(close);
        dock.Children.Add(box);
        window.Content = dock;
        return window.ShowDialog(RequireOwner());
    }

    public async Task<string?> AskSecretAsync(string title, string message)
    {
        var input = new TextBox { PasswordChar = '●' };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 96 };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Annulla", IsCancel = true, MinWidth = 96 };
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        input.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) window.Close(true); };
        window.Opened += (_, _) => input.Focus();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        return await window.ShowDialog<bool>(RequireOwner()) ? input.Text : null;
    }

    public async Task<bool> AskCertificateAsync(CertificatePrompt prompt)
    {
        var trust = new Button { Content = "Fidati e continua", MinWidth = 140 };
        trust.Classes.Add("danger");
        var reject = new Button { Content = "Rifiuta", IsCancel = true, IsDefault = true, MinWidth = 96 };
        reject.Classes.Add("accent");
        var window = new Window
        {
            Title = "Certificato del server",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        trust.Click += (_, _) => window.Close(true);
        reject.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Attenzione: certificato non verificato", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = prompt.Detail, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Fidati solo se sai che il certificato è cambiato per un motivo legittimo. La scelta vale per questa sessione.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { trust, reject } },
            },
        };
        return await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task<bool> OfferUpdateAsync(ReleaseInfo release, string currentVersion)
    {
        var open = new Button { Content = "Apri la pagina della release", IsDefault = true, MinWidth = 200 };
        open.Classes.Add("accent");
        var later = new Button { Content = "Più tardi", IsCancel = true, MinWidth = 96 };
        var window = new Window
        {
            Title = "Aggiornamento disponibile",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        open.Click += (_, _) => window.Close(true);
        later.Click += (_, _) => window.Close(false);
        var notes = release.Notes.Trim();
        if (notes.Length > 600)
            notes = notes[..600] + "…";
        var children = new List<Control>
        {
            new TextBlock { Text = $"È disponibile RdpManager {release.Version.ToString(3)}", FontSize = 18, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = $"Stai usando la versione {currentVersion}. Su Linux l'aggiornamento si scarica dalla pagina della release su GitHub.", TextWrapping = TextWrapping.Wrap },
        };
        if (notes.Length > 0)
            children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        children.Add(new TextBlock { Text = release.PageUrl, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });
        children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { later, open } });
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.AddRange(children);
        window.Content = panel;
        return await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        var files = await RequireOwner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> GetClipboardTextAsync() =>
        Owner?.Clipboard is { } clipboard ? await clipboard.GetTextAsync() : null;

    public async Task SetClipboardTextAsync(string text)
    {
        if (Owner?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public void OpenUrl(string url)
    {
        // Solo indirizzi web: l'indirizzo viene da fuori (GitHub) e va a xdg-open.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return;
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Default?.Warning("xdg-open non disponibile: " + ex.Message);
        }
    }

    public T Invoke<T>(Func<T> action) => Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    public Task<SessionTabViewModel?> ShowOverviewAsync(IReadOnlyList<SessionTabViewModel> tabs, SessionTabViewModel? selected) =>
        OverviewWindow.ShowAsync(RequireOwner(), tabs, selected);
}
