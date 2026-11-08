using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RdpManager.Core;

/// <summary>
/// File SHA256SUMS.txt allegato alle release: una riga per file, "&lt;sha256 esadecimale&gt;  &lt;nome&gt;"
/// (formato di sha256sum; accettati anche "*nome" e un solo spazio). Righe vuote e "#" si ignorano.
/// </summary>
public static class Sha256Sums
{
    public const string FileName = "SHA256SUMS.txt";

    /// <summary>Nome del file → hash in esadecimale minuscolo. Righe non valide si ignorano.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int space = line.IndexOfAny([' ', '\t']);
            if (space != 64)
                continue;
            var hash = line[..64];
            var name = line[space..].Trim().TrimStart('*').Trim();
            if (name.Length == 0 || !IsHex(hash))
                continue;
            result[Path.GetFileName(name)] = hash.ToLowerInvariant();
        }
        return result;
    }

    /// <summary>"&lt;hash&gt;  &lt;nome&gt;\n" per ogni file, come sha256sum (usato da scripts/release.ps1 e dai test).</summary>
    public static string Format(IEnumerable<(string Name, string Hash)> files) =>
        string.Concat(files.Select(f => $"{f.Hash.ToLowerInvariant()}  {f.Name}\n"));

    public static string ComputeHex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Vero se il file ha esattamente l'hash atteso (confronto a tempo costante).</summary>
    public static bool Verify(string path, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || expectedHex.Trim().Length != 64 || !IsHex(expectedHex.Trim()))
            return false;
        var actual = Convert.FromHexString(ComputeHex(path));
        var expected = Convert.FromHexString(expectedHex.Trim());
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);
}

public enum UpdateDownloadStatus
{
    /// <summary>Installer scaricato e verificato: si può avviare (dopo la conferma dell'utente).</summary>
    Verified,
    /// <summary>La release non ha l'installer.</summary>
    NoInstaller,
    /// <summary>La release non ha SHA256SUMS.txt, o non contiene la riga dell'installer.</summary>
    NoChecksum,
    /// <summary>L'hash del file scaricato non coincide: il file è stato cancellato.</summary>
    HashMismatch,
    /// <summary>Errore di rete, timeout, file troppo grande o disco.</summary>
    Failed,
}

/// <param name="InstallerPath">Solo con <see cref="UpdateDownloadStatus.Verified"/>.</param>
/// <param name="Sha256">Hash verificato (esadecimale minuscolo), da ricontrollare subito prima di avviare il file.</param>
public sealed record UpdateDownloadResult(UpdateDownloadStatus Status, string? InstallerPath, string Message, string? Sha256 = null)
{
    public bool IsVerified => Status == UpdateDownloadStatus.Verified && InstallerPath is not null;
}

/// <summary>
/// Aggiornamento con un clic, parte senza interfaccia: scarica l'installer dell'ultima release in una cartella
/// temporanea e lo verifica con SHA256SUMS.txt della stessa release. Non avvia mai nulla: l'avvio, dopo la
/// conferma dell'utente, spetta all'interfaccia e solo se l'esito è <see cref="UpdateDownloadStatus.Verified"/>.
/// </summary>
public static class UpdateDownloader
{
    /// <summary>Limite di sicurezza sulla dimensione dell'installer.</summary>
    public const long MaxInstallerBytes = 500L * 1024 * 1024;

    /// <summary>%LOCALAPPDATA%\RdpManager\updates: qui si scarica e si verifica l'installer.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpManager", "updates");

    /// <summary>
    /// Toglie dalla cartella degli aggiornamenti tutto tranne <paramref name="keep"/> (file vecchi, scaricamenti
    /// interrotti o non verificati). Restituisce quanti file ha cancellato.
    /// </summary>
    public static int CleanFolder(string folder, string? keep = null)
    {
        if (!Directory.Exists(folder))
            return 0;
        int n = 0;
        foreach (var f in Directory.GetFiles(folder))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                continue;
            TryDelete(f);
            if (!File.Exists(f))
                n++;
        }
        return n;
    }

    public static async Task<UpdateDownloadResult> DownloadAndVerifyAsync(HttpClient http, ReleaseInfo release,
        string? folder = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        if (release.SetupAsset is not { } setup)
            return new(UpdateDownloadStatus.NoInstaller, null, $"La release {release.Tag} non contiene l'installer.");
        if (release.ChecksumsAsset is not { } sums)
            return new(UpdateDownloadStatus.NoChecksum, null,
                $"La release {release.Tag} non contiene {Sha256Sums.FileName}: l'installer non si può verificare e non viene scaricato.");

        string? target = null;
        try
        {
            var expected = Sha256Sums.Parse(await GetStringAsync(http, sums.DownloadUrl, cancellationToken).ConfigureAwait(false));
            if (!expected.TryGetValue(setup.Name, out var expectedHash))
                return new(UpdateDownloadStatus.NoChecksum, null,
                    $"{Sha256Sums.FileName} non contiene la riga di {setup.Name}: l'installer non viene scaricato.");
            if (setup.Size > MaxInstallerBytes)
                return new(UpdateDownloadStatus.Failed, null, "L'installer è più grande del previsto: scaricamento annullato.");

            folder ??= DefaultFolder;
            Directory.CreateDirectory(folder);
            // Nome con il solo nome dell'allegato: niente percorsi dal server.
            target = Path.Combine(folder, Path.GetFileName(setup.Name));
            if (File.Exists(target))
                File.Delete(target);

            using (var request = NewRequest(setup.DownloadUrl))
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? length = response.Content.Headers.ContentLength;
                if (length > MaxInstallerBytes)
                    return new(UpdateDownloadStatus.Failed, null, "L'installer è più grande del previsto: scaricamento annullato.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = File.Create(target);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes)
                        throw new IOException("L'installer è più grande del previsto: scaricamento annullato.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (length > 0)
                        progress?.Report((double)total / length.Value);
                }
            }

            if (!Sha256Sums.Verify(target, expectedHash))
            {
                TryDelete(target);
                return new(UpdateDownloadStatus.HashMismatch, null,
                    $"Il file scaricato non corrisponde a {Sha256Sums.FileName} (SHA256 diverso): è stato cancellato e non viene avviato.");
            }
            progress?.Report(1.0);
            return new(UpdateDownloadStatus.Verified, target,
                $"{setup.Name} scaricato e verificato (SHA256 {expectedHash[..12]}…).", expectedHash);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or InvalidOperationException)
        {
            if (target is not null)
                TryDelete(target);
            return new(UpdateDownloadStatus.Failed, null, "Scaricamento non riuscito: " + ex.Message);
        }
    }

    private static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = NewRequest(url);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RdpManager", AppInfo.VersionText));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
