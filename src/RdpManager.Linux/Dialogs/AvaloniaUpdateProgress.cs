using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.Dialogs;

/// <summary>Finestra di avanzamento dello scaricamento dell'aggiornamento (non modale, con «Annulla»).</summary>
internal sealed class AvaloniaUpdateProgress : IUpdateProgressView
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Window _window;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Value = 0 };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancelButton = new() { Content = "Annulla", IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };

    public AvaloniaUpdateProgress(ReleaseInfo release)
    {
        var cancel = _cancelButton;
        cancel.Click += (_, _) =>
        {
            cancel.IsEnabled = false;
            _text.Text = "Annullamento…";
            _cancel.Cancel();
        };
        _text.Text = $"Scarico RdpManager {release.Version.ToString(3)}…";
        _window = new Window
        {
            Title = "Aggiornamento",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { _text, _bar, cancel } },
        };
        // Chiudere la finestra equivale ad annullare.
        _window.Closing += (_, _) =>
        {
            if (!_cancel.IsCancellationRequested)
                _cancel.Cancel();
        };
    }

    public CancellationToken Cancelled => _cancel.Token;

    public void Open(Window owner) => _window.Show(owner);

    public void Report(double fraction) => Dispatcher.UIThread.Post(() => _bar.Value = Math.Clamp(fraction, 0, 1));

    public void SetText(string text) => Dispatcher.UIThread.Post(() => _text.Text = text);

    public void SetIndeterminate(bool on) => Dispatcher.UIThread.Post(() =>
    {
        _bar.IsIndeterminate = on;
        _cancelButton.IsEnabled = !on; // durante l'installazione non si annulla più
    });

    public void Dispose() => Dispatcher.UIThread.Post(() =>
    {
        try { _window.Close(); }
        catch (InvalidOperationException) { }
    });
}
