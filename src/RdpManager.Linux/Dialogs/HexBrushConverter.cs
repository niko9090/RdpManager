using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RdpManager.Linux.Dialogs;

/// <summary>Da «#RRGGBB» a pennello, per le anteprime dei colori; testo vuoto o non valido = trasparente.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
