using System.Net;
using RdpManager.Core;

namespace RdpManager.Tests;

public class UpdatesTests
{
    private const string SampleJson = """
        {
          "tag_name": "v1.2.0",
          "name": "RdpManager 1.2.0",
          "html_url": "https://github.com/niko9090/RdpManager/releases/tag/v1.2.0",
          "draft": false,
          "prerelease": false,
          "assets": []
        }
        """;

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("v3", 3, 0, 0)]
    [InlineData("v1.4.0-beta1", 1, 4, 0)]
    [InlineData(" v1.0.10 ", 1, 0, 10)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void ParseVersion_AcceptsCommonTagForms(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateChecker.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.x")]
    [InlineData("v-1.0")]
    [InlineData("1.2.3.4.5")]
    public void ParseVersion_RejectsNonVersions(string? tag)
    {
        Assert.Null(UpdateChecker.ParseVersion(tag));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    public void IsNewer_ComparesNumerically(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(candidate), Version.Parse(current)));
    }

    [Fact]
    public void IsNewer_IgnoresRevisionOfAssemblyVersion()
    {
        Assert.False(UpdateChecker.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0, 0)));
    }

    [Fact]
    public void ParseLatestRelease_ReadsTagUrlAndName()
    {
        var r = UpdateChecker.ParseLatestRelease(SampleJson)!;
        Assert.Equal("v1.2.0", r.Tag);
        Assert.Equal(new Version(1, 2, 0), r.Version);
        Assert.Equal("https://github.com/niko9090/RdpManager/releases/tag/v1.2.0", r.PageUrl);
        Assert.Equal("RdpManager 1.2.0", r.Name);
    }

    [Theory]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"https://github.com/x","draft":true}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"https://github.com/x","prerelease":true}""")]
    [InlineData("""{"tag_name":"nightly","html_url":"https://github.com/x"}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"http://insicuro.example/x"}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"javascript:alert(1)"}""")]
    [InlineData("""{"tag_name":"v2.0.0"}""")]
    [InlineData("""{"message":"Not Found","documentation_url":"https://docs.github.com"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""non è json""")]
    [InlineData("")]
    public void ParseLatestRelease_RejectsUnusableResponses(string json)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(json));
    }

    [Fact]
    public void AppInfo_VersionMatchesProjectVersionWithThreeParts()
    {
        Assert.Equal(3, AppInfo.VersionText.Split('.').Length);
        Assert.Equal(AppInfo.VersionText, AppInfo.Version.ToString(3));
        Assert.True(AppInfo.Version >= new Version(1, 0, 0));
        Assert.StartsWith("https://api.github.com/repos/niko9090/RdpManager/", AppInfo.LatestReleaseApiUrl);
    }

    [Fact]
    public async Task CheckAsync_NewerRelease_IsReported()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, SampleJson)));
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckStatus.NewerAvailable, result.Status);
        Assert.Equal(new Version(1, 2, 0), result.Release!.Version);
    }

    [Fact]
    public async Task CheckAsync_SameOrOlderRelease_IsUpToDate()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, SampleJson)));
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 2, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckAsync_SendsUserAgentAndAsksTheRightUrl()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new FakeHandler(req => { seen = req; return Json(HttpStatusCode.OK, SampleJson); }));
        await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(AppInfo.LatestReleaseApiUrl, seen!.RequestUri!.ToString());
        Assert.Contains("RdpManager", seen.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)] // repository privato o nessuna release
    [InlineData(HttpStatusCode.Forbidden)] // limite di richieste
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_HttpErrors_AreUnavailableWithoutExceptions(HttpStatusCode status)
    {
        using var http = new HttpClient(new FakeHandler(_ => Json(status, """{"message":"x"}""")));
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_IsUnavailableWithoutExceptions()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("rete assente")));
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task CheckAsync_Timeout_IsUnavailableQuickly()
    {
        using var http = new HttpClient(new SlowHandler());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromMilliseconds(200));
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"timeout non rispettato: {sw.Elapsed}");
    }

    [Fact]
    public async Task CheckAsync_GarbageBody_IsUnavailable()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, "<html>captive portal</html>")));
        var result = await UpdateChecker.CheckAsync(http, new Version(1, 0, 0), TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
