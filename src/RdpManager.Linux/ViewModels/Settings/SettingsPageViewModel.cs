namespace RdpManager.Linux.ViewModels.Settings;

/// <summary>
/// Una pagina delle Impostazioni. La vista si trova per nome: <c>XxxPageViewModel</c> è mostrata da
/// <c>Dialogs/Settings/PaginaXxx.axaml</c> (vedi <c>SettingsPageTemplate</c>).
/// </summary>
public abstract class SettingsPageViewModel : ViewModelBase
{
    /// <summary>Titolo nell'elenco di navigazione a sinistra.</summary>
    public abstract string Title { get; }

    /// <summary>Scrive i valori scelti nelle impostazioni (chiamato quando si preme Salva).</summary>
    public abstract void Apply();
}

/// <summary>Pagina di una funzione non ancora presente su Linux: mostra solo un avviso e non cambia nulla.</summary>
public abstract class PlaceholderSettingsPageViewModel : SettingsPageViewModel
{
    public string Notice => "Queste impostazioni arriveranno insieme alla funzione relativa.";

    public override void Apply() { }
}
