using System.Drawing;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>Sezione «Schermi» della modifica connessione e argomenti FreeRDP per le tre modalità (k-184).</summary>
[Collection("Globali")]
public class ScreensSectionTests : IDisposable
{
    private static readonly FreeRdpClient V3 = new("xfreerdp3", true, false);
    private static readonly FreeRdpClient V2 = new("xfreerdp", false, false);
    private readonly Func<IReadOnlyList<ScreenInfo>> _oldSource = ScreenProvider.Source;

    private static ScreenInfo Screen(string name, int x, bool primary = false) =>
        new(name, new Rectangle(x, 0, 1920, 1080), new Rectangle(x, 0, 1920, 1040), primary);

    private static readonly ScreenInfo[] Three = [Screen("DP-1", 0, true), Screen("HDMI-1", 1920), Screen("DP-2", 3840)];

    public ScreensSectionTests() => ScreenProvider.Source = () => Three;

    public void Dispose() => ScreenProvider.Source = _oldSource;

    private static RdpConnection Conn(MonitorMode mode, params int[] selected) =>
        new() { Host = "pc", MonitorMode = mode, SelectedMonitors = selected };

    private static IReadOnlyList<string> Args(RdpConnection c, FreeRdpClient client, IReadOnlyList<ScreenInfo>? screens = null, long? parent = null) =>
        FreeRdpCommandBuilder.Build(c, client, new FreeRdpLaunchContext { Screens = screens ?? Three, ParentWindow = parent }).Arguments;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Un_solo_schermo_non_ha_opzioni_multimon(bool v3)
    {
        var args = Args(Conn(MonitorMode.Single), v3 ? V3 : V2);
        Assert.DoesNotContain("/multimon", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/monitors:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tutti_gli_schermi_e_schermo_intero_multimon(bool v3)
    {
        var args = Args(Conn(MonitorMode.All), v3 ? V3 : V2, parent: 5);
        Assert.Contains("/f", args);
        Assert.Contains("/multimon", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/monitors:", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("/parent-window", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Schermi_scelti_hanno_monitors_e_non_si_incorporano(bool v3)
    {
        var c = Conn(MonitorMode.Selected, 0, 2);
        var args = Args(c, v3 ? V3 : V2, parent: 5);
        Assert.Contains("/f", args);
        Assert.Contains("/multimon", args);
        Assert.Contains("/monitors:0,2", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/parent-window", StringComparison.Ordinal));
        Assert.False(FreeRdpCommandBuilder.WillEmbed(c, v3 ? V3 : V2, new FreeRdpLaunchContext { ParentWindow = 5 }));
    }

    [Fact]
    public void Scelti_tutti_quelli_collegati_vale_multimon_semplice()
    {
        var args = Args(Conn(MonitorMode.Selected, 0, 1, 2), V3);
        Assert.Contains("/multimon", args);
        Assert.DoesNotContain(args, a => a.StartsWith("/monitors:", StringComparison.Ordinal));
    }

    [Fact]
    public void Un_solo_schermo_scelto_usa_monitors_senza_multimon()
    {
        var args = Args(Conn(MonitorMode.Selected, 1), V3);
        Assert.Contains("/f", args);
        Assert.Contains("/monitors:1", args);
        Assert.DoesNotContain("/multimon", args);
    }

    [Fact]
    public void Schermo_scelto_non_piu_collegato_usa_gli_altri_o_tutti()
    {
        // L'indice 5 non esiste più: restano 0 e 2.
        var some = Args(Conn(MonitorMode.Selected, 0, 2, 5), V3);
        Assert.Contains("/monitors:0,2", some);
        // Nessuno dei due c'è più: ripiego su tutti gli schermi presenti.
        var none = Args(Conn(MonitorMode.Selected, 7, 8), V3);
        Assert.Contains("/multimon", none);
        Assert.DoesNotContain(none, a => a.StartsWith("/monitors:", StringComparison.Ordinal));
        // Per nome di dispositivo: HDMI-1 c'è, VGA-1 no.
        var c = Conn(MonitorMode.Selected, 0, 1);
        c.SelectedMonitorDevices = ["VGA-1", "HDMI-1"];
        Assert.Contains("/monitors:1", Args(c, V3));
    }

    [Fact]
    public void Senza_schermi_rilevati_si_usano_gli_indici_salvati()
    {
        Assert.Contains("/multimon", Args(Conn(MonitorMode.All), V3, []));
        var args = Args(Conn(MonitorMode.Selected, 0, 1), V3, []);
        Assert.Contains("/monitors:0,1", args);
        Assert.Contains("/multimon", args);
    }

    [Fact]
    public void Il_modulo_carica_le_tre_modalita()
    {
        var doc = new ConnectionsDocument();
        var single = new ConnectionEditViewModel(doc, Conn(MonitorMode.Single), false);
        Assert.True(single.SingleScreen);
        Assert.False(single.AllScreens);
        var all = new ConnectionEditViewModel(doc, Conn(MonitorMode.All), false);
        Assert.True(all.AllScreens);
        var chosen = new ConnectionEditViewModel(doc, Conn(MonitorMode.Selected, 0, 2), false);
        Assert.True(chosen.ChosenScreens);
        Assert.Equal(3, chosen.Screens.Count);
        Assert.Equal([true, false, true], chosen.Screens.Select(s => s.IsChecked));
        Assert.Contains("DP-1", chosen.Screens[0].Text);
        Assert.Contains("principale", chosen.Screens[0].Text);
        Assert.Null(chosen.ScreensNote);
    }

    [Fact]
    public void Il_modulo_salva_le_tre_modalita()
    {
        var doc = new ConnectionsDocument();
        var c = Conn(MonitorMode.Single);
        var vm = new ConnectionEditViewModel(doc, c, false);
        vm.ChosenScreens = true;
        Assert.False(vm.TryCommit()); // nessuno spuntato
        Assert.NotNull(vm.Error);
        vm.Screens[0].IsChecked = true;
        vm.Screens[2].IsChecked = true;
        Assert.True(vm.TryCommit());
        Assert.Equal(MonitorMode.Selected, c.MonitorMode);
        Assert.Equal([0, 2], c.SelectedMonitors);
        Assert.Equal(["DP-1", "DP-2"], c.SelectedMonitorDevices);

        vm.AllScreens = true;
        Assert.True(vm.TryCommit());
        Assert.True(c.UseAllMonitors);
        Assert.Equal([0, 2], c.SelectedMonitors); // la scelta resta ricordata

        vm.SingleScreen = true;
        Assert.True(vm.TryCommit());
        Assert.Equal(MonitorMode.Single, c.MonitorMode);
    }

    [Fact]
    public void Uno_schermo_scelto_e_scollegato_resta_spuntato_nell_elenco()
    {
        var c = Conn(MonitorMode.Selected, 0, 5);
        c.SelectedMonitorDevices = ["DP-1", "VGA-9"];
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), c, false);
        Assert.Equal(4, vm.Screens.Count);
        var gone = vm.Screens[3];
        Assert.False(gone.Connected);
        Assert.True(gone.IsChecked);
        Assert.Contains("non collegato", gone.Text);
        Assert.True(vm.TryCommit());
        Assert.Equal([0, 5], c.SelectedMonitors);
    }

    [Fact]
    public void Senza_schermi_rilevati_l_elenco_ha_una_nota()
    {
        ScreenProvider.Source = () => [];
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), Conn(MonitorMode.Single), false);
        Assert.Empty(vm.Screens);
        Assert.NotNull(vm.ScreensNote);
    }
}
