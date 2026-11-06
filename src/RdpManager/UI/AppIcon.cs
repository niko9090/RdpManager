using System.Reflection;

namespace RdpManager.UI;

/// <summary>
/// L'icona dell'app (monitor con schede), incorporata nella dll come «RdpManager.app.ico». Una sola istanza condivisa:
/// finestra principale, dialoghi e icona nell'area di notifica la usano al posto di quella predefinita di Windows.
/// </summary>
internal static class AppIcon
{
    internal const string ResourceName = "RdpManager.app.ico";

    private static readonly Lazy<Icon> Shared = new(Load);

    /// <summary>L'icona dell'app; da non eliminare (è condivisa).</summary>
    internal static Icon Instance => Shared.Value;

    private static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Icona dell'app mancante tra le risorse: " + ResourceName);
        return new Icon(stream);
    }
}
