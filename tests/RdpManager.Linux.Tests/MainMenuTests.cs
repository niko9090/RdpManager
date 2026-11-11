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
    public void Nessun_comando_e_piu_un_segnaposto()
    {
        // I blocchi A e B sono entrambi dentro: nessuna voce del menu deve restare legata a Placeholder().
        var root = Path.GetDirectoryName(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Menus/MainMenu.axaml"))!;
        var src = Path.GetFullPath(Path.Combine(root, ".."));
        var users = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("Placeholder()") && !File.ReadAllText(f).Contains("static RelayCommand Placeholder()"))
            .ToList();
        Assert.Empty(users);
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
