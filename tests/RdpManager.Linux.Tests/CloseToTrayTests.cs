using RdpManager.Core;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>Chiusura della finestra su Linux (k-159): resta nella barra o riduce a icona; VPN all'uscita. Solo ViewModel, nessuna finestra vera.</summary>
public class CloseToTrayTests
{
    private static MainWindowViewModel Make(TempData data, out RecordingRunner runner, bool vpnOn = true)
    {
        var vm = A.Vm(data, new ScriptedDialogs());
        vm.RolePath = Path.Combine(Path.GetDirectoryName(data.FilePath)!, AppRoleStore.FileName);
        var up = vpnOn;
        var r = runner = new RecordingRunner((file, args) =>
        {
            if (args.Contains("down"))
                up = false;
            return file == "ip" ? new ProcessResult(up ? 0 : 1, "", "") : new ProcessResult(0, "", "");
        });
        vm.VpnFactory = () => new LinuxWireGuard(r, Path.Combine(Path.GetDirectoryName(data.FilePath)!, "x.conf"), LinuxVpnBackend.WgQuick);
        return vm;
    }

    [Fact]
    public void Con_l_opzione_accesa_e_l_icona_la_X_nasconde()
    {
        using var data = new TempData();
        var vm = Make(data, out _);
        vm.Document.Settings.CloseToTray = true;
        Assert.Equal(CloseAction.HideToTray, vm.DecideClose(trayReady: true, exiting: false));
    }

    [Fact]
    public void Senza_area_delle_icone_la_X_riduce_a_icona()
    {
        using var data = new TempData();
        var vm = Make(data, out _);
        vm.Document.Settings.CloseToTray = true;
        Assert.Equal(CloseAction.Minimize, vm.DecideClose(trayReady: false, exiting: false));
    }

    [Fact]
    public void Con_l_opzione_spenta_o_con_Esci_si_esce_davvero()
    {
        using var data = new TempData();
        var vm = Make(data, out _);
        vm.Document.Settings.CloseToTray = false;
        Assert.Equal(CloseAction.Exit, vm.DecideClose(true, false));
        vm.Document.Settings.CloseToTray = true;
        Assert.Equal(CloseAction.Exit, vm.DecideClose(true, exiting: true));
    }

    [Fact]
    public void L_opzione_si_accende_di_serie_una_volta_sola()
    {
        using var data = new TempData();
        var vm = Make(data, out _);
        Assert.True(new AppSettings().CloseToTray); // valore di serie, uguale a Windows (k-158)
        vm.Document.Settings.CloseToTray = false; // impostazioni già salvate prima, con l'opzione spenta
        Assert.True(vm.ApplyCloseToTrayDefaultOnce());
        Assert.True(vm.Document.Settings.CloseToTray);
        vm.Document.Settings.CloseToTray = false; // scelta dell'utente: non si riaccende
        Assert.False(vm.ApplyCloseToTrayDefaultOnce());
        Assert.False(vm.Document.Settings.CloseToTray);
    }

    [Fact]
    public void L_avviso_compare_solo_la_prima_volta()
    {
        using var data = new TempData();
        var vm = Make(data, out _);
        Assert.Equal("RdpManager resta attivo nella barra. Per uscire usa Esci.", vm.TakeTrayHint());
        Assert.Null(vm.TakeTrayHint());
    }

    [Fact]
    public async Task All_uscita_la_VPN_integrata_si_spegne()
    {
        using var data = new TempData();
        var vm = Make(data, out var runner);
        AppRoleStore.Save(AppRole.DesktopAndVpn, vm.RolePath);
        Assert.True(await vm.StopIntegratedVpnOnExitAsync());
        Assert.Contains(runner.Calls, c => c.StartsWith("pkexec wg-quick down"));
    }

    [Fact]
    public async Task All_uscita_con_la_VPN_gia_spenta_non_chiede_la_password()
    {
        using var data = new TempData();
        var vm = Make(data, out var runner, vpnOn: false);
        Assert.False(await vm.StopIntegratedVpnOnExitAsync());
        Assert.DoesNotContain(runner.Calls, c => c.Contains("down"));
    }

    [Fact]
    public async Task Con_il_gestore_VPN_installato_la_VPN_resta_accesa()
    {
        using var data = new TempData();
        var vm = Make(data, out var runner);
        AppRoleStore.Save(AppRole.VpnManager, vm.RolePath);
        Assert.False(await vm.StopIntegratedVpnOnExitAsync());
        Assert.DoesNotContain(runner.Calls, c => c.Contains("down"));
    }
}
