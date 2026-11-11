namespace RdpManager.Tests;

/// <summary>Associazione del file .rdpmconfig nell'installer di Windows e nei pacchetti Linux (controlli sui file di costruzione).</summary>
public class ConfigFileAssociationTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void L_installer_associa_l_estensione_per_l_utente_senza_amministratore()
    {
        var iss = Read("installer", "RdpManager.iss");
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains("ChangesAssociations=yes", iss);
        Assert.Contains(@"Subkey: ""Software\Classes\.rdpmconfig""", iss);
        Assert.Contains(@"shell\open\command", iss);
        Assert.Contains("\"\"\"{app}\\{#AppExe}\"\" \"\"%1\"\"\"", iss); // "…\RdpManager.exe" "%1"
        Assert.DoesNotContain("Root: HKLM", iss);
        Assert.DoesNotContain("Root: HKCR", iss);
    }

    [Fact]
    public void I_pacchetti_Linux_dichiarano_il_tipo_MIME_e_lo_installano()
    {
        Assert.Contains("MimeType=application/x-rdpmconfig;", Read("packaging", "linux", "rdpmanager.desktop"));
        var xml = Read("packaging", "linux", "rdpmanager-rdpmconfig.xml");
        Assert.Contains("application/x-rdpmconfig", xml);
        Assert.Contains("*.rdpmconfig", xml);
        var script = Read("packaging", "linux", "build-packages.sh");
        Assert.Contains("rdpmanager-rdpmconfig.xml", script);
        Assert.Contains("/usr/share/mime/packages/rdpmanager-rdpmconfig.xml", script); // elenco file dell'rpm
        Assert.Contains("$APPDIR/usr/share/mime/packages", script); // AppImage
    }
}
