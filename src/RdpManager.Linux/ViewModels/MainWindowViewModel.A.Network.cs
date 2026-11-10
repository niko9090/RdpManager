using System.Net.Sockets;
using RdpManager.Core;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.ViewModels;

// Blocco A: raggiungibilità dei PC (#13), Wake-on-LAN (#14), cerca PC in rete (#15).
public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<Guid, ReachabilityStatus> _reach = [];
    private bool _checking;

    /// <summary>Come si prova un PC (le prove lo sostituiscono: di norma una connessione TCP alla porta RDP).</summary>
    internal Func<RdpConnection, Task<ReachabilityResult>> ReachabilityProbe { get; set; } = c => HostReachability.CheckAsync(c);

    /// <summary>Come si invia il pacchetto di accensione (le prove lo sostituiscono).</summary>
    internal Func<string, Task> WakeSender { get; set; } = mac => WakeOnLan.SendAsync(mac);

    /// <summary>Attesa fra un controllo e l'altro dopo l'accensione (15 secondi, per un paio di minuti).</summary>
    internal TimeSpan WakeRecheckDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Sorgenti di «Cerca PC in rete» (le prove le sostituiscono).</summary>
    internal NetworkScanSources? ScanSources { get; set; }

    /// <summary>Ultimo esito della verifica per la connessione.</summary>
    public ReachabilityStatus ReachabilityOf(Guid id) => _reach.GetValueOrDefault(id);

    /// <summary>
    /// Prova la porta RDP delle connessioni (al massimo 8 alla volta) e aggiorna lo stato nell'elenco. Senza elenco: quelle
    /// selezionate se ce n'è, altrimenti tutte.
    /// </summary>
    public async Task CheckReachabilityAsync(IReadOnlyList<RdpConnection>? list = null, bool announce = false)
    {
        list ??= SelectedConnections.Count > 0 && announce ? SelectedConnections : Document.Connections.ToList();
        if (_checking && !announce)
            return;
        _checking = true;
        try
        {
            using var gate = new SemaphoreSlim(8);
            var tasks = list.Select(async c =>
            {
                await gate.WaitAsync();
                try { return (c, await ReachabilityProbe(c)); }
                finally { gate.Release(); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            foreach (var (c, r) in results)
                _reach[c.Id] = r.Status;
            ApplyReachabilityToTree();
            if (announce)
            {
                int ok = results.Count(r => r.Item2.IsReachable);
                StatusMessage = list.Count == 1
                    ? $"«{list[0].DisplayName}»: {(ok == 1 ? "raggiungibile" : "non raggiungibile")}"
                    : $"Raggiungibili {ok} su {list.Count}";
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private void ApplyReachabilityToTree()
    {
        foreach (var node in Flatten(Roots).Where(n => n.Connection is not null))
            node.Reachability = _reach.GetValueOrDefault(node.Connection!.Id);
    }

    /// <summary>Accende il PC selezionato con un pacchetto Wake-on-LAN, poi controlla per un paio di minuti quando risponde.</summary>
    public async Task WakeAsync()
    {
        if (SelectedNode?.Connection is not { } c)
            return;
        if (!WakeOnLan.IsValidMac(c.MacAddress))
        {
            if (await _dialogs.ConfirmAsync("Wake-on-LAN",
                    $"Per accendere «{c.DisplayName}» serve l'indirizzo MAC della sua scheda di rete.\n\nAprire la modifica della connessione?"))
                await EditSelectedAsync();
            return;
        }
        try
        {
            await WakeSender(c.MacAddress);
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            await _dialogs.ShowMessageAsync("Wake-on-LAN", "Invio non riuscito: " + ex.Message);
            return;
        }
        StatusMessage = $"Accensione inviata a «{c.DisplayName}» ({c.MacAddress}). Può servire un minuto.";
        // Si controlla per un paio di minuti quando il PC risponde.
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(WakeRecheckDelay);
            var r = await ReachabilityProbe(c);
            _reach[c.Id] = r.Status;
            ApplyReachabilityToTree();
            if (r.IsReachable)
            {
                StatusMessage = $"«{c.DisplayName}» è raggiungibile.";
                return;
            }
        }
    }

    // --- Cerca PC in rete (#15) ---

    public async Task ShowNetworkScanAsync()
    {
        var vm = new NetworkScanViewModel(Document, AddDiscoveredPcs, text => _dialogs.ConfirmAsync("Cerca PC in rete", text), ScanSources);
        try
        {
            await _dialogs.ShowAsync(vm);
        }
        finally
        {
            vm.CancelAll();
        }
    }

    /// <summary>Porta nell'elenco i PC trovati (senza doppioni), salva e controlla subito la loro raggiungibilità.</summary>
    internal DiscoveryImportResult AddDiscoveredPcs(IReadOnlyList<DiscoveredPc> pcs, string folder)
    {
        var result = DiscoveryImport.ImportInto(Document, pcs, folder);
        if (result.Added.Count > 0)
        {
            Save();
            RebuildTree();
            CheckAfterImport(result.Added);
        }
        return result;
    }
}
