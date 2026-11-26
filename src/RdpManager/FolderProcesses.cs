using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RdpManager.Core;

namespace RdpManager;

/// <summary>
/// Processi che girano da un exe dentro una cartella, di qualunque utente e sessione (anche il servizio della VPN, SYSTEM
/// nella sessione 0): <see cref="Process.MainModule"/> non si legge per i processi di altri utenti, il percorso sì con
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>. Serve al programma di aggiornamento per non avviare l'installer finché qualcuno
/// tiene in uso i file da sostituire, e per scrivere nel log chi è.
/// </summary>
internal static class FolderProcesses
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static IReadOnlyList<string> Find(string folder)
    {
        var found = new List<string>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4)
                        continue;
                    var path = ImagePath(p.Id);
                    if (path is null || !InstallLocation.IsSameOrInside(path, folder))
                        continue;
                    found.Add($"{p.ProcessName} (PID {p.Id}, sessione {p.SessionId}, {path})");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    // processo appena terminato o percorso non valido: non conta
                }
            }
        }
        return found;
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
