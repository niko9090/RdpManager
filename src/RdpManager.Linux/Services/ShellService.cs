using System.ComponentModel;
using System.Diagnostics;

namespace RdpManager.Linux.Services;

/// <summary>Apre cartelle e indirizzi web con il programma predefinito del desktop (nelle prove è sostituito da un finto).</summary>
public interface IShellService
{
    /// <summary>Apre la cartella nel gestore di file (la crea se manca). Falso se non è stato possibile.</summary>
    bool OpenFolder(string path);

    /// <summary>Apre l'indirizzo nel browser. Falso se non è stato possibile.</summary>
    bool OpenUrl(string url);
}

/// <summary>Implementazione per Linux: <c>xdg-open</c>.</summary>
public sealed class XdgShellService : IShellService
{
    public bool OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return Open(path);
    }

    public bool OpenUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Open(uri.AbsoluteUri);

    private static bool Open(string target)
    {
        try
        {
            // Senza redirect: nessuno legge l'output, e un pipe pieno bloccherebbe xdg-open.
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            var process = Process.Start(psi);
            if (process is null)
                return false;
            // Il processo non si chiude subito: si aspetta che finisca (senza lasciare zombie) e solo allora si libera.
            _ = Task.Run(() =>
            {
                using (process)
                    process.WaitForExit();
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
