using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("Errore imprevisto:\n" + e.Exception.Message, "RdpManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        // «--dati <file>»: un elenco di connessioni diverso da quello predefinito (prove, PC condivisi).
        int i = Array.FindIndex(args, a => a.Equals("--dati", StringComparison.OrdinalIgnoreCase));
        Application.Run(i >= 0 && i + 1 < args.Length
            ? new MainForm(new ConnectionStore(Path.GetFullPath(args[i + 1])), checkUpdatesOnStartup: true, backgroundChecks: true)
            : new MainForm());
    }
}
