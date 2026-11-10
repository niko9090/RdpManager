using System.Text.RegularExpressions;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

public class MainMenuTests
{
    private static readonly string Axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Menus/MainMenu.axaml"));

    private static List<string> MenuCommands() =>
        Regex.Matches(Axaml, @"Command=""\{Binding (\w+)\}""").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Il_menu_ha_i_menu_del_programma_Windows()
    {
        var headers = Regex.Matches(Axaml, @"^      <MenuItem Header=""([^""]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        Assert.Equal(["_File", "_Connessioni", "_Sessione", "_Visualizza", "Spazi di la_voro", "S_trumenti", "A_iuto"], headers);
    }

    [Fact]
    public void Ogni_voce_e_legata_a_un_comando_esistente()
    {
        using var data = new TempData();
        var vm = Seed.Main(data);
        var type = typeof(MainWindowViewModel);
        foreach (var name in MenuCommands().Distinct())
        {
            var prop = type.GetProperty(name);
            Assert.True(prop is not null && typeof(RelayCommand).IsAssignableFrom(prop.PropertyType), $"comando mancante: {name}");
            Assert.NotNull(prop!.GetValue(vm));
        }
    }

    [Fact]
    public void I_comandi_segnaposto_dei_blocchi_sono_disabilitati()
    {
        using var data = new TempData();
        var vm = Seed.Main(data);
        string[] placeholders = [nameof(vm.ImportRdpCommand), nameof(vm.HistoryCommand), nameof(vm.AboutCommand),
            nameof(vm.VpnCommand), nameof(vm.FullScreenCommand), nameof(vm.CheckUpdatesCommand)];
        foreach (var name in placeholders)
            Assert.False(((RelayCommand)typeof(MainWindowViewModel).GetProperty(name)!.GetValue(vm)!).CanExecute(null), name);
    }

    [Fact]
    public void Le_voci_gia_funzionanti_sono_abilitate()
    {
        using var data = new TempData();
        var vm = Seed.Main(data);
        Assert.True(vm.NewConnectionCommand.CanExecute(null));
        Assert.True(vm.SettingsCommand.CanExecute(null));
        Assert.True(vm.ExitCommand.CanExecute(null));
        Assert.False(vm.CloseCurrentTabCommand.CanExecute(null));
        bool exit = false;
        vm.ExitRequested += (_, _) => exit = true;
        vm.ExitCommand.Execute(null);
        Assert.True(exit);
    }
}
