using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Difetto della 1.19.5 su Z2: il primo aggiornamento fallisce (clrjit.dll in uso da un altro processo), l'app viene
/// riaperta dalla copia di riserva e da lì ogni aggiornamento «fallisce» per sempre, perché la cartella da aggiornare era
/// presa dall'exe in esecuzione (la riserva) invece che dall'installazione vera.
/// </summary>
public sealed class UpdateFromBackupTests
{
    private const string Programs = @"C:\Users\tecnico\AppData\Local\Programs\RdpManager";
    private const string Runner = @"C:\Users\tecnico\AppData\Local\RdpManager\update-runner";

    [Fact]
    public void Dalla_cartella_installata_si_aggiorna_quella()
    {
        Assert.Equal(Programs, InstallLocation.ResolveInstallDir(Programs, Runner, null, Programs, _ => true));
    }

    [Theory]
    [InlineData(Runner + @"\riserva")]
    [InlineData(Runner + @"\riserva-20261126-085308")]
    public void Dalla_copia_di_riserva_si_aggiorna_la_cartella_registrata_dall_installer(string exeDir)
    {
        var registered = @"D:\Altrove\RdpManager\"; // Inno scrive InstallLocation con la barra finale
        Assert.Equal(@"D:\Altrove\RdpManager",
            InstallLocation.ResolveInstallDir(exeDir, Runner, registered, Programs, p => p.StartsWith(@"D:\Altrove", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dalla_copia_di_riserva_senza_registro_si_usa_la_cartella_predefinita()
    {
        Assert.Equal(Programs, InstallLocation.ResolveInstallDir(Runner + @"\riserva", Runner, null, Programs, _ => true));
    }

    [Fact]
    public void Una_cartella_registrata_senza_exe_o_dentro_la_riserva_non_si_usa()
    {
        Assert.Equal(Programs, InstallLocation.ResolveInstallDir(Runner + @"\riserva", Runner, Runner + @"\riserva", Programs, _ => true));
        Assert.Equal(Programs, InstallLocation.ResolveInstallDir(Runner + @"\riserva", Runner, @"D:\Vuota",
            Programs, p => !p.StartsWith(@"D:\", StringComparison.Ordinal)));
    }

    [Fact]
    public void Senza_installazione_trovata_resta_la_cartella_dell_exe()
    {
        Assert.Equal(Runner + @"\riserva", InstallLocation.ResolveInstallDir(Runner + @"\riserva", Runner, null, Programs, _ => false));
    }

    private sealed class Env : IUpdateEnvironment
    {
        public int BusyChecks;          // quante volte la cartella risulta ancora in uso
        public readonly List<string> Steps = [];

        public bool WaitForExit(int processId, TimeSpan timeout) => true;
        public bool FileExists(string path) => true;
        public bool VerifySha256(string path, string sha256) => true;
        public int? RunInstaller(string path, string arguments, TimeSpan timeout) { Steps.Add("install"); return 0; }
        public Version? FileVersion(string exePath) => new(1, 19, 5);
        public void Restore(string backup, string target) => Steps.Add("restore");
        public void Launch(string exePath, IReadOnlyList<string> arguments) => Steps.Add("launch");
        public bool StopVpnService(Action<string> log) { Steps.Add("stop-vpn"); return true; }
        public IReadOnlyList<string> ProcessesInFolder(string folder)
        {
            Steps.Add("check");
            return BusyChecks-- > 0 ? ["RdpManager (PID 24116, sessione 0, " + folder + @"\RdpManager.exe)"] : [];
        }
        public void Pause(TimeSpan time) => Steps.Add("pause");
    }

    private static UpdateJob Job() => new(1, @"C:\u\RdpManager-Setup-1.19.5.exe", new string('a', 64), Programs, Runner + @"\riserva",
        new Version(1, 19, 5), new Version(1, 19, 2), @"C:\u\a.log", @"C:\u\s.log");

    [Fact]
    public void L_installer_parte_solo_quando_nessun_processo_usa_piu_la_cartella_e_il_log_dice_chi_la_usava()
    {
        var env = new Env { BusyChecks = 3 };
        var log = new List<string>();
        var r = UpdateRunner.Run(Job(), env, log.Add);

        Assert.True(r.Succeeded);
        int install = env.Steps.IndexOf("install");
        Assert.Equal(3, env.Steps.Take(install).Count(s => s == "pause"));
        Assert.Equal("check", env.Steps[install - 1]); // l'ultimo controllo, libero, subito prima dell'installer
        Assert.Contains(log, l => l.Contains("PID 24116") && l.Contains("sessione 0"));
        Assert.Contains(log, l => l.Contains("libera"));
    }

    [Fact]
    public void Se_la_cartella_resta_in_uso_si_riprova_a_fermare_il_servizio_e_poi_si_installa_comunque()
    {
        var env = new Env { BusyChecks = int.MaxValue };
        var log = new List<string>();
        UpdateRunner.Run(Job(), env, log.Add);

        int install = env.Steps.IndexOf("install");
        Assert.True(install > 0);
        Assert.Equal((int)UpdateRunner.FolderFreeTimeout.TotalSeconds, env.Steps.Take(install).Count(s => s == "pause"));
        Assert.True(env.Steps.Take(install).Count(s => s == "stop-vpn") >= 3); // il primo e i nuovi tentativi durante l'attesa
        Assert.Contains(log, l => l.Contains("provo comunque"));
    }

    [Fact]
    public void Cartella_libera_nessuna_attesa()
    {
        var env = new Env();
        UpdateRunner.Run(Job(), env, _ => { });
        Assert.DoesNotContain("pause", env.Steps);
    }
}
