using System.Net;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

/// <summary>Plancia VPN su Linux: solo ViewModel con un client finto, nessuna finestra e nessuna attesa senza limite.</summary>
public class VpnDashboardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static VpnDashboardViewModel Make(Func<Task<IReadOnlyList<VpnDashboardDevice>>> load, bool signedIn = true) =>
        new(() => signedIn, _ => load(), () => Now);

    private static Func<Task<IReadOnlyList<VpnDashboardDevice>>> Devices(params VpnDashboardDevice[] devices) =>
        () => Task.FromResult<IReadOnlyList<VpnDashboardDevice>>(devices);

    [Fact]
    public async Task Prima_i_collegati_poi_per_nome()
    {
        var vm = Make(Devices(
            new("zeta", "10.66.0.5/32", false, Now.AddDays(-2)),
            new("beta", "10.66.0.3/32", true, Now.AddSeconds(-20)),
            new("alfa", "10.66.0.2/32", false, null),
            new("alice", "10.66.0.4/32", true, Now.AddMinutes(-1))));
        await vm.RefreshAsync();

        Assert.Equal(["alice", "beta", "alfa", "zeta"], vm.Rows.Select(r => r.Name));
        Assert.Null(vm.ErrorText);
        Assert.Equal("10.66.0.4", vm.Rows[0].Address); // senza /32
    }

    [Fact]
    public async Task Stato_collegato_verde_non_collegato_grigio_con_ultimo_contatto()
    {
        var vm = Make(Devices(
            new("su", "10.66.0.2/32", true, Now.AddSeconds(-10)),
            new("giu", "10.66.0.3/32", false, null)));
        await vm.RefreshAsync();

        var up = vm.Rows[0];
        Assert.Equal("Collegato", up.StateText);
        Assert.Equal(UiColors.Connected, up.StatusColor);
        Assert.Equal("adesso", up.LastContactText);
        var down = vm.Rows[1];
        Assert.Equal("Non collegato", down.StateText);
        Assert.Equal(UiColors.Unreachable, down.StatusColor);
        Assert.Equal("mai", down.LastContactText);
    }

    [Fact]
    public async Task Senza_account_dice_di_accedere_e_non_chiama_il_server()
    {
        int calls = 0;
        var vm = Make(() => { calls++; return Devices()(); }, signedIn: false);
        await vm.RefreshAsync();

        Assert.Equal(0, calls);
        Assert.Equal(VpnDashboardStatus.NotSignedIn, vm.Current.Status);
        Assert.Contains("Account e sincronizzazione", vm.ErrorText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Server_che_non_risponde_da_un_messaggio_chiaro_e_svuota_l_elenco()
    {
        bool down = false;
        var vm = Make(() => down
            ? throw new SyncUnreachableException("non risponde")
            : Devices(new VpnDashboardDevice("pc", "10.66.0.2/32", true, Now))());
        await vm.RefreshAsync();
        Assert.Single(vm.Rows);

        down = true;
        await vm.RefreshAsync();
        Assert.Empty(vm.Rows);
        Assert.Equal(VpnDashboardStatus.Unreachable, vm.Current.Status);
        Assert.Contains("Il server non risponde", vm.ErrorText);
        Assert.False(vm.Loading);
    }

    [Fact]
    public async Task Accesso_scaduto_e_vpn_non_attiva_hanno_il_loro_messaggio()
    {
        Exception error = new SyncServerException(HttpStatusCode.Unauthorized, "unauthorized", "no");
        var vm = Make(() => throw error);
        await vm.RefreshAsync();
        Assert.Equal(VpnDashboardStatus.Unauthorized, vm.Current.Status);

        error = new SyncServerException(HttpStatusCode.ServiceUnavailable, "vpn_disabled", "off");
        await vm.RefreshAsync();
        Assert.Equal(VpnDashboardStatus.Disabled, vm.Current.Status);

        error = new InvalidOperationException("Serve prima l'accesso all'account.");
        await vm.RefreshAsync();
        Assert.Equal(VpnDashboardStatus.Error, vm.Current.Status);
        Assert.NotNull(vm.ErrorText);
    }

    [Fact]
    public async Task Elenco_vuoto_non_e_un_errore()
    {
        var vm = Make(Devices());
        await vm.RefreshAsync();
        Assert.Null(vm.ErrorText);
        Assert.True(vm.HasInfo);
        Assert.Equal("Nessun PC registrato in marshmelloVPN.", vm.InfoText);
    }

    [Fact]
    public async Task Apri_desktop_remoto_sceglie_il_pc_e_chiude_la_finestra()
    {
        var vm = Make(Devices(new("pc1", "10.66.0.2/32", true, Now), new("pc2", "10.66.0.3/32", false, Now)));
        await vm.RefreshAsync();
        int closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Assert.False(vm.OpenCommand.CanExecute(null)); // niente selezione
        vm.OpenSelected();
        Assert.Null(vm.Chosen);

        vm.Selected = vm.Rows[1];
        Assert.True(vm.OpenCommand.CanExecute(null));
        vm.OpenCommand.Execute(null);
        Assert.Equal("pc2", vm.Chosen!.Name);
        Assert.Equal("10.66.0.3", vm.Chosen.Address);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Aggiornando_la_selezione_resta_sullo_stesso_pc()
    {
        var vm = Make(Devices(new("a", "10.66.0.2/32", true, Now), new("b", "10.66.0.3/32", false, Now)));
        await vm.RefreshAsync();
        vm.Selected = vm.Rows[1];
        await vm.RefreshAsync();
        Assert.Equal("b", vm.Selected?.Name);
    }

    [Fact]
    public async Task Dopo_la_chiusura_non_legge_piu()
    {
        int calls = 0;
        var vm = Make(() => { calls++; return Devices()(); });
        vm.Cancel();
        await vm.RefreshAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Il_menu_Strumenti_ha_Plancia_VPN_con_lo_stesso_testo_di_Windows()
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Menus/MainMenu.axaml"));
        Assert.Contains("Header=\"Pl_ancia marshmelloVPN…\" Command=\"{Binding VpnDashboardCommand}\"", axaml);
    }

    [Fact]
    public void Dalla_plancia_si_apre_il_desktop_remoto_riusando_la_connessione_salvata()
    {
        using var data = new TempData();
        var saved = Seed.Conn("Ufficio", "10.66.0.7");
        Seed.Write(data.Store, saved);
        var vm = Seed.Main(data);

        vm.OpenVpnDashboardPc("Ufficio (VPN)", "10.66.0.7");
        Assert.Single(vm.Tabs);
        Assert.Equal(saved.Id, vm.Tabs[0].Session.ConnectionId);

        // Stesso PC di nuovo: passa alla scheda già aperta.
        vm.OpenVpnDashboardPc("Ufficio (VPN)", "10.66.0.7");
        Assert.Single(vm.Tabs);
    }

    [Fact]
    public void Un_pc_non_salvato_apre_una_connessione_temporanea_sempre_la_stessa()
    {
        using var data = new TempData();
        var vm = Seed.Main(data);

        vm.OpenVpnDashboardPc("Nuovo", "10.66.0.9");
        vm.OpenVpnDashboardPc("Nuovo", "10.66.0.9");
        Assert.Single(vm.Tabs);
        Assert.Empty(vm.Document.Connections); // non entra nell'elenco dell'utente
    }
}
