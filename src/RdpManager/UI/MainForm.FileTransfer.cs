using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>File in arrivo dagli altri PC dell'account: controllo ogni 30 secondi, avviso con «Accetta» e «Rifiuta».</summary>
public sealed partial class MainForm
{
    private IncomingFilesCoordinator _incomingFiles = null!;
    private readonly System.Windows.Forms.Timer _filesTimer = new() { Interval = VpnDashboardModel.RefreshSeconds * 1000 };

    private void InitFileTransfer()
    {
        _incomingFiles = new IncomingFilesCoordinator(
            () => _sync.SignedIn && !_locked && !IsDisposed,
            ct => _sync.GetIncomingFilesAsync(ct),
            (file, _) => Task.FromResult(IncomingFileForm.Ask(Visible ? this : null, file)),
            (file, folder, ct) => _sync.AcceptIncomingFileAsync(file, folder, ct),
            (file, ct) => _sync.RejectIncomingFileAsync(file, ct),
            text => { if (!IsDisposed) ShowNotice(text); });
        _filesTimer.Tick += async (_, _) =>
        {
            if (_shutdownCts.IsCancellationRequested)
                return;
            await _incomingFiles.CheckAsync(_shutdownCts.Token);
        };
        if (_autoSync)
            _filesTimer.Start();
        FormClosed += (_, _) => _filesTimer.Dispose();
    }
}
