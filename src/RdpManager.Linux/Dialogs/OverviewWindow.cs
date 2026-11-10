using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Dialogs;

/// <summary>
/// Panoramica delle sessioni: una scheda per sessione aperta, con titolo e stato; un clic porta in primo piano la sessione.
/// Le miniature dell'immagine non ci sono: una sessione incorporata è una finestra X11 figlia e Avalonia non può copiarne
/// il contenuto (la «Foto della sessione» usa «import» a parte).
/// </summary>
public sealed class OverviewWindow : Window
{
    private OverviewWindow(IReadOnlyList<SessionTabViewModel> tabs, SessionTabViewModel? selected)
    {
        Title = "Panoramica delle sessioni";
        Width = 880;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var wrap = new WrapPanel { Margin = new Thickness(16) };
        foreach (var tab in tabs)
        {
            var state = tab.Session switch
            {
                FreeRdpSession f when f.IsConnected => "Collegata",
                FreeRdpSession => "Non collegata",
                _ => "Aperta",
            };
            var card = new Button
            {
                Width = 250,
                Height = 130,
                Margin = new Thickness(6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(tab == selected ? 3 : 1),
                BorderBrush = tab == selected ? Brushes.DodgerBlue : Brushes.Gray,
                Content = new StackPanel
                {
                    Spacing = 4,
                    Margin = new Thickness(6),
                    Children =
                    {
                        new TextBlock { Text = tab.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = state + (tab.IsDetached ? " · in una finestra separata" : ""), Opacity = 0.75 },
                    },
                },
            };
            card.Click += (_, _) => Close(tab);
            wrap.Children.Add(card);
        }
        Content = new ScrollViewer { Content = wrap };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
    }

    public static Task<SessionTabViewModel?> ShowAsync(Window owner, IReadOnlyList<SessionTabViewModel> tabs, SessionTabViewModel? selected) =>
        new OverviewWindow(tabs, selected).ShowDialog<SessionTabViewModel?>(owner);
}
