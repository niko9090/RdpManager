using System.Text.RegularExpressions;

namespace RdpmSync;

/// <summary>
/// Scambio di file fra i PC dello stesso account (stessi token di /v1/vpn/overview). Il chiamante dice chi è con
/// l'intestazione <c>X-Device-Name</c> (un PC registrato nella VPN dell'account); il destinatario è un altro PC dello
/// stesso account. Sul disco il file si chiama come l'id generato dal server: il nome scelto dall'utente non tocca mai
/// un percorso. Un file sparisce dopo l'accettazione, il rifiuto, o dopo <see cref="SyncOptions.FileRetention"/>.
/// </summary>
public sealed partial class VpnEndpoints
{
    public const string DeviceHeader = "X-Device-Name";
    private const int MaxFileNameLength = 200;

    private delegate IResult? GateFn(HttpContext ctx, out TokenInfo t);

    private string TransferDir
    {
        get
        {
            var dir = options.TransferDirectory.Length > 0
                ? options.TransferDirectory
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "transfers");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string PathOf(string id) => Path.Combine(TransferDir, id + ".bin");

    /// <summary>Nome file accettabile: niente percorsi, caratteri di controllo o riservati, né «.» e «..».</summary>
    public static bool ValidFileName(string? n) =>
        n is { Length: >= 1 and <= MaxFileNameLength }
        && n == n.Trim() && !n.EndsWith('.')
        && n.All(ch => !char.IsControl(ch) && "/\\:*?\"<>|".IndexOf(ch) < 0)
        && n is not ("." or "..");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdRegex();

    /// <summary>Cancella i file non ritirati da troppo tempo (con la riga del database).</summary>
    public void PurgeExpiredFiles()
    {
        foreach (var t in store.ListTransfersOlderThan(_time.GetUtcNow() - options.FileRetention))
            Remove(t.Id);
    }

    private void Remove(string id)
    {
        try { File.Delete(PathOf(id)); }
        catch (IOException ex) { log.LogWarning(ex, "File in transito {Id} non cancellato", id); }
        store.DeleteTransfer(id);
    }

    private static object View(TransferInfo t) => new
    {
        id = t.Id, from = t.FromName, to = t.ToName, name = t.FileName, size = t.Size, createdUtc = t.CreatedUtc,
    };

    private void MapFiles(WebApplication app, GateFn gate)
    {
        // Il PC che chiama: deve essere un dispositivo dell'account, altrimenti non esiste per questo scambio.
        IResult? Caller(HttpContext ctx, out TokenInfo t, out VpnDevice me)
        {
            me = null!;
            if (gate(ctx, out t) is { } denied)
                return denied;
            var name = ctx.Request.Headers[DeviceHeader].ToString().Trim();
            if (name.Length == 0 || store.FindDevice(t.AccountId, name) is not { } d)
                return SyncApp.Err(400, "device_unknown",
                    "Questo PC non è registrato nella VPN del tuo account (manca o è sbagliata l'intestazione X-Device-Name).");
            me = d;
            return null;
        }

        app.MapPost("/v1/vpn/files", async (HttpContext ctx) =>
        {
            if (Caller(ctx, out var t, out var me) is { } denied)
                return denied;
            var to = ctx.Request.Query["to"].ToString().Trim();
            var fileName = ctx.Request.Query["name"].ToString();
            if (!ValidFileName(fileName))
                return SyncApp.Err(400, "invalid_file_name", "Nome del file non valido (niente percorsi o caratteri speciali).");
            if (store.FindDevice(t.AccountId, to) is not { } target)
                return SyncApp.Err(404, "recipient_not_found", "Il PC destinatario non è nel tuo account.");
            if (target.Id == me.Id)
                return SyncApp.Err(400, "same_device", "Scegli un altro PC: il file non si invia a se stessi.");
            if (ctx.Request.ContentLength is { } len && len > options.MaxFileBytes)
                return TooLarge();

            PurgeExpiredFiles();
            if (store.CountTransfers(t.AccountId) >= options.MaxPendingFiles)
                return SyncApp.Err(429, "too_many_files", "Ci sono già troppi file in attesa di essere ritirati.");

            var id = Guid.NewGuid().ToString("N");
            var path = PathOf(id);
            long size = 0;
            try
            {
                await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    {
                        size += read;
                        if (size > options.MaxFileBytes)
                            throw new FileTooLargeException();
                        await fs.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    }
                }
                if (size == 0)
                {
                    File.Delete(path);
                    return SyncApp.Err(400, "empty_file", "Il file è vuoto.");
                }
            }
            catch (FileTooLargeException)
            {
                TryDelete(path);
                return TooLarge();
            }
            catch
            {
                TryDelete(path);
                throw;
            }

            var info = new TransferInfo(id, t.AccountId, me.Name, target.Name, fileName, size, _time.GetUtcNow());
            store.InsertTransfer(info);
            log.LogInformation("File: {User} {From} → {To} ({Size} byte)", t.Username, me.Name, target.Name, size);
            return Results.Json(View(info), statusCode: 201);
        });

        app.MapGet("/v1/vpn/files", (HttpContext ctx) =>
        {
            if (Caller(ctx, out var t, out var me) is { } denied)
                return denied;
            PurgeExpiredFiles();
            return Results.Json(new { files = store.ListTransfersFor(t.AccountId, me.Name).Select(View) });
        });

        app.MapPost("/v1/vpn/files/{id}/accept", async (HttpContext ctx, string id) =>
        {
            if (Caller(ctx, out var t, out var me) is { } denied)
                return denied;
            PurgeExpiredFiles();
            if (!IdRegex().IsMatch(id) || store.FindTransfer(t.AccountId, me.Name, id) is not { } info || !File.Exists(PathOf(id)))
                return SyncApp.Err(404, "not_found", "Nessun file in arrivo con questo identificativo.");
            await using (var fs = new FileStream(PathOf(id), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = fs.Length;
                ctx.Response.Headers["X-File-Name"] = Uri.EscapeDataString(info.FileName);
                await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            // Cancellato solo a scaricamento finito: se la connessione cade prima, il file resta in attesa.
            Remove(id);
            log.LogInformation("File: {User} {To} ha accettato «{Name}»", t.Username, me.Name, info.FileName);
            return Results.Empty;
        });

        app.MapPost("/v1/vpn/files/{id}/reject", (HttpContext ctx, string id) =>
        {
            if (Caller(ctx, out var t, out var me) is { } denied)
                return denied;
            if (!IdRegex().IsMatch(id) || store.FindTransfer(t.AccountId, me.Name, id) is not { } info)
                return SyncApp.Err(404, "not_found", "Nessun file in arrivo con questo identificativo.");
            Remove(id);
            log.LogInformation("File: {User} {To} ha rifiutato «{Name}»", t.Username, me.Name, info.FileName);
            return Results.NoContent();
        });
    }

    private IResult TooLarge() =>
        SyncApp.Err(413, "too_large", $"Il file supera il limite di {options.MaxFileBytes / (1024 * 1024)} MB.");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* resta un file orfano: nessun record lo indica */ }
    }

    private sealed class FileTooLargeException : Exception;
}
