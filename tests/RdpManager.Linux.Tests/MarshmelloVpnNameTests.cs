using RdpManager.Core;

namespace RdpManager.Linux.Tests;

/// <summary>Su Linux il prodotto VPN si chiama «marshmelloVPN» (tutto minuscolo tranne «VPN») in menu, finestre e ruoli.</summary>
public class MarshmelloVpnNameTests
{
    [Fact]
    public void Menu_e_finestre_di_Linux_si_chiamano_marshmelloVPN()
    {
        var menu = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Menus/MainMenu.axaml"));
        Assert.Contains("Header=\"_marshmelloVPN (WireGuard)…\"", menu);
        Assert.Contains("Header=\"_Plancia\"", menu);
        Assert.Contains("Title=\"marshmelloVPN (WireGuard)\"",
            File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Dialogs/VpnWindow.axaml")));
        Assert.Contains("\"Plancia marshmelloVPN\"",
            File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Dialogs/VpnDashboardWindow.cs")));
    }

    [Fact]
    public void I_ruoli_e_la_nota_delle_impostazioni_usano_il_nome_nuovo()
    {
        Assert.Equal("Gestore marshmelloVPN", AppRoleStore.Describe(AppRole.VpnManager));
        Assert.Equal("Desktop remoto + marshmelloVPN", AppRoleStore.Describe(AppRole.DesktopAndVpn));
        var note = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/ViewModels/Settings/AvvioPageViewModel.cs"));
        Assert.Contains("«Gestore marshmelloVPN»", note);
    }
}
