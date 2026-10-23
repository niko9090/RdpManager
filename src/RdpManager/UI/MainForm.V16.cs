using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Novità della 1.6: finestra «Cerca PC in rete» (Strumenti e barra laterale).</summary>
public sealed partial class MainForm
{
    private NetworkScanForm? _networkScan;

    /// <summary>Finestra aperta di «Cerca PC in rete» (null se chiusa).</summary>
    internal NetworkScanForm? NetworkScan => _networkScan is { IsDisposed: false } f ? f : null;

    /// <summary>Fonti dei dati della finestra (le prove le sostituiscono con dati finti).</summary>
    internal NetworkScanSources? NetworkScanSourcesForTests { get; set; }

    /// <summary>Apre «Cerca PC in rete» (una sola alla volta). Non avvia nessuna ricerca: lo fa l'utente.</summary>
    internal void ShowNetworkScan()
    {
        if (NetworkScan is { } open)
        {
            open.Activate();
            return;
        }
        _networkScan = new NetworkScanForm(_doc, AddDiscoveredPcs, NetworkScanSourcesForTests);
        _networkScan.FormClosed += (_, _) => _networkScan = null;
        _networkScan.Show(this);
    }

    private DiscoveryImportResult AddDiscoveredPcs(IReadOnlyList<DiscoveredPc> pcs, string folder)
    {
        var result = DiscoveryImport.ImportInto(_doc, pcs, folder);
        if (result.Added.Count > 0)
        {
            SaveConnections();
            RefreshTree(result.Added[^1]);
            if (_backgroundChecks)
                _ = CheckReachabilityAsync(result.Added.ToList());
        }
        ShowNotice(result.Added.Count == 0
            ? "Nessun PC nuovo da aggiungere."
            : $"Aggiunti {result.Added.Count} PC trovati in rete" + (folder.Length > 0 ? $" in «{folder}»." : "."));
        return result;
    }
}
