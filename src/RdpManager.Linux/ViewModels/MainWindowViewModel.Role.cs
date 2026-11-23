using RdpManager.Core;

namespace RdpManager.Linux.ViewModels;

/// <summary>
/// Ruolo dell'installazione (k-151). I pacchetti Linux (.deb, .rpm, AppImage) non hanno pagine di scelta: il ruolo si
/// sceglie al primo avvio con una finestra semplice (le due voci di <see cref="AppRoleStore.Describe"/>) e si cambia poi
/// in Impostazioni → Generale. Una finestra sola, senza voci in più nel menu applicazioni, vale per tutti i pacchetti
/// allo stesso modo e non lascia due icone da tenere allineate negli aggiornamenti.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>File del ruolo; le prove ne indicano uno temporaneo.</summary>
    internal string RolePath { get; set; } = AppRoleStore.DefaultPath;

    /// <summary>
    /// All'avvio: se il ruolo non è mai stato scelto lo chiede (annullando si usa il predefinito per questa volta e si
    /// richiede al prossimo avvio) e lo salva. Restituisce il ruolo da usare; con <see cref="AppRole.VpnManager"/>
    /// l'interfaccia apre la Plancia VPN.
    /// </summary>
    public async Task<AppRole> ResolveStartupRoleAsync()
    {
        if (AppRoleStore.IsChosen(RolePath))
            return AppRoleStore.Load(RolePath);
        var names = Enum.GetValues<AppRole>().Select(AppRoleStore.Describe).ToList();
        var picked = await _dialogs.ChooseAsync("Come usi RdpManager?",
            "Scegli il ruolo di questa installazione. Lo cambi quando vuoi in Impostazioni → Generale.",
            names, names[(int)AppRoleStore.Default], editable: false);
        int index = picked is null ? -1 : names.IndexOf(picked);
        if (index < 0)
            return AppRoleStore.Default;
        var role = (AppRole)index;
        AppRoleStore.Save(role, RolePath);
        return role;
    }
}
