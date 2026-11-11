using System.Diagnostics;

namespace RdpManager.Platform.Linux;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Esegue un programma esterno senza shell (argomenti separati). Si sostituisce nelle prove.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        Process? p = null;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("Avvio non riuscito: " + fileName);
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                p.StandardInput.Close();
            }
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Annullato: il programma esterno non deve restare vivo.
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Programma non installato: esito di errore, non eccezione.
            return new ProcessResult(127, "", ex.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }
}
