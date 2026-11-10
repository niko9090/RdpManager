using System.ComponentModel;
using System.Diagnostics;
using RdpManager.Core;

namespace RdpManager.Linux.Services;

/// <summary>
/// Lettura della rete Tailscale su Linux con <c>tailscale status --json</c>. Il Core cerca solo «tailscale.exe»:
/// qui si cerca «tailscale» nel PATH e nelle cartelle di sistema, poi si usa la stessa interpretazione del Core.
/// </summary>
public static class LinuxTailscale
{
    public const string NotInstalledMessage =
        "Tailscale non è installato su questo computer (non trovo il programma «tailscale»). Per vedere qui i PC della tua rete " +
        "Tailscale installalo da tailscale.com e accedi con il tuo account; le altre schede funzionano anche senza.";

    private static readonly string[] SystemFolders = ["/usr/bin", "/usr/sbin", "/usr/local/bin", "/bin", "/snap/bin"];

    /// <summary>Percorso di «tailscale»; null se non c'è.</summary>
    public static string? FindExecutable(Func<string, bool>? fileExists = null, string? path = null)
    {
        fileExists ??= File.Exists;
        var dirs = (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"')).Concat(SystemFolders);
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir, "tailscale");
                if (fileExists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // voce del PATH con caratteri non validi: si salta
            }
        }
        return null;
    }

    public static async Task<TailscaleLookup> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindExecutable();
        if (exe is null)
            return new TailscaleLookup(TailscaleLookupOutcome.NotInstalled, null, NotInstalledMessage);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Impossibile avviare tailscale: " + ex.Message);
        }
        if (process is null)
            return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Impossibile avviare tailscale.");
        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return TailscaleCli.Interpret(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new TailscaleLookup(TailscaleLookupOutcome.Failed, null, "Tailscale non ha risposto in tempo.");
            }
        }
    }
}
