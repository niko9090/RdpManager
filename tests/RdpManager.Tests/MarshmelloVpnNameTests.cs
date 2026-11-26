using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Il nome del prodotto VPN che l'utente vede è «marshmelloVPN» (tutto minuscolo tranne «VPN»). Gli identificatori
/// (servizi, chiavi, percorsi, AppId) restano quelli di prima: cambiarli romperebbe aggiornamenti e installazioni.
/// </summary>
public class MarshmelloVpnNameTests
{
    private const string Name = "marshmelloVPN";

    [Fact]
    public void Lo_stato_della_ricerca_reti_usa_il_nome_nuovo()
    {
        var off = new AdapterCard("RdpManager", "WireGuard Tunnel", NetworkKind.WireGuard, false, null, 0);
        var headline = NetworkOverview.Build([], [off]).Headline;
        Assert.StartsWith(Name + " NON COLLEGATA", headline);
        Assert.Equal(Name, NetworkOverview.VpnLabel);
        // il nome dell'interfaccia WireGuard non cambia
        Assert.Contains("«RdpManager»", headline);
    }

    [Fact]
    public void Menu_e_finestre_di_Windows_si_chiamano_marshmelloVPN()
    {
        var menu = File.ReadAllText(RepoFile("src/RdpManager/UI/MainForm.cs"));
        Assert.Contains("\"&marshmelloVPN (WireGuard)…\"", menu);
        Assert.Contains("\"&Plancia\"", menu);
        Assert.Contains("Text = \"marshmelloVPN (WireGuard)\";", File.ReadAllText(RepoFile("src/RdpManager/UI/VpnForm.cs")));
        Assert.Contains("\"Plancia marshmelloVPN\"", File.ReadAllText(RepoFile("src/RdpManager/UI/VpnDashboardForm.cs")));
    }

    [Fact]
    public void Gli_installer_mostrano_il_nome_nuovo_ma_tengono_AppId_e_nomi_dei_file()
    {
        var vpn = File.ReadAllText(RepoFile("installer/RdpManagerVpn.iss"));
        Assert.Contains("#define AppName \"marshmelloVPN\"", vpn);
        Assert.Contains("AppId={{3B1E6C52-8A4D-4F0B-9D27-5C61A7E0F4B9}", vpn);
        Assert.Contains("OutputBaseFilename=RdpManagerVPN-Setup", vpn);
        Assert.Contains("ValueName: \"RdpManagerVPN\"", vpn);
        var main = File.ReadAllText(RepoFile("installer/RdpManager.iss"));
        Assert.Contains("Gestore marshmelloVPN", main);
    }

    [Fact]
    public void L_installer_VPN_toglie_la_vecchia_voce_del_menu_Start()
    {
        var vpn = File.ReadAllText(RepoFile("installer/RdpManagerVpn.iss"));
        var start = vpn.IndexOf("[InstallDelete]", StringComparison.Ordinal);
        Assert.True(start >= 0, "manca la sezione [InstallDelete]");
        var end = vpn.IndexOf("\n[", start + 1, StringComparison.Ordinal);
        var section = vpn[start..(end < 0 ? vpn.Length : end)];
        Assert.Contains("Type: files; Name: \"{autoprograms}\\RdpManager VPN.lnk\"", section);
        Assert.Contains("Type: filesandordirs; Name: \"{autoprograms}\\RdpManager VPN\"", section);
        // la voce nuova resta
        Assert.Contains("Name: \"{autoprograms}\\{#AppName}\"; Filename", vpn);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "installer", "RdpManager.iss")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repository non trovato"), relative);
    }
}
