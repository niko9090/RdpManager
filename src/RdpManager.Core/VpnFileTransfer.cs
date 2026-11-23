using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RdpManager.Core;

/// <summary>Un file in arrivo da un altro PC dello stesso account, in attesa di «Accetta» o «Rifiuta».</summary>
public sealed record IncomingFile(string Id, string From, string FileName, long Size, DateTimeOffset CreatedUtc);

/// <summary>
/// Scambio di file fra i PC della VPN tramite rdpm-sync (<c>/v1/vpn/files</c>, stesso token della plancia). Nessuna
/// interfaccia: la usano sia Windows sia Linux. <paramref name="deviceName"/> è il nome di questo PC nella VPN.
/// </summary>
public sealed class VpnFileTransferClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _device;
    private readonly TimeSpan _quick, _transfer;

    public VpnFileTransferClient(string baseUrl, string deviceName, HttpMessageHandler? handler = null,
        TimeSpan? timeout = null, TimeSpan? transferTimeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(SyncHttp.NormalizeUrl(baseUrl));
        _http.Timeout = Timeout.InfiniteTimeSpan; // i limiti sono per chiamata (sotto)
        _device = deviceName;
        _quick = timeout ?? TimeSpan.FromSeconds(15);
        _transfer = transferTimeout ?? TimeSpan.FromMinutes(15);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Device-Name", _device);
        return req;
    }

    /// <summary>Esegue la chiamata con un tempo massimo e traduce gli errori di rete nelle eccezioni del contratto.</summary>
    private async Task<T> CallAsync<T>(HttpRequestMessage req, TimeSpan limit, Func<HttpResponseMessage, Task<T>> read,
        CancellationToken ct, bool headersOnly = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            using var r = await _http.SendAsync(req, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cts.Token);
            if (r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                throw new SyncUnreachableException(
                    $"Il server di sincronizzazione è spento o si sta riavviando (errore {(int)r.StatusCode}).", serverDown: true);
            if (!r.IsSuccessStatusCode)
                throw VpnDashboardClient.Error(r.StatusCode, await r.Content.ReadAsStringAsync(cts.Token));
            return await read(r);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncUnreachableException("Il server di sincronizzazione non è raggiungibile.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SyncUnreachableException("Il server di sincronizzazione non risponde.", ex);
        }
    }

    /// <summary>Invia il file al PC <paramref name="to"/> (un altro PC del tuo account).</summary>
    /// <exception cref="SyncServerException">Rifiutato dal server (file troppo grande, PC non trovato, accesso scaduto…).</exception>
    public async Task SendAsync(string token, string to, string fileName, Stream content, CancellationToken ct = default)
    {
        var path = $"v1/vpn/files?to={Uri.EscapeDataString(to)}&name={Uri.EscapeDataString(fileName)}";
        using var req = Request(HttpMethod.Post, path, token);
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (content.CanSeek)
            req.Content.Headers.ContentLength = content.Length;
        await CallAsync(req, _transfer, _ => Task.FromResult(true), ct);
    }

    public async Task<IReadOnlyList<IncomingFile>> ListIncomingAsync(string token, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, "v1/vpn/files", token);
        return await CallAsync(req, _quick, async r => Parse(await r.Content.ReadAsStringAsync(ct), r.StatusCode), ct);
    }

    /// <summary>
    /// Scarica il file in <paramref name="folder"/> con un nome che non ne sovrascrive altri; il server lo cancella a
    /// scaricamento finito. Restituisce il percorso salvato.
    /// </summary>
    public async Task<string> AcceptAsync(string token, IncomingFile file, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".rdpm-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var req = Request(HttpMethod.Post, $"v1/vpn/files/{Uri.EscapeDataString(file.Id)}/accept", token);
            await CallAsync(req, _transfer, async r =>
            {
                await using var src = await r.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
                if (dst.Length != file.Size)
                    throw new IOException("Il file è arrivato incompleto: riprova ad accettarlo.");
                return true;
            }, ct, headersOnly: true);
            return MoveToUniqueName(temp, folder, SafeLocalName(file.FileName));
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* file temporaneo rimasto: nome nascosto, innocuo */ }
        }
    }

    public async Task RejectAsync(string token, IncomingFile file, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"v1/vpn/files/{Uri.EscapeDataString(file.Id)}/reject", token);
        await CallAsync(req, _quick, _ => Task.FromResult(true), ct);
    }

    private static string MoveToUniqueName(string temp, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 0; n < 1000; n++)
        {
            var candidate = Path.Combine(folder, n == 0 ? name : $"{stem} ({n}){ext}");
            try
            {
                File.Move(temp, candidate); // senza overwrite: se esiste, lancia e si prova il nome dopo
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }
        throw new IOException("Non trovo un nome libero nella cartella scelta.");
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Nome file sicuro da scrivere sul disco: mai percorsi, caratteri riservati o nomi riservati di Windows.</summary>
    public static string SafeLocalName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
            || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (s.Length > 150)
            s = s[..150];
        if (s.Length == 0 || s.All(c => c == '.' || c == '_'))
            s = "file";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(s)))
            s = "_" + s;
        return s;
    }

    private static IReadOnlyList<IncomingFile> Parse(string raw, HttpStatusCode status)
    {
        try
        {
            var list = new List<IncomingFile>();
            foreach (var f in JsonDocument.Parse(raw).RootElement.GetProperty("files").EnumerateArray())
                list.Add(new IncomingFile(
                    f.GetProperty("id").GetString() ?? "",
                    f.GetProperty("from").GetString() ?? "",
                    f.GetProperty("name").GetString() ?? "",
                    f.GetProperty("size").GetInt64(),
                    f.TryGetProperty("createdUtc", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTimeOffset(out var t)
                        ? t : DateTimeOffset.MinValue));
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SyncServerException(status, "invalid_response", "Risposta del server non valida.");
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Testi italiani dello scambio di file (uguali su Windows e Linux).</summary>
public static class VpnFileModel
{
    public static string FormatSize(long bytes)
    {
        var c = CultureInfo.GetCultureInfo("it-IT");
        return bytes switch
        {
            < 1024 => $"{bytes} byte",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", c) + " KB",
            < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.#", c) + " MB",
            _ => (bytes / 1073741824.0).ToString("0.##", c) + " GB",
        };
    }

    public static string Title => "File in arrivo";

    public static string PromptText(IncomingFile f) =>
        $"{f.From} vuole inviarti il file «{f.FileName}» ({FormatSize(f.Size)}).";

    public static string SentText(string to, string fileName) => $"«{fileName}» inviato a {to}. Il PC deve accettarlo.";

    public static string SavedText(string path) => $"File salvato in {path}";

    public static string SendError(Exception ex) => ex switch
    {
        SyncUnreachableException => "Il server non risponde: il file non è stato inviato.",
        SyncServerException { Status: HttpStatusCode.Unauthorized } =>
            "L'accesso all'account non è più valido: rifai l'accesso da «Account e sincronizzazione».",
        SyncServerException s => s.Message,
        FileNotFoundException or DirectoryNotFoundException => "Il file scelto non esiste più.",
        UnauthorizedAccessException or IOException => "Non riesco a leggere il file: " + ex.Message,
        _ => "Invio non riuscito: " + ex.Message,
    };

    public static string ReceiveError(Exception ex) => ex switch
    {
        SyncUnreachableException => "Il server non risponde: il file non è stato scaricato. Riproverò al prossimo avvio.",
        SyncServerException { Code: "not_found" } => "Il file non è più disponibile sul server.",
        SyncServerException s => s.Message,
        UnauthorizedAccessException or IOException => "Non riesco a salvare il file: " + ex.Message,
        _ => "Download non riuscito: " + ex.Message,
    };

    /// <summary>Cartella proposta per il salvataggio: Scaricati dell'utente (se non esiste, il profilo).</summary>
    public static string DefaultFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) || home.Length == 0 ? downloads : home;
    }
}

public enum IncomingFileChoice { Later, Accept, Reject }

/// <summary>Risposta dell'utente all'avviso: <see cref="Folder"/> vale solo per «Accetta».</summary>
public sealed record IncomingFileDecision(IncomingFileChoice Choice, string? Folder = null);

/// <summary>
/// Controlla i file in arrivo e li presenta uno alla volta (nessuna interfaccia: la finestra dell'avviso è un delegato).
/// Un file a cui l'utente ha chiuso l'avviso senza scegliere («Più tardi») non viene richiesto di nuovo in questa
/// sessione; resta sul server fino a scadenza.
/// </summary>
public sealed class IncomingFilesCoordinator(
    Func<bool> signedIn,
    Func<CancellationToken, Task<IReadOnlyList<IncomingFile>>> load,
    Func<IncomingFile, CancellationToken, Task<IncomingFileDecision>> ask,
    Func<IncomingFile, string, CancellationToken, Task<string>> accept,
    Func<IncomingFile, CancellationToken, Task> reject,
    Action<string> notify)
{
    private readonly HashSet<string> _handled = [];
    private int _running;

    /// <summary>Un solo controllo alla volta: se l'avviso è ancora aperto, il giro successivo non fa nulla.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!signedIn() || Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            IReadOnlyList<IncomingFile> files;
            try
            {
                files = await load(ct);
            }
            catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                           or HttpRequestException or IOException)
            {
                return; // controllo in sottofondo: un server che non risponde non deve disturbare, si riprova tra 30 s
            }
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested || !_handled.Add(file.Id))
                    continue;
                var decision = await ask(file, ct);
                try
                {
                    switch (decision.Choice)
                    {
                        case IncomingFileChoice.Accept:
                            var saved = await accept(file, decision.Folder ?? VpnFileModel.DefaultFolder(), ct);
                            notify(VpnFileModel.SavedText(saved));
                            break;
                        case IncomingFileChoice.Reject:
                            await reject(file, ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is SyncServerException or SyncUnreachableException or InvalidOperationException
                                               or HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    // Accettazione non riuscita: si potrà riprovare al prossimo avvio (il file è ancora sul server).
                    notify(VpnFileModel.ReceiveError(ex));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
