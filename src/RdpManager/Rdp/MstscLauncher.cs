using System.Diagnostics;
using RdpManager.Core;

namespace RdpManager.Rdp;

/// <summary>
/// Alternativa al controllo incorporato: genera un .rdp temporaneo e avvia mstsc.exe.
/// La password, se presente, finisce nel file solo cifrata DPAPI; il file viene cancellato poco dopo.
/// </summary>
public static class MstscLauncher
{
    private static string TempFolder => Path.Combine(Path.GetTempPath(), "RdpManager");

    public static void Launch(RdpConnection connection)
    {
        var path = CreateTempRdpFile(connection);
        var mstsc = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
        Process.Start(new ProcessStartInfo(mstsc) { ArgumentList = { path }, UseShellExecute = false });

        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => TryDelete(path), TaskScheduler.Default);
    }

    /// <summary>Scrive il .rdp temporaneo per mstsc (password solo cifrata DPAPI) e ne restituisce il percorso.</summary>
    public static string CreateTempRdpFile(RdpConnection connection, string? folder = null)
    {
        folder ??= TempFolder;
        Directory.CreateDirectory(folder);
        var safeName = string.Concat(connection.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(folder, $"{safeName}-{Guid.NewGuid():N}.rdp");
        RdpFile.Save(path, connection, includePassword: true);
        return path;
    }

    /// <summary>Elimina eventuali file temporanei rimasti da esecuzioni precedenti.</summary>
    public static void CleanupOldFiles()
    {
        if (!Directory.Exists(TempFolder))
            return;
        foreach (var file in Directory.EnumerateFiles(TempFolder, "*.rdp"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5))
                TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
