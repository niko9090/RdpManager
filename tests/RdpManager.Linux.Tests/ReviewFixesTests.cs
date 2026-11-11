using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Settings;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>Correzioni dei difetti trovati dalla revisione del blocco B.</summary>
public class SyncSafetyTests
{
    private static void SignIn(MainWindowViewModel vm)
    {
        vm.SyncService.State.ServerUrl = "https://sync.example.invalid/";
        vm.SyncService.State.Username = "utente";
        vm.SyncService.State.ProtectedPassword = "x";
        Assert.True(vm.SyncService.SignedIn);
    }

    [Fact]
    public async Task File_dati_illeggibile_non_sincronizza_per_non_svuotare_il_server()
    {
        using var data = new TempData();
        File.WriteAllText(data.FilePath, "non json {{{");
        var vm = Seed.Main(data);
        vm.Ui = new NullShellUi();
        vm.VpnProfilePath = Path.Combine(Path.GetTempPath(), "rdpm-vpn-" + Guid.NewGuid().ToString("N") + ".json");
        SignIn(vm);

        var outcome = await vm.RunSyncAsync(manual: true);

        Assert.Equal(SyncStatus.Error, outcome.Status);
        Assert.Contains("non sincronizzo", outcome.Message);
        Assert.Null(vm.SyncService.State.Base); // la base non è avanzata
        vm.ShutdownBlockB();
    }

    [Fact]
    public void Salvataggio_locale_fallito_fa_fallire_la_sincronizzazione()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Uno", "a.example"));
        var vm = Seed.Main(data);
        vm.Ui = new NullShellUi();
        // Il file dei dati diventa una cartella: il salvataggio non può riuscire.
        File.Delete(data.FilePath);
        Directory.CreateDirectory(data.FilePath);

        var access = vm.SyncDocumentAccess;
        Assert.Throws<IOException>(() => access.Apply(doc => { doc.Connections.Clear(); return true; }));
        vm.ShutdownBlockB();
    }
}

public class VpnServiceTests
{
    private static WireGuardConfig Config()
    {
        var profile = new VpnProfile { Address = "10.66.0.2/32", Endpoint = "vpn.example.com:51820", PcName = "Prova" };
        profile.GenerateKeys();
        profile.ServerPublicKey = profile.PublicKey!;
        return profile.ToConfig();
    }

    [Fact]
    public async Task Richieste_insieme_accendono_la_VPN_una_volta_sola()
    {
        var conf = Path.Combine(Path.GetTempPath(), "rdpm-" + Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            var up = false;
            var ups = 0;
            var runner = new AsyncRunner(async (file, args) =>
            {
                await Task.Delay(20);
                if (file == "pkexec" && args.Contains("up"))
                {
                    Interlocked.Increment(ref ups);
                    up = true;
                    return new ProcessResult(0, "", "");
                }
                return file == "ip" ? new ProcessResult(up ? 0 : 1, "", "") : new ProcessResult(0, "", "");
            });
            var vpn = new LinuxWireGuard(runner, conf, LinuxVpnBackend.WgQuick);
            var config = Config();

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => vpn.EnsureConnectedAsync(config)));

            Assert.All(results, r => Assert.True(r.Success));
            Assert.Equal(1, ups);
        }
        finally
        {
            File.Delete(conf);
        }
    }

    [Fact]
    public void Il_servizio_VPN_del_programma_e_unico()
    {
        Assert.Same(LinuxPlatformInit.Vpn, LinuxPlatformInit.Vpn);
    }

    [Fact]
    public async Task Con_NetworkManager_il_file_con_la_chiave_si_cancella_dopo_l_import()
    {
        var conf = Path.Combine(Path.GetTempPath(), "rdpm-" + Guid.NewGuid().ToString("N") + ".conf");
        var runner = new RecordingRunner();
        var vpn = new LinuxWireGuard(runner, conf, LinuxVpnBackend.NetworkManager);

        var result = await vpn.ConnectAsync(Config());

        Assert.True(result.Success);
        Assert.Contains(runner.Calls, c => c.StartsWith("nmcli connection import"));
        Assert.False(File.Exists(conf));
    }

    [Fact]
    public void WriteConf_porta_a_0600_anche_un_file_esistente()
    {
        if (OperatingSystem.IsWindows())
            return;
        var conf = Path.Combine(Path.GetTempPath(), "rdpm-" + Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            File.WriteAllText(conf, "vecchio");
            File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            new LinuxWireGuard(new RecordingRunner(), conf, LinuxVpnBackend.WgQuick).WriteConf(Config());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(conf));
        }
        finally
        {
            File.Delete(conf);
        }
    }

    [Fact]
    public async Task Annullando_il_processo_esterno_viene_chiuso_e_l_annullamento_rilanciato()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SystemProcessRunner().RunAsync("sleep", ["30"], null, cts.Token));
    }

    private sealed class AsyncRunner(Func<string, IReadOnlyList<string>, Task<ProcessResult>> answer) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default) =>
            answer(fileName, arguments);
    }
}

[Collection("Globali")]
public class SettingsFixesTests
{
    [Fact]
    public void Pagina_aggiornamenti_ignora_il_valore_nullo()
    {
        var page = new AggiornamentiPageViewModel(new AppSettings());
        var before = page.Update;
        page.Update = null!;
        Assert.Same(before, page.Update);
    }

    [Fact]
    public void Pagina_vassoio_notifica_sempre_i_campi_dipendenti()
    {
        var page = new VassoioPageViewModel(new AppSettings { ShowTrayIcon = false });
        var changed = new List<string>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        page.CloseToTray = true; // senza icona resta spento, ma la vista va comunque riallineata
        Assert.False(page.CloseToTray);
        Assert.Contains(nameof(VassoioPageViewModel.CloseToTray), changed);
        changed.Clear();
        page.ShowTrayIcon = true;
        Assert.Contains(nameof(VassoioPageViewModel.CloseToTrayEnabled), changed);
    }
}
