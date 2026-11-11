using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RdpManager.Linux.ViewModels.Settings;

namespace RdpManager.Linux.Dialogs.Settings;

/// <summary>
/// Trova la vista di una pagina dal nome del suo modello: <c>AspettoPageViewModel</c> diventa <c>PaginaAspetto</c>.
/// Una pagina nuova non richiede quindi modifiche a <c>SettingsWindow.axaml</c>.
/// </summary>
public sealed class SettingsPageTemplate : IDataTemplate
{
    public static Type? ViewTypeFor(Type viewModelType)
    {
        const string suffix = "PageViewModel";
        var name = viewModelType.Name;
        if (!name.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        return typeof(SettingsPageTemplate).Assembly.GetType(
            $"{typeof(SettingsPageTemplate).Namespace}.Pagina{name[..^suffix.Length]}");
    }

    public bool Match(object? data) => data is SettingsPageViewModel;

    public Control? Build(object? param) =>
        param is not null && ViewTypeFor(param.GetType()) is { } type
            ? (Control?)Activator.CreateInstance(type)
            : new TextBlock { Text = "Pagina non disponibile." };
}
