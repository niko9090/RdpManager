using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using RdpManager.Core;

namespace RdpManagerVpn;

/// <summary>Carica un file di configurazione (<c>.rdpmconfig</c>) dall'icona, da riga di comando o dall'installer, e mostra il riepilogo.</summary>
internal static class ConfigCommand
{
    public const string Title = "RdpManager VPN";

    /// <summary>Finestra «Apri» per scegliere il file; null se si annulla.</summary>
    public static string? PickFile(IWin32Window? owner = null)
    {
        using var open = new OpenFileDialog
        {
            Title = "Carica file di configurazione",
            Filter = "File di configurazione RdpManager (*.rdpmconfig)|*.rdpmconfig|Configurazione WireGuard (*.conf)|*.conf|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
        };
        return open.ShowDialog(owner) == DialogResult.OK ? open.FileName : null;
    }

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Applica il file e mostra «Fatto» o «Da completare»; gli errori del file si elencano senza modificare niente. Restituisce 0 se è tutto a posto.</summary>
    public static async Task<int> RunAsync(string path, string exePath, string programData, IWin32Window? owner = null)
    {
        var work = ClientConfigApplier.DefaultWorkFolder();
        var installer = new ClientProfileInstaller(programData, exePath, work, IsAdministrator, temp => RunElevated(exePath, temp));
        try
        {
            using var wait = new WaitCursor();
            var report = await ClientConfigApplier.ApplyFileAsync(path, work, installer);
            MessageBox.Show(owner, report.Text, "RdpManager VPN: " + report.Title, MessageBoxButtons.OK,
                report.Complete ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return report.Complete ? 0 : 1;
        }
        catch (ConfigFileException ex)
        {
            var list = ex.Errors.Count == 1 ? ex.Errors[0] : string.Join("\n", ex.Errors.Select((e, i) => $"{i + 1}) {e}"));
            MessageBox.Show(owner, "Il file di configurazione non è valido, non ho cambiato niente.\n\n" + list, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            MessageBox.Show(owner, ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
    }

    private static int RunElevated(string exePath, string tempFile)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, Arguments = $"{Program.ApplyProfileArg} \"{tempFile}\"" });
            p!.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ClientProfileInstaller.UacDeclined;
        }
    }

    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor? _old = Cursor.Current;

        public WaitCursor() => Cursor.Current = Cursors.WaitCursor;

        public void Dispose() => Cursor.Current = _old;
    }
}
