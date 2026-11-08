using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RdpManager.Linux;

public static class Converters
{
    /// <summary>Le cartelle dell'albero in grassetto.</summary>
    public static readonly IValueConverter FolderWeight =
        new FuncValueConverter<bool, FontWeight>(isFolder => isFolder ? FontWeight.SemiBold : FontWeight.Normal);
}
