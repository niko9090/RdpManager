using System.Net;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Cronologia delle connessioni e aggiornamento con un clic (verifica SHA256), senza rete vera.</summary>
public sealed class HistoryAndUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerHU-" + Guid.NewGuid().ToString("N"));

    public HistoryAndUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTime T0 = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    // ---------- Cronologia ----------

    [Fact]
    public void History_StartEnd_DurationAndReason()
    {
        var h = new ConnectionHistory();
        var c = new RdpConnection { Name = "Server", Host = "srv.example.test" };
        var e = h.Start(c, T0);
        Assert.True(e.IsOpen);
        Assert.Null(e.Duration);

        h.End(e, T0.AddMinutes(65).AddSeconds(3), "Disconnessione remota dall'utente", 2);
        Assert.Equal(TimeSpan.FromSeconds(65 * 60 + 3), e.Duration);
        Assert.Equal(("Server", "srv.example.test", 2), (e.Name, e.Host, e.ReasonCode));
        Assert.Equal("1 h 05 min", HistoryEntry.FormatDuration(e.Duration));

        h.End(e, T0.AddHours(5), "altro"); // la seconda chiusura non cambia nulla
        Assert.Equal("Disconnessione remota dall'utente", e.Reason);

        Assert.Equal("3 min 07 s", HistoryEntry.FormatDuration(TimeSpan.FromSeconds(187)));
        Assert.Equal("12 s", HistoryEntry.FormatDuration(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void History_RecentFilterTotalAndClear()
    {
        var h = new ConnectionHistory();
        var a = new RdpConnection { Host = "a" };
        var b = new RdpConnection { Host = "b" };
        h.End(h.Start(a, T0), T0.AddMinutes(10), "fine");
        h.End(h.Start(b, T0.AddMinutes(1)), T0.AddMinutes(2), "fine");
        h.End(h.Start(a, T0.AddMinutes(20)), T0.AddMinutes(25), "fine");

        Assert.Equal(["a", "b", "a"], h.Recent().Select(e => e.Host).Reverse());
        Assert.Equal(2, h.Recent(a.Id).Count);
        Assert.Equal(TimeSpan.FromMinutes(15), h.TotalDuration(a.Id));
        Assert.Equal(2, h.Clear(a.Id));
        Assert.Single(h.Entries);
        h.Clear();
        Assert.Empty(h.Entries);
    }

    [Fact]
    public void History_KeepsOnlyTheLastEntries()
    {
        var h = new ConnectionHistory();
        var c = new RdpConnection { Host = "x" };
        for (int i = 0; i < ConnectionHistory.MaxEntries + 5; i++)
            h.Start(c, T0.AddMinutes(i));
        Assert.Equal(ConnectionHistory.MaxEntries, h.Entries.Count);
        Assert.Equal(T0.AddMinutes(5), h.Entries[0].StartUtc);
    }

    [Fact]
    public void HistoryStore_RoundTrip_InterruptedSessions_AndBrokenFile()
    {
        var store = HistoryStore.Beside(Path.Combine(_dir, "connections.json"));
        Assert.Equal(Path.Combine(_dir, "history.json"), store.FilePath);
        Assert.Empty(store.Load().Entries); // file assente

        var h = new ConnectionHistory();
        var c = new RdpConnection { Name = "PC", Host = "pc", Password = "Mai-Nella-Cronologia" };
        h.End(h.Start(c, T0), T0.AddMinutes(3), "Disconnessione locale", 1);
        h.Start(c, T0.AddMinutes(10)); // resta aperta: l'app si è chiusa di colpo
        store.Save(h);
        Assert.DoesNotContain("Mai-Nella-Cronologia", File.ReadAllText(store.FilePath));

        var loaded = store.Load();
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), loaded.Entries[0].Duration);
        Assert.Equal(1, loaded.CloseInterrupted());
        Assert.Equal(ConnectionHistory.InterruptedReason, loaded.Entries[1].Reason);

        File.WriteAllText(store.FilePath, "{ non è json");
        Assert.Empty(store.Load().Entries);
    }

    // ---------- SHA256SUMS ----------

    [Fact]
    public void Sha256Sums_ParsesSha256sumFormats()
    {
        var h1 = new string('a', 64);
        var h2 = new string('B', 64);
        var sums = Sha256Sums.Parse($"# commento\n{h1}  RdpManager-Setup-1.2.0.exe\r\n{h2} *RdpManager-1.2.0-win-x64.exe\nriga sbagliata\n{new string('c', 63)}  corto.exe\n");
        Assert.Equal(2, sums.Count);
        Assert.Equal(h1, sums["rdpmanager-setup-1.2.0.exe"]);
        Assert.Equal(h2.ToLowerInvariant(), sums["RdpManager-1.2.0-win-x64.exe"]);
    }

    [Fact]
    public void Sha256Sums_AcceptsTheRightFile_RejectsAnAlteredOne()
    {
        var path = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("installer di prova"));
        var hash = Sha256Sums.ComputeHex(path);
        Assert.Equal(64, hash.Length);
        Assert.True(Sha256Sums.Verify(path, hash));
        Assert.True(Sha256Sums.Verify(path, hash.ToUpperInvariant()));

        File.AppendAllText(path, "!");
        Assert.False(Sha256Sums.Verify(path, hash));
        Assert.False(Sha256Sums.Verify(path, ""));
        Assert.False(Sha256Sums.Verify(path, "zz"));
    }

    // ---------- Aggiornamento con un clic ----------

    private const string SetupUrl = "https://github.com/niko9090/RdpManager/releases/download/v9.9.9/RdpManager-Setup-9.9.9.exe";
    private const string SumsUrl = "https://github.com/niko9090/RdpManager/releases/download/v9.9.9/SHA256SUMS.txt";
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ installer finto 9.9.9");

    private static string HashOf(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static ReleaseInfo Release(bool withSums = true)
    {
        var assets = new List<ReleaseAsset> { new("RdpManager-Setup-9.9.9.exe", SetupUrl, Installer.Length) };
        if (withSums)
            assets.Add(new ReleaseAsset("SHA256SUMS.txt", SumsUrl, 100));
        return new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "https://github.com/niko9090/RdpManager/releases/tag/v9.9.9", null) { Assets = assets };
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Download_RightHash_IsVerified()
    {
        var sums = Sha256Sums.Format([("RdpManager-Setup-9.9.9.exe", HashOf(Installer))]);
        using var http = new HttpClient(new FakeHandler(new() { [SetupUrl] = Installer, [SumsUrl] = Encoding.UTF8.GetBytes(sums) }));

        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, Release(), _dir);

        Assert.Equal(UpdateDownloadStatus.Verified, result.Status);
        Assert.True(result.IsVerified);
        Assert.Equal(Path.Combine(_dir, "RdpManager-Setup-9.9.9.exe"), result.InstallerPath);
        Assert.Equal(Installer, File.ReadAllBytes(result.InstallerPath!));
    }

    [Fact]
    public async Task Download_AlteredFile_IsRejectedAndDeleted()
    {
        var sums = Sha256Sums.Format([("RdpManager-Setup-9.9.9.exe", HashOf(Installer))]);
        var altered = Installer.Concat(new byte[] { 0x90 }).ToArray();
        using var http = new HttpClient(new FakeHandler(new() { [SetupUrl] = altered, [SumsUrl] = Encoding.UTF8.GetBytes(sums) }));

        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, Release(), _dir);

        Assert.Equal(UpdateDownloadStatus.HashMismatch, result.Status);
        Assert.False(result.IsVerified);
        Assert.Null(result.InstallerPath);
        Assert.False(File.Exists(Path.Combine(_dir, "RdpManager-Setup-9.9.9.exe")));
    }

    [Fact]
    public async Task Download_WithoutChecksums_DownloadsNothing()
    {
        var handler = new FakeHandler(new() { [SetupUrl] = Installer });
        using var http = new HttpClient(handler);

        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, Release(withSums: false), _dir);

        Assert.Equal(UpdateDownloadStatus.NoChecksum, result.Status);
        Assert.Empty(handler.Requested);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_ChecksumsWithoutTheInstallerLine_DownloadsNothing()
    {
        var sums = Sha256Sums.Format([("altro-file.exe", HashOf(Installer))]);
        var handler = new FakeHandler(new() { [SetupUrl] = Installer, [SumsUrl] = Encoding.UTF8.GetBytes(sums) });
        using var http = new HttpClient(handler);

        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, Release(), _dir);

        Assert.Equal(UpdateDownloadStatus.NoChecksum, result.Status);
        Assert.DoesNotContain(SetupUrl, handler.Requested);
    }

    [Fact]
    public async Task Download_NetworkError_IsFailedWithoutFile()
    {
        using var http = new HttpClient(new FakeHandler([]));
        var result = await UpdateDownloader.DownloadAndVerifyAsync(http, Release(), _dir);
        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_ReleaseWithoutInstaller()
    {
        using var http = new HttpClient(new FakeHandler([]));
        var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "https://github.com/x", null);
        Assert.Equal(UpdateDownloadStatus.NoInstaller, (await UpdateDownloader.DownloadAndVerifyAsync(http, release, _dir)).Status);
    }

    [Fact]
    public void ParseLatestRelease_ReadsAssets_IgnoringNonHttps()
    {
        const string json = """
            {
              "tag_name": "v1.2.0", "html_url": "https://github.com/niko9090/RdpManager/releases/tag/v1.2.0",
              "assets": [
                { "name": "RdpManager-Setup-1.2.0.exe", "browser_download_url": "https://github.com/a/RdpManager-Setup-1.2.0.exe", "size": 123 },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/a/SHA256SUMS.txt", "size": 200 },
                { "name": "insicuro.exe", "browser_download_url": "http://example.test/insicuro.exe", "size": 1 }
              ]
            }
            """;
        var r = UpdateChecker.ParseLatestRelease(json)!;
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal("RdpManager-Setup-1.2.0.exe", r.SetupAsset!.Name);
        Assert.Equal(123, r.SetupAsset.Size);
        Assert.Equal("SHA256SUMS.txt", r.ChecksumsAsset!.Name);
    }
}
