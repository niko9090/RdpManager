using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Ruolo dell'installazione (k-151): lettura, scrittura, predefinito, file mancante o rovinato. Nessuna interfaccia.</summary>
public class AppRoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-role-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, AppRoleStore.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Senza_file_vale_il_predefinito_Desktop_remoto_e_VPN()
    {
        Assert.Equal(AppRole.DesktopAndVpn, AppRoleStore.Load(File_));
        Assert.False(AppRoleStore.IsChosen(File_));
    }

    [Theory]
    [InlineData(AppRole.VpnManager)]
    [InlineData(AppRole.DesktopAndVpn)]
    public void Scrittura_e_lettura_tornano_uguali(AppRole role)
    {
        Assert.True(AppRoleStore.Save(role, File_)); // crea anche la cartella
        Assert.Equal(role, AppRoleStore.Load(File_));
        Assert.True(AppRoleStore.IsChosen(File_));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ non è json")]
    [InlineData("{}")]
    [InlineData("{\"role\":\"Boh\"}")]
    [InlineData("{\"role\":3}")]
    [InlineData("[1,2]")]
    public void File_vuoto_o_rovinato_vale_il_predefinito_e_non_conta_come_scelto(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);
        Assert.Equal(AppRole.DesktopAndVpn, AppRoleStore.Load(File_));
        Assert.False(AppRoleStore.IsChosen(File_));
    }

    [Fact]
    public void Il_formato_scritto_dall_installer_si_legge()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{\"role\":\"VpnManager\"}");
        Assert.Equal(AppRole.VpnManager, AppRoleStore.Load(File_));
        File.WriteAllText(File_, "{\"role\":\"DesktopAndVpn\"}");
        Assert.Equal(AppRole.DesktopAndVpn, AppRoleStore.Load(File_));
    }

    [Fact]
    public void Riscrivere_sostituisce_il_ruolo_e_non_lascia_file_temporanei()
    {
        AppRoleStore.Save(AppRole.VpnManager, File_);
        AppRoleStore.Save(AppRole.DesktopAndVpn, File_);
        Assert.Equal(AppRole.DesktopAndVpn, AppRoleStore.Load(File_));
        Assert.Equal([File_], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Se_il_disco_non_permette_di_scrivere_Save_dice_falso_senza_eccezioni()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        // «file» è un file, non una cartella: il percorso sotto non si può creare.
        Assert.False(AppRoleStore.Save(AppRole.VpnManager, Path.Combine(blocker, AppRoleStore.FileName)));
    }

    [Theory]
    [InlineData("vpn", true, AppRole.VpnManager)]
    [InlineData(" VpnManager ", true, AppRole.VpnManager)]
    [InlineData("desktop", true, AppRole.DesktopAndVpn)]
    [InlineData("rdp", true, AppRole.DesktopAndVpn)]
    [InlineData("altro", false, AppRole.DesktopAndVpn)]
    [InlineData(null, false, AppRole.DesktopAndVpn)]
    public void I_nomi_dei_parametri_si_riconoscono(string? name, bool ok, AppRole expected)
    {
        Assert.Equal(ok, AppRoleStore.TryParseName(name, out var role));
        Assert.Equal(expected, role);
    }

    [Fact]
    public void I_titoli_sono_quelli_dell_installer()
    {
        Assert.Equal("Desktop remoto + VPN", AppRoleStore.Describe(AppRole.DesktopAndVpn));
        Assert.Equal("Gestore VPN", AppRoleStore.Describe(AppRole.VpnManager));
    }

    [Fact]
    public void L_installer_ha_la_pagina_del_ruolo_e_non_scrive_nelle_installazioni_silenziose_senza_ROLE()
    {
        var iss = File.ReadAllText(RepoFile("installer/RdpManager.iss"));
        Assert.Contains("CreateInputOptionPage", iss);
        Assert.Contains("Desktop remoto + VPN", iss);
        Assert.Contains("Gestore VPN", iss);
        Assert.Contains("role.json", iss);
        Assert.Contains("not WizardSilent", iss); // silenziosa senza /ROLE: il file resta com'è
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "installer", "RdpManager.iss")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repository non trovato"), relative);
    }
}
