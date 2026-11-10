using System.Drawing;
using RdpManager.Core;

namespace RdpManager.Linux.Features;

/// <summary>
/// Elenco dei monitor, per le Impostazioni e per lo spostamento della finestra. L'interfaccia lo riempie con
/// <c>Window.Screens</c> (<see cref="FromAvalonia"/>); senza interfaccia (prove) è vuoto.
/// </summary>
public static class ScreenProvider
{
    public static Func<IReadOnlyList<ScreenInfo>> Source { get; set; } = () => [];

    public static IReadOnlyList<ScreenInfo> All() => Source();

    /// <summary>Converte i monitor di Avalonia; il nome è «Monitor 1», «Monitor 2», … nell'ordine da sinistra a destra (non c'è un nome di dispositivo stabile fra le sessioni).</summary>
    public static IReadOnlyList<ScreenInfo> FromAvalonia(Avalonia.Controls.Screens screens)
    {
        var list = new List<ScreenInfo>();
        var ordered = screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var name = string.IsNullOrWhiteSpace(s.DisplayName) ? $"Monitor {i + 1}" : s.DisplayName!;
            list.Add(new ScreenInfo(name + $" ({s.Bounds.Width}x{s.Bounds.Height} @ {s.Bounds.X},{s.Bounds.Y})",
                new Rectangle(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
                new Rectangle(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height),
                s.IsPrimary, (int)Math.Round(96 * s.Scaling)));
        }
        return list;
    }
}
