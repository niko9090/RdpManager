using RdpManager.Core;
using RdpManager.Vpn;

namespace RdpManager.Tests;

/// <summary>
/// Prove dell'aggiornamento con la VPN accesa (k-87): il servizio «RdpManagerVpn» è RdpManager.exe della cartella installata,
/// quindi tiene in uso i file che l'installer deve sostituire; se non lo si ferma prima l'installazione si annulla e si
/// torna alla versione precedente.
/// </summary>
public sealed class UpdateVpnServiceTests
{
    private sealed class FakeService(bool present, TunnelState state, bool stopDenied = false, bool stuck = false) : IVpnServiceControl
    {
        public TunnelState State = state;
        public readonly List<string> Calls = [];

        public bool Exists(string serviceName) => present;
        public TunnelState Query(string serviceName) => State;
        public void Start(string serviceName) => Calls.Add("start");
        public void Stop(string serviceName)
        {
            Calls.Add("stop");
            if (stopDenied)
                throw new InvalidOperationException("accesso negato");
            State = stuck ? TunnelState.InArresto : TunnelState.Spento;
        }
    }

    [Fact]
    public void Update_RunningVpnService_IsStoppedAndReportedAsStopped()
    {
        var svc = new FakeService(present: true, TunnelState.Acceso);
        var log = new List<string>();
        Assert.True(VpnServiceStopper.StopIfRunning(svc, log.Add, TimeSpan.FromSeconds(1), _ => { }));
        Assert.Equal(["stop"], svc.Calls);
        Assert.Equal(TunnelState.Spento, svc.State);
        Assert.Contains(log, l => l.Contains("fermato"));
    }

    [Theory]
    [InlineData(false, TunnelState.Spento)] // servizio mai installato
    [InlineData(true, TunnelState.Spento)] // già fermo
    public void Update_NoRunningVpnService_NothingIsTouched(bool present, TunnelState state)
    {
        var svc = new FakeService(present, state);
        Assert.True(VpnServiceStopper.StopIfRunning(svc, _ => { }, TimeSpan.FromSeconds(1), _ => { }));
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void Update_VpnServiceAlreadyStopping_IsWaitedForWithoutAskingAgain()
    {
        var svc = new FakeService(present: true, TunnelState.InArresto);
        int polls = 0;
        Assert.True(VpnServiceStopper.StopIfRunning(svc, _ => { }, TimeSpan.FromSeconds(5), _ => { if (++polls == 3) svc.State = TunnelState.Spento; }));
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void Update_VpnServiceThatDoesNotStop_GivesUpAfterTheTimeout()
    {
        var svc = new FakeService(present: true, TunnelState.Acceso, stuck: true);
        var log = new List<string>();
        Assert.False(VpnServiceStopper.StopIfRunning(svc, log.Add, TimeSpan.FromSeconds(1), _ => { }));
        Assert.Contains(log, l => l.Contains("non si è fermato"));
    }

    [Fact]
    public void Update_StopDenied_ReportsFailureInsteadOfCrashing()
    {
        var svc = new FakeService(present: true, TunnelState.Acceso, stopDenied: true);
        var log = new List<string>();
        Assert.False(VpnServiceStopper.StopIfRunning(svc, log.Add, TimeSpan.FromSeconds(1), _ => { }));
        Assert.Contains(log, l => l.Contains("accesso negato"));
    }

    private sealed class RunnerEnv : IUpdateEnvironment
    {
        public readonly List<string> Steps = [];
        public bool VpnStops = true;

        public bool WaitForExit(int processId, TimeSpan timeout) { Steps.Add("wait"); return true; }
        public bool FileExists(string path) => true;
        public bool VerifySha256(string path, string sha256) { Steps.Add("verify"); return true; }
        public int? RunInstaller(string path, string arguments, TimeSpan timeout) { Steps.Add("install"); return 0; }
        public Version? FileVersion(string exePath) => new(1, 14, 1);
        public void Restore(string backup, string target) => Steps.Add("restore");
        public void Launch(string exePath, IReadOnlyList<string> arguments) => Steps.Add("launch");
        public bool StopVpnService(Action<string> log) { Steps.Add("stop-vpn"); return VpnStops; }
    }

    private static UpdateJob Job() => new(
        4242, @"C:\u\RdpManager-Setup-1.14.1.exe", new string('a', 64), @"C:\p\RdpManager", @"C:\r\riserva",
        new Version(1, 14, 1), new Version(1, 14, 0), @"C:\l\aggiornamento.log", @"C:\l\setup.log");

    [Fact]
    public void Update_Runner_StopsTheVpnServiceAfterVerifyingAndBeforeRunningTheInstaller()
    {
        var env = new RunnerEnv();
        var r = UpdateRunner.Run(Job(), env, _ => { });
        Assert.True(r.Succeeded);
        Assert.Equal(["wait", "verify", "stop-vpn", "install", "launch"], env.Steps);
    }

    [Fact]
    public void Update_Runner_VpnThatDoesNotStopIsLoggedButTheInstallStillRuns()
    {
        var env = new RunnerEnv { VpnStops = false };
        var log = new List<string>();
        var r = UpdateRunner.Run(Job(), env, log.Add);
        Assert.True(r.Succeeded);
        Assert.Contains(log, l => l.Contains("VPN") && l.Contains("comunque"));
    }

    [Fact]
    public void Update_Runner_UnverifiedInstallerDoesNotStopTheVpn()
    {
        var env = new RunnerEnvBadHash();
        UpdateRunner.Run(Job(), env, _ => { });
        Assert.Empty(env.Steps);
    }

    private sealed class RunnerEnvBadHash : IUpdateEnvironment
    {
        public readonly List<string> Steps = [];
        public bool WaitForExit(int processId, TimeSpan timeout) => true;
        public bool FileExists(string path) => true;
        public bool VerifySha256(string path, string sha256) => false;
        public int? RunInstaller(string path, string arguments, TimeSpan timeout) { Steps.Add("install"); return 0; }
        public Version? FileVersion(string exePath) => new(1, 14, 1);
        public void Restore(string backup, string target) { }
        public void Launch(string exePath, IReadOnlyList<string> arguments) { }
        public bool StopVpnService(Action<string> log) { Steps.Add("stop-vpn"); return true; }
    }

    [Fact]
    public void Update_Installer_StopsTheVpnServiceBeforeCopyingFiles()
    {
        // Nessun Inno Setup in prova: si controlla che lo script lo fermi sia in silenzioso (InitializeSetup) sia a mano (PrepareToInstall).
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "installer", "RdpManager.iss")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var iss = File.ReadAllText(Path.Combine(dir!, "installer", "RdpManager.iss"));
        Assert.Contains("net.exe", iss);
        Assert.Contains("stop " + WireGuardNtTunnel.ServiceName, iss);
        Assert.Contains("function PrepareToInstall", iss);
        Assert.True(iss.IndexOf("StopVpnService;", iss.IndexOf("function InitializeSetup", StringComparison.Ordinal), StringComparison.Ordinal) > 0);
    }
}
