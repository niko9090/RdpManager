using System.Net;
using RdpManager.Core;
using RdpManager.Linux.Features;

namespace RdpManager.Linux.Tests;

/// <summary>«Invia file…» della plancia VPN su Linux: solo ViewModel con funzioni finte, nessuna finestra.</summary>
public class VpnFileTransferViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static Task<IReadOnlyList<VpnDashboardDevice>> TwoPcs(CancellationToken _) =>
        Task.FromResult<IReadOnlyList<VpnDashboardDevice>>(
            [new("casa", "10.66.0.2/32", true, Now), new("ufficio", "10.66.0.3/32", true, Now)]);

    private static async Task<VpnDashboardViewModel> Make(Func<string, string, CancellationToken, Task>? send,
        Func<string, Task<string?>>? pick)
    {
        var vm = new VpnDashboardViewModel(() => true, TwoPcs, () => Now, send, pick);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Senza_funzioni_di_invio_il_pulsante_non_c_e()
    {
        var vm = await Make(null, null);
        vm.Selected = vm.Rows[0];

        Assert.False(vm.CanSendFeature);
        Assert.False(vm.SendFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Il_pulsante_si_abilita_con_un_pc_selezionato()
    {
        var vm = await Make((_, _, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));

        Assert.True(vm.CanSendFeature);
        Assert.False(vm.SendFileCommand.CanExecute(null));
        vm.Selected = vm.Rows[0];
        Assert.True(vm.SendFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Invio_riuscito_manda_il_file_al_pc_scelto_e_lo_dice()
    {
        (string To, string Path)? sent = null;
        string? pickedFor = null;
        var vm = await Make((to, path, _) => { sent = (to, path); return Task.CompletedTask; },
            to => { pickedFor = to; return Task.FromResult<string?>("/home/mario/relazione.pdf"); });
        vm.Selected = vm.Rows.Single(r => r.Name == "ufficio");

        await vm.SendFileAsync();

        Assert.Equal("ufficio", pickedFor);
        Assert.Equal(("ufficio", "/home/mario/relazione.pdf"), sent);
        Assert.Contains("«relazione.pdf» inviato a ufficio", vm.SendInfoText);
        Assert.Null(vm.SendErrorText);
        Assert.True(vm.SendFileCommand.CanExecute(null)); // di nuovo disponibile
    }

    [Fact]
    public async Task Selezione_annullata_non_invia_niente()
    {
        int calls = 0;
        var vm = await Make((_, _, _) => { calls++; return Task.CompletedTask; }, _ => Task.FromResult<string?>(null));
        vm.Selected = vm.Rows[0];

        await vm.SendFileAsync();

        Assert.Equal(0, calls);
        Assert.Equal("", vm.SendInfoText);
    }

    [Fact]
    public async Task Rifiuto_del_server_mostra_il_motivo_in_italiano()
    {
        var vm = await Make(
            (_, _, _) => throw new SyncServerException(HttpStatusCode.RequestEntityTooLarge, "too_large", "Il file supera il limite di 100 MB."),
            _ => Task.FromResult<string?>("/x/grosso.iso"));
        vm.Selected = vm.Rows[0];

        await vm.SendFileAsync();

        Assert.Equal("Il file supera il limite di 100 MB.", vm.SendErrorText);
        Assert.Equal("", vm.SendInfoText);
        Assert.True(vm.SendFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Server_giu_e_file_sparito_hanno_messaggi_chiari()
    {
        Exception error = new SyncUnreachableException("giù");
        var vm = await Make((_, _, _) => throw error, _ => Task.FromResult<string?>("/x/a.txt"));
        vm.Selected = vm.Rows[0];

        await vm.SendFileAsync();
        Assert.Contains("Il server non risponde", vm.SendErrorText);

        error = new FileNotFoundException("a.txt");
        await vm.SendFileAsync();
        Assert.Equal("Il file scelto non esiste più.", vm.SendErrorText);
    }

    [Fact]
    public async Task Due_invii_insieme_non_partono_in_parallelo()
    {
        var gate = new TaskCompletionSource();
        int calls = 0;
        var vm = await Make((_, _, _) => { calls++; return gate.Task; }, _ => Task.FromResult<string?>("/x/a.txt"));
        vm.Selected = vm.Rows[0];

        var first = vm.SendFileAsync();
        Assert.False(vm.SendFileCommand.CanExecute(null));
        await vm.SendFileAsync(); // ignorato: il primo è ancora in corso
        Assert.Equal(1, calls);
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.SendFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Chiusa_la_finestra_l_invio_in_corso_viene_annullato_senza_errore()
    {
        var vm = await Make(async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct), _ => Task.FromResult<string?>("/x/a.txt"));
        vm.Selected = vm.Rows[0];

        var sending = vm.SendFileAsync();
        vm.Cancel();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(vm.SendErrorText);
        Assert.Equal("", vm.SendInfoText);
    }

    [Fact]
    public async Task Avviso_in_arrivo_nell_interfaccia_nulla_registra_la_richiesta_e_risponde()
    {
        var ui = new NullShellUi { IncomingFileAnswer = new IncomingFileDecision(IncomingFileChoice.Accept, "/tmp/in") };
        var file = new IncomingFile("id1", "casa", "a.txt", 10, Now);

        var decision = await ui.AskIncomingFileAsync(file);

        Assert.Equal(IncomingFileChoice.Accept, decision.Choice);
        Assert.Equal("/tmp/in", decision.Folder);
        Assert.Same(file, Assert.Single(ui.IncomingFilePrompts));
    }
}
