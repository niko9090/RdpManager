using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

public class VpnInstallAndAutoConnectTests
{
    private static Func<string, bool> Has(params string[] tools) => t => tools.Contains(t);

    private static VpnProfile CompleteProfile(bool auto = true)
    {
        var p = new VpnProfile { Address = "10.66.0.2/32", Endpoint = "vpn.example.org:51820", AutoConnect = auto };
        p.GenerateKeys();
        p.ServerPublicKey = WireGuardKeys.Generate().Item2;
        return p;
    }

    [Fact]
    public void Rileva_il_gestore_pacchetti_nell_ordine_apt_dnf_zypper_pacman()
    {
        Assert.Equal(LinuxPackageManager.Apt, LinuxWireGuardInstall.Detect(Has("apt-get", "dnf")));
        Assert.Equal(LinuxPackageManager.Dnf, LinuxWireGuardInstall.Detect(Has("dnf", "pacman")));
        Assert.Equal(LinuxPackageManager.Zypper, LinuxWireGuardInstall.Detect(Has("zypper")));
        Assert.Equal(LinuxPackageManager.Pacman, LinuxWireGuardInstall.Detect(Has("pacman")));
        Assert.Equal(LinuxPackageManager.None, LinuxWireGuardInstall.Detect(Has()));
    }

    [Fact]
    public void Comandi_di_installazione_con_pkexec()
    {
        Assert.Equal("pkexec dnf install -y wireguard-tools", LinuxWireGuardInstall.InstallCommand(LinuxPackageManager.Dnf)!.ToString());
        Assert.Equal("pkexec zypper --non-interactive install wireguard-tools", LinuxWireGuardInstall.InstallCommand(LinuxPackageManager.Zypper)!.ToString());
        Assert.Equal("pkexec pacman -S --noconfirm wireguard-tools", LinuxWireGuardInstall.InstallCommand(LinuxPackageManager.Pacman)!.ToString());
        var apt = LinuxWireGuardInstall.InstallCommand(LinuxPackageManager.Apt)!;
        Assert.Equal("pkexec", apt.FileName);
        Assert.Contains("apt-get install -y wireguard-tools", apt.Arguments[^1]);
        Assert.Null(LinuxWireGuardInstall.InstallCommand(LinuxPackageManager.None));
    }

    [Fact]
    public void Cerca_i_programmi_nel_PATH_e_nelle_cartelle_di_sistema()
    {
        Assert.True(LinuxTools.Exists("wg", "/opt/x:/usr/bin", f => f == "/opt/x/wg"));
        Assert.True(LinuxTools.Exists("wg-quick", "", f => f == "/usr/bin/wg-quick"));
        Assert.False(LinuxTools.Exists("wg", "/opt/x", _ => false));
        Assert.False(LinuxWireGuardInstall.ToolsPresent(Has("wg")));
        Assert.True(LinuxWireGuardInstall.ToolsPresent(Has("wg", "wg-quick")));
    }

    [Fact]
    public async Task Installazione_esegue_il_comando_e_riporta_l_esito()
    {
        var installed = false;
        var runner = new RecordingRunner((_, _) => { installed = true; return new ProcessResult(0, "", ""); });
        var installer = new LinuxWireGuardInstaller(runner, t => t is "pkexec" or "dnf" || (installed && t is "wg" or "wg-quick"));
        var steps = new List<string>();
        var r = await installer.InstallAsync(new Progress<string>(steps.Add));
        Assert.True(r.Success);
        Assert.Equal("WireGuard installato.", r.Message);
        Assert.Equal(["pkexec dnf install -y wireguard-tools"], runner.Calls);
    }

    [Fact]
    public async Task Installazione_senza_gestore_noto_o_annullata_spiega_il_perche_senza_eseguire_nulla()
    {
        var none = new RecordingRunner();
        var r1 = await new LinuxWireGuardInstaller(none, Has("pkexec")).InstallAsync();
        Assert.False(r1.Success);
        Assert.Contains("gestore pacchetti", r1.Message);
        Assert.Empty(none.Calls);

        var noPkexec = new RecordingRunner();
        var r2 = await new LinuxWireGuardInstaller(noPkexec, Has("apt-get")).InstallAsync();
        Assert.False(r2.Success);
        Assert.Contains("pkexec", r2.Message);
        Assert.Empty(noPkexec.Calls);

        var cancelled = new RecordingRunner((_, _) => new ProcessResult(126, "", ""));
        var r3 = await new LinuxWireGuardInstaller(cancelled, Has("pkexec", "pacman")).InstallAsync();
        Assert.False(r3.Success);
        Assert.Contains("annullata", r3.Message);
    }

    [Fact]
    public async Task Pannello_vpn_segnala_la_mancanza_e_dopo_l_installazione_collega_da_solo()
    {
        var path = Path.Combine(Path.GetTempPath(), "rdpm-vpn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            CompleteProfile().Save(path);
            var installed = false;
            var up = false;
            var runner = new RecordingRunner((file, args) =>
            {
                if (file == "pkexec" && args.Contains("install")) { installed = true; return new ProcessResult(0, "", ""); }
                if (file == "pkexec" && args.Contains("up")) { up = true; return new ProcessResult(0, "", ""); }
                if (file == "ip") return new ProcessResult(up ? 0 : 1, "", "");
                return new ProcessResult(0, "", "");
            });
            var installer = new LinuxWireGuardInstaller(runner, t => t is "pkexec" or "dnf" || (installed && t is "wg" or "wg-quick"));
            var vpn = new VpnViewModel(new LinuxWireGuard(runner, path + ".conf", LinuxVpnBackend.WgQuick), null,
                new NullShellUi(), new FakeDialogs(), path, installer: installer);
            Assert.True(vpn.ToolsMissing);
            Assert.Contains("non è installato", vpn.InstallNotice);

            await vpn.InstallWireGuardAsync();

            Assert.False(vpn.ToolsMissing);
            Assert.True(up);
            Assert.True(vpn.IsConnected);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".conf");
        }
    }

    [Fact]
    public void Connessione_automatica_attiva_di_default_e_solo_con_profilo_completo()
    {
        Assert.True(new VpnProfile().AutoConnect);
        Assert.True(VpnProvisioning.ShouldAutoConnect(CompleteProfile()));
        Assert.False(VpnProvisioning.ShouldAutoConnect(CompleteProfile(auto: false)));
        Assert.False(VpnProvisioning.ShouldAutoConnect(new VpnProfile())); // senza profilo non c'è nulla da collegare
    }

    [Fact]
    public void Vecchio_vpn_json_senza_il_campo_ha_la_connessione_automatica_attiva()
    {
        var path = Path.Combine(Path.GetTempPath(), "rdpm-vpn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Address\":\"10.66.0.2/32\"}");
            Assert.True(VpnProfile.Load(path).AutoConnect);
        }
        finally { File.Delete(path); }
    }

    private static (LinuxVpnAutoConnector Auto, LinuxWireGuard Vpn, RecordingRunner Runner, Func<bool> Up) Setup(VpnProfile profile, bool tools = true)
    {
        var up = false;
        var runner = new RecordingRunner((file, args) =>
        {
            if (file == "pkexec" && args.Contains("up")) { up = true; return new ProcessResult(0, "", ""); }
            if (file == "pkexec" && args.Contains("down")) { up = false; return new ProcessResult(0, "", ""); }
            if (file == "ip") return new ProcessResult(up ? 0 : 1, "", "");
            return new ProcessResult(0, "", "");
        });
        var vpn = new LinuxWireGuard(runner, Path.Combine(Path.GetTempPath(), "rdpm-auto-" + Guid.NewGuid().ToString("N") + ".conf"), LinuxVpnBackend.WgQuick);
        return (new LinuxVpnAutoConnector(vpn, () => profile, () => tools), vpn, runner, () => up);
    }

    [Fact]
    public async Task Auto_collega_all_avvio_e_si_ricollega_se_cade()
    {
        var (auto, _, runner, up) = Setup(CompleteProfile());
        Assert.Equal("VPN collegata.", await auto.TickAsync());
        Assert.True(up());
        Assert.Equal("", await auto.TickAsync()); // già accesa: niente da fare
        runner.Calls.Clear();

        // la VPN cade da sola
        await runner.RunAsync("pkexec", ["wg-quick", "down", "x"]);
        Assert.False(up());
        Assert.Equal("VPN ricollegata.", await auto.TickAsync());
        Assert.True(up());
    }

    [Fact]
    public async Task Auto_non_fa_nulla_se_disattivata_senza_profilo_o_spenta_a_mano()
    {
        var (off, _, offRunner, _) = Setup(CompleteProfile(auto: false));
        Assert.Equal("", await off.TickAsync());
        Assert.Empty(offRunner.Calls);

        var (empty, _, emptyRunner, _) = Setup(new VpnProfile());
        Assert.Equal("", await empty.TickAsync());
        Assert.Empty(emptyRunner.Calls);

        var (auto, vpn, _, up) = Setup(CompleteProfile());
        await auto.TickAsync();
        await vpn.DisconnectAsync();
        Assert.True(vpn.StoppedByUser);
        Assert.Equal("", await auto.TickAsync());
        Assert.False(up());
    }

    [Fact]
    public async Task Auto_senza_wireguard_avvisa_una_volta_sola_e_non_prova()
    {
        var (auto, _, runner, _) = Setup(CompleteProfile(), tools: false);
        Assert.Contains("Installa WireGuard", await auto.TickAsync());
        Assert.Equal("", await auto.TickAsync());
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("pkexec"));
    }

    [Fact]
    public async Task Auto_smette_dopo_tre_fallimenti_di_fila()
    {
        var profile = CompleteProfile();
        var runner = new RecordingRunner((file, args) => file == "pkexec" ? new ProcessResult(1, "", "errore") : new ProcessResult(1, "", ""));
        var vpn = new LinuxWireGuard(runner, Path.Combine(Path.GetTempPath(), "rdpm-auto-" + Guid.NewGuid().ToString("N") + ".conf"), LinuxVpnBackend.WgQuick);
        var auto = new LinuxVpnAutoConnector(vpn, () => profile, () => true);
        for (var i = 0; i < LinuxVpnAutoConnector.MaxFailures; i++)
            Assert.StartsWith("VPN non collegata", await auto.TickAsync());
        var before = runner.Calls.Count;
        Assert.Equal("", await auto.TickAsync());
        Assert.Equal(before, runner.Calls.Count);
    }
}
