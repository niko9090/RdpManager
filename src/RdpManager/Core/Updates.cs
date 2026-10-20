using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Versione dell'app e indirizzi del progetto.</summary>
public static class AppInfo
{
    public const string RepositoryOwner = "niko9090";
    public const string RepositoryName = "RdpManager";
    public const string RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

    /// <summary>Versione dell'assembly (dal tag &lt;Version&gt; del csproj), a tre cifre.</summary>
    public static Version Version { get; } = Normalize(typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string VersionText => Version.ToString(3);

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

/// <summary>Dati essenziali di una release GitHub.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string PageUrl, string? Name);

public enum UpdateCheckStatus { UpToDate, NewerAvailable, Unavailable }

/// <summary>Esito di un controllo: <see cref="Release"/> è valorizzata solo se c'è una versione più recente.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null)
{
    public static readonly UpdateCheckResult Unavailable = new(UpdateCheckStatus.Unavailable);
}

/// <summary>
/// Controllo degli aggiornamenti sulle release GitHub. Solo lettura: non scarica e non installa nulla.
/// Ogni errore (rete assente, timeout, repository privato, JSON inatteso) dà <see cref="UpdateCheckStatus.Unavailable"/>,
/// senza eccezioni: chi chiama decide se tacere (controllo automatico) o dirlo (controllo dal menu).
/// </summary>
public static class UpdateChecker
{
    /// <summary>"v1.2.3", "1.2", "v2.0.0-beta1" → 1.2.3 / 1.2.0 / 2.0.0; null se non è una versione.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            text = text[..cut];
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
            return null;
        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return null;
            if (i < 3)
                numbers[i] = n;
        }
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    public static bool IsNewer(Version candidate, Version current) =>
        AppInfo.Normalize(candidate) > AppInfo.Normalize(current);

    /// <summary>Legge la risposta di /releases/latest. Bozze, prerelease e tag non numerici → null.</summary>
    public static ReleaseInfo? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (Bool(root, "draft") || Bool(root, "prerelease"))
                return null;
            var tag = Str(root, "tag_name");
            var url = Str(root, "html_url");
            var version = ParseVersion(tag);
            if (tag is null || version is null || url is null
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return null;
            return new ReleaseInfo(tag, version, url, Str(root, "name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http, Version current, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.LatestReleaseApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RdpManager", AppInfo.VersionText));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Unavailable;
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Evaluate(ParseLatestRelease(json), current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return UpdateCheckResult.Unavailable;
        }
    }

    public static UpdateCheckResult Evaluate(ReleaseInfo? latest, Version current) =>
        latest is null ? UpdateCheckResult.Unavailable
        : IsNewer(latest.Version, current) ? new UpdateCheckResult(UpdateCheckStatus.NewerAvailable, latest)
        : new UpdateCheckResult(UpdateCheckStatus.UpToDate);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
