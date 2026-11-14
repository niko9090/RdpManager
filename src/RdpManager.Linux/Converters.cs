using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RdpManager.Linux;

public static class Converters
{
    /// <summary>Le cartelle dell'albero in grassetto.</summary>
    public static readonly IValueConverter FolderWeight =
        new FuncValueConverter<bool, FontWeight>(isFolder => isFolder ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Le intestazioni delle sezioni rapide (Preferiti, Recenti) in grassetto, come in Windows; le cartelle restano normali.</summary>
    public static readonly IValueConverter SectionWeight =
        new FuncValueConverter<bool, FontWeight>(isSection => isSection ? FontWeight.Bold : FontWeight.Normal);
}
