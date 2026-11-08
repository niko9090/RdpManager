using System.Diagnostics;
using System.Text;

namespace RdpManager.Platform.Linux;

/// <summary>Un processo FreeRDP in esecuzione. Le password passano solo dallo stdin (mai dalla riga di comando).</summary>
public sealed class FreeRdpProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private int _exited;

    private FreeRdpProcess(Process process) => _process = process;

    /// <summary>Il processo è terminato (da solo o per <see cref="StopAsync"/>): codice d'uscita e ultime righe di errore.</summary>
    public event Action<int, string>? Exited;

    public bool HasExited => Volatile.Read(ref _exited) == 1;

    public int Id => _process.Id;

    /// <summary>Avvia il comando. Lancia <see cref="InvalidOperationException"/> con un messaggio in italiano se non parte.</summary>
    public static FreeRdpProcess Start(FreeRdpCommand command)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in command.Arguments)
            psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var result = new FreeRdpProcess(process);
        process.ErrorDataReceived += (_, e) => result.AppendError(e.Data);
        process.OutputDataReceived += (_, e) => result.AppendError(e.Data);
        process.Exited += (_, _) => result.OnExited();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Non riesco ad avviare {command.FileName}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"Non riesco ad avviare {command.FileName}: {ex.Message}", ex);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            foreach (var line in command.StdinLines)
                process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
            // Non si chiude: FreeRDP può leggere ancora; si chiude con il processo.
        }
        catch (IOException)
        {
            // Il processo è già uscito: lo dirà l'evento Exited.
        }
        return result;
    }

    private void AppendError(string? line)
    {
        if (line is null)
            return;
        lock (_stderr)
        {
            _stderr.AppendLine(line);
            if (_stderr.Length > 8192)
                _stderr.Remove(0, _stderr.Length - 4096);
        }
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1)
            return;
        int code;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        string err;
        lock (_stderr)
            err = _stderr.ToString().Trim();
        Exited?.Invoke(code, err);
    }

    /// <summary>Chiusura pulita: SIGTERM, poi SIGKILL se dopo il tempo concesso è ancora vivo.</summary>
    public async Task StopAsync(TimeSpan? grace = null)
    {
        if (HasExited)
            return;
        try
        {
            if (!OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-TERM", _process.Id.ToString() }, UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            else
                _process.CloseMainWindow();
            using var cts = new CancellationTokenSource(grace ?? TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // già terminato
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
