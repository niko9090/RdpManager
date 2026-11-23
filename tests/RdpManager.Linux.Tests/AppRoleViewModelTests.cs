using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

/// <summary>Ruolo su Linux: scelta al primo avvio e cambio da Impostazioni. Solo ViewModel con dialoghi finti.</summary>
public class AppRoleViewModelTests
{
    private static string RoleFile(TempData data) => Path.Combine(Path.GetDirectoryName(data.FilePath)!, AppRoleStore.FileName);

    private static MainWindowViewModel Make(TempData data, ScriptedDialogs dialogs)
    {
        var vm = A.Vm(data, dialogs);
        vm.RolePath = RoleFile(data);
        return vm;
    }

    [Fact]
    public async Task Al_primo_avvio_chiede_il_ruolo_e_lo_salva()
    {
        using var data = new TempData();
        var vm = Make(data, new ScriptedDialogs { Choice = "Gestore marshmelloVPN" });

        Assert.Equal(AppRole.VpnManager, await vm.ResolveStartupRoleAsync());
        Assert.Equal(AppRole.VpnManager, AppRoleStore.Load(RoleFile(data)));
    }

    [Fact]
    public async Task Annullando_la_scelta_vale_il_predefinito_e_non_si_salva_niente()
    {
        using var data = new TempData();
        var vm = Make(data, new ScriptedDialogs { Choice = null });

        Assert.Equal(AppRole.DesktopAndVpn, await vm.ResolveStartupRoleAsync());
        Assert.False(File.Exists(RoleFile(data))); // si richiede al prossimo avvio
    }

    [Fact]
    public async Task Con_il_ruolo_gia_scelto_non_chiede_niente()
    {
        using var data = new TempData();
        AppRoleStore.Save(AppRole.VpnManager, RoleFile(data));
        var dialogs = new ScriptedDialogs { Choice = "Desktop remoto + marshmelloVPN" }; // se chiedesse, il ruolo cambierebbe
        var vm = Make(data, dialogs);

        Assert.Equal(AppRole.VpnManager, await vm.ResolveStartupRoleAsync());
        Assert.Equal(AppRole.VpnManager, AppRoleStore.Load(RoleFile(data)));
    }

    [Fact]
    public async Task File_rovinato_conta_come_non_scelto_e_si_richiede()
    {
        using var data = new TempData();
        File.WriteAllText(RoleFile(data), "{ rotto");
        var vm = Make(data, new ScriptedDialogs { Choice = "Desktop remoto + marshmelloVPN" });

        Assert.Equal(AppRole.DesktopAndVpn, await vm.ResolveStartupRoleAsync());
        Assert.True(AppRoleStore.IsChosen(RoleFile(data)));
    }

    [Fact]
    public void Le_Impostazioni_cambiano_il_ruolo_solo_su_Salva_e_solo_se_e_cambiato()
    {
        using var data = new TempData();
        var path = RoleFile(data);
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, data.FilePath, rolePath: path);
        var startup = vm.Sections.OfType<RdpManager.Linux.ViewModels.Settings.AvvioPageViewModel>().Single();

        Assert.Equal(["Desktop remoto + marshmelloVPN", "Gestore marshmelloVPN"], startup.RoleChoices);
        Assert.Equal(0, startup.RoleIndex);
        vm.Apply();
        Assert.False(File.Exists(path)); // invariato: niente scrittura

        startup.RoleIndex = 1;
        Assert.False(File.Exists(path)); // prima di Salva non si scrive
        vm.Apply();
        Assert.Equal(AppRole.VpnManager, AppRoleStore.Load(path));
    }

    [Fact]
    public void Le_Impostazioni_partono_dal_ruolo_salvato()
    {
        using var data = new TempData();
        var path = RoleFile(data);
        AppRoleStore.Save(AppRole.VpnManager, path);
        var vm = new SettingsViewModel(new AppSettings(), data.FilePath, rolePath: path);
        Assert.Equal(1, vm.Sections.OfType<RdpManager.Linux.ViewModels.Settings.AvvioPageViewModel>().Single().RoleIndex);
    }
}
