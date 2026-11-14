using System.Net;
using System.Text;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

internal sealed class UpdateRunner : IProcessRunner
{
    public List<(string File, string[] Args)> Calls { get; } = [];
    public Func<string, string[], ProcessResult> Script { get; set; } = (_, _) => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default)
    {
        var args = arguments.ToArray();
        Calls.Add((fileName, args));
        return Task.FromResult(Script(fileName, args));
    }
}

internal sealed class UpdateSystem : IUpdateSystem
{
    public Dictionary<string, string> Env { get; } = [];
    public HashSet<string> Files { get; } = [];
    public HashSet<string> Commands { get; set; } = ["pkexec", "apt-get", "dpkg", "dnf", "zypper", "rpm"];
    public string? ProcessPath { get; set; }
    public string? Relaunched { get; private set; }
    public string? GetEnvironmentVariable(string name) => Env.GetValueOrDefault(name);
    public bool FileExists(string path) => Files.Contains(path);
    public bool CommandExists(string name) => Commands.Contains(name);
    public void RelaunchDetached(string path) => Relaunched = path;
}

public class LinuxUpdaterTests
{
    private static ReleaseInfo Release(string version = "2.0.0", params string[] names) =>
        new("v" + version, Version.Parse(version), "https://github.com/niko9090/RdpManager/releases/tag/v" + version, null)
        {
            Assets = names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 10)).ToList(),
        };

    [Fact]
    public async Task Rileva_AppImage_dalla_variabile_APPIMAGE()
    {
        var sys = new UpdateSystem { ProcessPath = "/tmp/.mount_x/usr/bin/RdpManager.Linux" };
        sys.Env["APPIMAGE"] = "/home/u/App/RdpManager.AppImage";
        sys.Files.Add("/home/u/App/RdpManager.AppImage");
        var info = await new LinuxUpdater(new UpdateRunner(), sys).DetectAsync();
        Assert.Equal(LinuxInstallKind.AppImage, info.Kind);
        Assert.Equal("/home/u/App/RdpManager.AppImage", info.AppImagePath);
        Assert.Equal("/home/u/App/RdpManager.AppImage", info.LaunchPath);
    }

    [Fact]
    public async Task APPIMAGE_che_punta_a_un_file_inesistente_non_conta()
    {
        var sys = new UpdateSystem { ProcessPath = "/home/u/build/RdpManager.Linux" };
        sys.Env["APPIMAGE"] = "/nonesiste.AppImage";
        Assert.Equal(LinuxInstallKind.Unknown, (await new LinuxUpdater(new UpdateRunner(), sys).DetectAsync()).Kind);
    }

    [Fact]
    public async Task Rileva_deb_con_dpkg_e_rpm_con_rpm()
    {
        var exe = "/opt/rdpmanager/RdpManager.Linux";
        var runner = new UpdateRunner { Script = (f, _) => f == "dpkg" ? new ProcessResult(0, "rdpmanager: " + exe, "") : new ProcessResult(1, "", "") };
        var sys = new UpdateSystem { ProcessPath = exe };
        var deb = await new LinuxUpdater(runner, sys).DetectAsync();
        Assert.Equal(LinuxInstallKind.Deb, deb.Kind);
        Assert.Equal("/usr/bin/rdpmanager", deb.LaunchPath);
        Assert.Equal("dpkg", runner.Calls[0].File);
        Assert.Equal(["-S", exe], runner.Calls[0].Args);

        var rpmRunner = new UpdateRunner { Script = (f, _) => f == "rpm" ? new ProcessResult(0, "rdpmanager-2.0.0-1", "") : new ProcessResult(1, "", "") };
        Assert.Equal(LinuxInstallKind.Rpm, (await new LinuxUpdater(rpmRunner, sys).DetectAsync()).Kind);
        Assert.Equal(["dpkg", "rpm"], rpmRunner.Calls.Select(c => c.File).ToArray());
    }

    [Fact]
    public async Task Fuori_da_opt_o_senza_pacchetto_e_sconosciuto()
    {
        var runner = new UpdateRunner();
        Assert.Equal(LinuxInstallKind.Unknown,
            (await new LinuxUpdater(runner, new UpdateSystem { ProcessPath = "/home/u/bin/RdpManager.Linux" }).DetectAsync()).Kind);
        Assert.Empty(runner.Calls);
        var none = new UpdateRunner { Script = (_, _) => new ProcessResult(1, "", "") };
        Assert.Equal(LinuxInstallKind.Unknown,
            (await new LinuxUpdater(none, new UpdateSystem { ProcessPath = "/opt/rdpmanager/RdpManager.Linux" }).DetectAsync()).Kind);
    }

    [Fact]
    public void Sceglie_il_file_giusto_della_release()
    {
        var r = Release("2.0.1", "rdpmanager_2.0.1_amd64.deb", "rdpmanager-2.0.1-1.x86_64.rpm", "RdpManager-2.0.1-x86_64.AppImage",
            "RdpManager-Setup-2.0.1.exe", "SHA256SUMS.txt");
        Assert.Equal("rdpmanager_2.0.1_amd64.deb", LinuxUpdater.PickAsset(r, LinuxInstallKind.Deb)!.Name);
        Assert.Equal("rdpmanager-2.0.1-1.x86_64.rpm", LinuxUpdater.PickAsset(r, LinuxInstallKind.Rpm)!.Name);
        Assert.Equal("RdpManager-2.0.1-x86_64.AppImage", LinuxUpdater.PickAsset(r, LinuxInstallKind.AppImage)!.Name);
        Assert.Null(LinuxUpdater.PickAsset(r, LinuxInstallKind.Unknown));
        Assert.Null(LinuxUpdater.PickAsset(Release("2.0.1", "RdpManager-Setup-2.0.1.exe"), LinuxInstallKind.Deb));
    }

    [Fact]
    public void Comandi_deb_apt_poi_dpkg_sempre_dietro_pkexec()
    {
        var cmds = new LinuxUpdater(new UpdateRunner(), new UpdateSystem()).BuildInstallCommands(LinuxInstallKind.Deb, "/tmp/u/x.deb");
        Assert.Equal(2, cmds.Count);
        Assert.All(cmds, c => Assert.Equal("pkexec", c.FileName));
        Assert.Equal(["apt-get", "install", "-y", "/tmp/u/x.deb"], cmds[0].Arguments);
        Assert.Equal(["dpkg", "-i", "/tmp/u/x.deb"], cmds[1].Arguments);
    }

    [Fact]
    public void Comandi_rpm_saltano_i_gestori_assenti()
    {
        var all = new LinuxUpdater(new UpdateRunner(), new UpdateSystem()).BuildInstallCommands(LinuxInstallKind.Rpm, "/tmp/u/x.rpm");
        Assert.Equal(["dnf", "zypper", "rpm"], all.Select(c => c.Arguments[0]).ToArray());
        Assert.Equal(["dnf", "install", "-y", "/tmp/u/x.rpm"], all[0].Arguments);

        var suse = new UpdateSystem { Commands = ["pkexec", "zypper", "rpm"] };
        var cmds = new LinuxUpdater(new UpdateRunner(), suse).BuildInstallCommands(LinuxInstallKind.Rpm, "/tmp/u/x.rpm");
        Assert.Equal(["zypper", "rpm"], cmds.Select(c => c.Arguments[0]).ToArray());

        var noPkexec = new UpdateSystem { Commands = ["dnf", "rpm"] };
        Assert.Empty(new LinuxUpdater(new UpdateRunner(), noPkexec).BuildInstallCommands(LinuxInstallKind.Rpm, "/tmp/u/x.rpm"));
    }

    [Fact]
    public async Task Se_apt_fallisce_ripiega_su_dpkg()
    {
        var runner = new UpdateRunner { Script = (_, a) => a[0] == "apt-get" ? new ProcessResult(100, "", "E: dipendenze") : new ProcessResult(0, "", "") };
        var r = await new LinuxUpdater(runner, new UpdateSystem()).InstallAsync(new LinuxInstallInfo(LinuxInstallKind.Deb), "/tmp/x.deb");
        Assert.True(r.IsInstalled);
        Assert.Equal(["apt-get", "dpkg"], runner.Calls.Select(c => c.Args[0]).ToArray());
    }

    [Fact]
    public async Task Se_tutti_falliscono_dice_l_ultimo_errore()
    {
        var runner = new UpdateRunner { Script = (_, a) => new ProcessResult(1, "", "errore di " + a[0] + "\naltro") };
        var r = await new LinuxUpdater(runner, new UpdateSystem()).InstallAsync(new LinuxInstallInfo(LinuxInstallKind.Deb), "/tmp/x.deb");
        Assert.Equal(LinuxInstallStatus.Failed, r.Status);
        Assert.Contains("errore di dpkg", r.Message);
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task Autorizzazione_negata_si_ferma_senza_ripiego(int code)
    {
        var runner = new UpdateRunner { Script = (_, _) => new ProcessResult(code, "", "") };
        var r = await new LinuxUpdater(runner, new UpdateSystem()).InstallAsync(new LinuxInstallInfo(LinuxInstallKind.Deb), "/tmp/x.deb");
        Assert.Equal(LinuxInstallStatus.Cancelled, r.Status);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Senza_pkexec_non_lancia_nulla()
    {
        var runner = new UpdateRunner();
        var sys = new UpdateSystem { Commands = ["apt-get", "dpkg"] };
        var r = await new LinuxUpdater(runner, sys).InstallAsync(new LinuxInstallInfo(LinuxInstallKind.Deb), "/tmp/x.deb");
        Assert.Equal(LinuxInstallStatus.NoPrivilegeTool, r.Status);
        Assert.Contains("pkexec", r.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Tipo_sconosciuto_non_si_installa()
    {
        var r = await new LinuxUpdater(new UpdateRunner(), new UpdateSystem()).InstallAsync(new LinuxInstallInfo(LinuxInstallKind.Unknown), "/tmp/x");
        Assert.Equal(LinuxInstallStatus.Failed, r.Status);
    }

    [Fact]
    public void AppImage_si_sostituisce_con_rename_e_senza_avanzi()
    {
        var dir = Directory.CreateTempSubdirectory("rdpm-upd-").FullName;
        try
        {
            var target = Path.Combine(dir, "RdpManager.AppImage");
            var download = Path.Combine(dir, "download.bin");
            File.WriteAllText(target, "vecchia");
            File.WriteAllText(download, "nuova");
            var r = new LinuxUpdater(new UpdateRunner(), new UpdateSystem()).ReplaceAppImage(target, download);
            Assert.True(r.IsInstalled);
            Assert.Equal("nuova", File.ReadAllText(target));
            Assert.Equal(["RdpManager.AppImage", "download.bin"],
                Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
            if (!OperatingSystem.IsWindows())
                Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AppImage_senza_percorso_o_con_cartella_inesistente_fallisce_con_messaggio()
    {
        var u = new LinuxUpdater(new UpdateRunner(), new UpdateSystem());
        Assert.Equal(LinuxInstallStatus.Failed, u.ReplaceAppImage(null, "x").Status);
        var r = u.ReplaceAppImage(Path.Combine(Path.GetTempPath(), "rdpm-nodir-" + Guid.NewGuid().ToString("N"), "A.AppImage"), "x");
        Assert.Equal(LinuxInstallStatus.Failed, r.Status);
    }

    [Fact]
    public void Riavvio_usa_il_percorso_da_riaprire()
    {
        var sys = new UpdateSystem();
        new LinuxUpdater(new UpdateRunner(), sys).Relaunch(new LinuxInstallInfo(LinuxInstallKind.Deb, LaunchPath: "/usr/bin/rdpmanager"));
        Assert.Equal("/usr/bin/rdpmanager", sys.Relaunched);
    }
}

public class LinuxUpdateDownloadTests
{
    private sealed class Handler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static ReleaseInfo Release(params string[] names) =>
        new("v2.0.0", new Version(2, 0, 0), "https://github.com/x/r", null)
        {
            Assets = names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 5)).ToList(),
        };

    [Fact]
    public async Task Scarica_e_verifica_l_hash_dell_allegato_scelto()
    {
        var deb = Encoding.UTF8.GetBytes("pacchetto");
        var name = "rdpmanager_2.0.0_amd64.deb";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(deb)).ToLowerInvariant();
        var sums = Encoding.UTF8.GetBytes($"{hash}  {name}\n");
        using var http = new HttpClient(new Handler(new() { [name] = deb, ["SHA256SUMS.txt"] = sums }));
        var folder = Directory.CreateTempSubdirectory("rdpm-dl-").FullName;
        try
        {
            var release = Release(name, "SHA256SUMS.txt");
            var result = await UpdateDownloader.DownloadAndVerifyAsync(http, release, LinuxUpdater.PickAsset(release, LinuxInstallKind.Deb), folder);
            Assert.True(result.IsVerified);
            Assert.Equal(name, Path.GetFileName(result.InstallerPath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Hash_diverso_cancella_il_file_e_non_e_verificato()
    {
        var name = "RdpManager-2.0.0-x86_64.AppImage";
        var sums = Encoding.UTF8.GetBytes($"{new string('a', 64)}  {name}\n");
        using var http = new HttpClient(new Handler(new() { [name] = Encoding.UTF8.GetBytes("manomesso"), ["SHA256SUMS.txt"] = sums }));
        var folder = Directory.CreateTempSubdirectory("rdpm-dl-").FullName;
        try
        {
            var release = Release(name, "SHA256SUMS.txt");
            var result = await UpdateDownloader.DownloadAndVerifyAsync(http, release, LinuxUpdater.PickAsset(release, LinuxInstallKind.AppImage), folder);
            Assert.Equal(UpdateDownloadStatus.HashMismatch, result.Status);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Riga_mancante_in_SHA256SUMS_non_scarica()
    {
        var name = "rdpmanager_2.0.0_amd64.deb";
        using var http = new HttpClient(new Handler(new() { ["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{new string('b', 64)}  altro.deb\n") }));
        var release = Release(name, "SHA256SUMS.txt");
        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, release, LinuxUpdater.PickAsset(release, LinuxInstallKind.Deb));
        Assert.Equal(UpdateDownloadStatus.NoChecksum, result.Status);
    }
}

public class LinuxUpdateFlowTests
{
    private static readonly ReleaseInfo Rel = new("v99.0.0", new Version(99, 0, 0), "https://github.com/niko9090/RdpManager/releases/tag/v99.0.0", "99")
    {
        Assets =
        [
            new ReleaseAsset("rdpmanager_99.0.0_amd64.deb", "https://example.com/a.deb", 10),
            new ReleaseAsset("SHA256SUMS.txt", "https://example.com/s", 10),
        ],
    };

    private static (RdpManager.Linux.ViewModels.MainWindowViewModel Vm, NullShellUi Ui, UpdateRunner Runner, UpdateSystem Sys, TempData Data) Make()
    {
        var data = new TempData();
        var vm = Seed.Main(data);
        var ui = new NullShellUi { OfferAnswer = true };
        vm.Ui = ui;
        var runner = new UpdateRunner();
        var sys = new UpdateSystem { ProcessPath = "/opt/rdpmanager/RdpManager.Linux" };
        vm.Updater = new LinuxUpdater(runner, sys);
        vm.UpdateCheck = _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NewerAvailable, Rel));
        return (vm, ui, runner, sys, data);
    }

    [Fact]
    public async Task Deb_confermato_scarica_installa_e_riavvia()
    {
        var (vm, ui, runner, sys, data) = Make();
        using var _ = data;
        runner.Script = (f, a) => f == "dpkg" && a[0] == "-S" ? new ProcessResult(0, "", "") : new ProcessResult(0, "", "");
        vm.UpdateDownload = (_, asset, progress, _) =>
        {
            progress.Report(1);
            return Task.FromResult(new UpdateDownloadResult(UpdateDownloadStatus.Verified, "/tmp/" + asset.Name, "ok", new string('a', 64)));
        };
        bool exited = false;
        vm.ExitRequested += (_, _) => exited = true;
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Equal([true], ui.OffersCanInstall);
        Assert.Single(ui.Progresses);
        Assert.True(ui.Progresses[0].Disposed);
        Assert.Contains(runner.Calls, c => c.File == "pkexec" && c.Args.SequenceEqual(["apt-get", "install", "-y", "/tmp/rdpmanager_99.0.0_amd64.deb"]));
        Assert.Equal("/usr/bin/rdpmanager", sys.Relaunched);
        Assert.True(exited);
        Assert.Empty(ui.OpenedUrls);
    }

    [Fact]
    public async Task Utente_annulla_i_permessi_avviso_e_pagina_della_release()
    {
        var (vm, ui, runner, sys, data) = Make();
        using var _ = data;
        runner.Script = (f, a) => f == "pkexec" ? new ProcessResult(126, "", "") : new ProcessResult(0, "", "");
        vm.UpdateDownload = (_, asset, _, _) =>
            Task.FromResult(new UpdateDownloadResult(UpdateDownloadStatus.Verified, "/tmp/" + asset.Name, "ok", new string('a', 64)));
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Equal([Rel.PageUrl], ui.OpenedUrls);
        Assert.Contains("autorizzato", vm.StatusMessage);
        Assert.Null(sys.Relaunched);
    }

    [Fact]
    public async Task Hash_sbagliato_non_installa_e_apre_la_pagina()
    {
        var (vm, ui, runner, _, data) = Make();
        using var _ = data;
        vm.UpdateDownload = (_, _, _, _) =>
            Task.FromResult(new UpdateDownloadResult(UpdateDownloadStatus.HashMismatch, null, "SHA256 diverso"));
        await vm.CheckUpdatesAsync(manual: true);
        Assert.DoesNotContain(runner.Calls, c => c.File == "pkexec");
        Assert.Equal([Rel.PageUrl], ui.OpenedUrls);
        Assert.Contains("SHA256 diverso", vm.StatusMessage);
    }

    [Fact]
    public async Task Installazione_non_riconosciuta_offre_solo_la_pagina()
    {
        var (vm, ui, _, sys, data) = Make();
        using var _ = data;
        sys.ProcessPath = "/home/u/build/RdpManager.Linux";
        bool downloaded = false;
        vm.UpdateDownload = (_, _, _, _) => { downloaded = true; return Task.FromResult(new UpdateDownloadResult(UpdateDownloadStatus.Failed, null, "no")); };
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Equal([false], ui.OffersCanInstall);
        Assert.False(downloaded);
        Assert.Equal([Rel.PageUrl], ui.OpenedUrls);
    }

    [Fact]
    public async Task Piu_tardi_non_scarica_nulla()
    {
        var (vm, ui, _, _, data) = Make();
        using var _ = data;
        ui.OfferAnswer = false;
        bool downloaded = false;
        vm.UpdateDownload = (_, _, _, _) => { downloaded = true; return Task.FromResult(new UpdateDownloadResult(UpdateDownloadStatus.Failed, null, "no")); };
        await vm.CheckUpdatesAsync(manual: true);
        Assert.False(downloaded);
        Assert.Empty(ui.OpenedUrls);
    }
}
