using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using RdpManager.Core;

namespace RdpManager.Linux.ViewModels.Tools;

/// <summary>Da dove vengono i dati della finestra «Cerca PC in rete» (sostituibili nelle prove).</summary>
public sealed class NetworkScanSources
{
    public Func<IReadOnlyList<AdapterInfo>> Adapters { get; init; } = NetworkAdapters.ReadActive;

    public Func<CancellationToken, Task<TailscaleLookup>> Tailscale { get; init; } = Services.LinuxTailscale.GetStatusAsync;

    public NetworkScanner Scanner { get; init; } = new();

    /// <summary>Prova della porta RDP dei dispositivi Tailscale (predefinito: connessione TCP).</summary>
    public Func<string, int, CancellationToken, Task<bool>> RdpCheck { get; init; } = async (host, port, ct) =>
        (await HostReachability.CheckAsync(host, port, TimeSpan.FromSeconds(2), ct)).IsReachable;
}

/// <summary>Un PC trovato, con la casella per sceglierlo.</summary>
public sealed class FoundPcItem : ViewModelBase
{
    private bool _checked;
    private string _note, _detail;

    public FoundPcItem(DiscoveredPc pc, string detail, string note, bool isChecked)
    {
        Pc = pc;
        _detail = detail;
        _note = note;
        _checked = isChecked;
    }

    public DiscoveredPc Pc { get; }

    public string Name => Pc.Name;

    public string Address => Pc.Address;

    public string Port => Pc.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Nome DNS (rete) oppure sistema e stato (Tailscale).</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>«già presente», «risponde», «non risponde»…</summary>
    public string Note { get => _note; set => Set(ref _note, value); }

    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

/// <summary>Una scheda della ricerca: i suoi risultati e il messaggio di stato.</summary>
public abstract class ScanTabViewModel : ViewModelBase
{
    private string _status = "";
    private bool _statusIsProblem, _busy;

    public abstract string Title { get; }

    public ObservableCollection<FoundPcItem> Results { get; } = [];

    public string Status { get => _status; protected set => Set(ref _status, value); }

    public bool StatusIsProblem { get => _statusIsProblem; protected set => Set(ref _statusIsProblem, value); }

    public bool IsBusy
    {
        get => _busy;
        protected set { if (Set(ref _busy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !_busy;

    protected void SetStatus(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }
}

/// <summary>Scheda «Rete locale» o «Altre VPN»: scelta della rete o intervallo scritto a mano, porta, avvio e annullamento.</summary>
public sealed class RangeScanTabViewModel : ScanTabViewModel
{
    public const string ManualText = "Intervallo scritto a mano";

    private readonly NetworkScanViewModel _owner;
    private readonly string _title;
    private ChoiceItem<AdapterInfo?> _network;
    private string _range = "", _port = RdpConnection.DefaultPort.ToString(CultureInfo.InvariantCulture);
    private double _progress, _progressMax = 1;
    private CancellationTokenSource? _cts;
    private bool _updating;

    internal RangeScanTabViewModel(NetworkScanViewModel owner, string title, IReadOnlyList<AdapterInfo> adapters, string note, string emptyNote)
    {
        _owner = owner;
        _title = title;
        Note = note;
        Adapters = adapters;
        EmptyNote = adapters.Count == 0 ? emptyNote : null;
        Networks = [.. adapters.Select(a => new ChoiceItem<AdapterInfo?>(a, $"{a.Name} - {a.KindName} - {a.Address}/{a.Prefix}")),
            new ChoiceItem<AdapterInfo?>(null, ManualText)];
        _network = Networks[0];
        ApplyNetwork();
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => IsIdle);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    public override string Title => _title;

    public string Note { get; }

    /// <summary>Avviso se non c'è nessuna rete di questo tipo (null altrimenti).</summary>
    public string? EmptyNote { get; }

    public IReadOnlyList<AdapterInfo> Adapters { get; }

    public IReadOnlyList<ChoiceItem<AdapterInfo?>> Networks { get; }

    public RelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public ChoiceItem<AdapterInfo?> Network
    {
        get => _network;
        set
        {
            if (value is null || !Set(ref _network, value))
                return;
            ApplyNetwork();
        }
    }

    /// <summary>Intervallo da cercare: «192.168.1.0/24» o «192.168.1.10-192.168.1.50».</summary>
    public string Range
    {
        get => _range;
        set
        {
            if (!Set(ref _range, value) || _updating)
                return;
            // Scritto a mano: la scelta della rete passa a «intervallo a mano», come in Windows.
            if (_network.Value is { } a && value != SuggestedText(a))
                Network = Networks[^1];
        }
    }

    public string Port { get => _port; set => Set(ref _port, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public double ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    private static string SuggestedText(AdapterInfo a) => $"{a.SuggestedRange.FirstAddress}-{a.SuggestedRange.LastAddress}";

    private void ApplyNetwork()
    {
        if (_network.Value is not { } a)
            return;
        _updating = true;
        Range = SuggestedText(a);
        _updating = false;
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Avvia la ricerca sull'intervallo scritto, dopo le conferme richieste da <see cref="ScanPolicy"/>.</summary>
    public async Task StartAsync()
    {
        if (_cts is not null)
            return;
        if (!IpRange.TryParse(Range, out var range, out var error))
        {
            SetStatus(error, problem: true);
            return;
        }
        if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            SetStatus("La porta deve essere un numero da 1 a 65535.", problem: true);
            return;
        }
        bool sizeOk = false, publicOk = false;
        while (true)
        {
            var check = ScanPolicy.Check(range, sizeOk, publicOk);
            if (check == ScanCheck.Ok)
                break;
            if (check == ScanCheck.TooLarge)
            {
                SetStatus(ScanPolicy.Describe(check, range), problem: true);
                return;
            }
            if (!await _owner.Confirm(ScanPolicy.Describe(check, range)))
            {
                SetStatus("Ricerca non avviata.", problem: false);
                return;
            }
            if (check == ScanCheck.NeedsSizeConfirmation)
                sizeOk = true;
            else
                publicOk = true;
        }

        var source = Network.Value?.KindName ?? (_owner.VpnTab == this ? "VPN" : "Rete locale");
        _cts = new CancellationTokenSource();
        IsBusy = true;
        RaiseCommands();
        Results.Clear();
        ProgressMax = Math.Max(1, Math.Min(range.Count, int.MaxValue));
        Progress = 0;
        SetStatus($"Ricerca in {range} sulla porta {port}…", problem: false);
        var progress = new Progress<ScanProgress>(p =>
        {
            if (_cts is null)
                return; // arrivato dopo la fine della ricerca
            Progress = Math.Min(p.Done, ProgressMax);
            Status = $"Controllati {p.Done:N0} di {p.Total:N0} indirizzi, trovati {p.Found}…";
        });
        try
        {
            var hits = await _owner.Sources.Scanner.ScanAsync(range, new ScanOptions { Port = port }, progress, _cts.Token);
            foreach (var h in hits)
            {
                var pc = DiscoveredPc.FromScan(h, source);
                bool present = DiscoveryImport.IsAlreadyPresent(_owner.Document, pc);
                Results.Add(new FoundPcItem(pc, h.HostName ?? "", present ? "già presente" : "", isChecked: !present));
            }
            SetStatus(hits.Count == 0
                ? $"Nessun PC con il Desktop remoto acceso in {range} (porta {port})."
                : $"Trovati {hits.Count} PC con il Desktop remoto acceso in {range} (porta {port}).", problem: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Ricerca annullata.", problem: false);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>Scheda «Tailscale»: legge i dispositivi della rete con il programma <c>tailscale</c>, solo quando si preme il pulsante.</summary>
public sealed class TailscaleTabViewModel : ScanTabViewModel
{
    private readonly NetworkScanViewModel _owner;
    private CancellationTokenSource? _cts;
    private bool _checkRdp = true;

    internal TailscaleTabViewModel(NetworkScanViewModel owner)
    {
        _owner = owner;
        ReadCommand = new RelayCommand(() => _ = ReadAsync(), () => IsIdle);
    }

    public override string Title => "Tailscale";

    public string Note => "Elenca i dispositivi della tua rete Tailscale (serve il programma «tailscale» installato e connesso). " +
                          "Non parte nulla da solo: la lettura comincia solo con il pulsante.";

    public RelayCommand ReadCommand { get; }

    /// <summary>Controlla se il Desktop remoto risponde (porta 3389) sui dispositivi in linea.</summary>
    public bool CheckRdp { get => _checkRdp; set => Set(ref _checkRdp, value); }

    public void Cancel() => _cts?.Cancel();

    public async Task ReadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        ReadCommand.RaiseCanExecuteChanged();
        Results.Clear();
        SetStatus("Lettura della rete Tailscale…", problem: false);
        try
        {
            TailscaleLookup lookup;
            try
            {
                lookup = await _owner.Sources.Tailscale(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (lookup.Outcome != TailscaleLookupOutcome.Ok || lookup.Status is null)
            {
                SetStatus(lookup.Message, problem: true);
                return;
            }
            var status = lookup.Status;
            if (!status.IsRunning)
            {
                SetStatus(status.StateMessage, problem: true);
                return;
            }
            var peers = status.Peers.Where(p => !p.IsSelf).ToList();
            foreach (var p in peers)
            {
                var pc = DiscoveredPc.FromTailscale(p);
                bool present = DiscoveryImport.IsAlreadyPresent(_owner.Document, pc);
                var detail = $"{(p.OS.Length > 0 ? p.OS + ", " : "")}{(p.Online ? "in linea" : "non in linea")}";
                Results.Add(new FoundPcItem(pc, detail, present ? "già presente" : p.Online && CheckRdp ? "controllo…" : "", p.Online && !present));
            }
            int online = peers.Count(p => p.Online);
            SetStatus(peers.Count == 0
                ? "Tailscale è connesso, ma nella tua rete non ci sono altri dispositivi."
                : $"{peers.Count} dispositivi nella rete Tailscale, {online} in linea" +
                  (status.MagicDnsSuffix.Length > 0 ? $" (rete {status.MagicDnsSuffix})." : "."), problem: false);
            if (CheckRdp)
                await CheckRdpAsync(ct);
        }
        finally
        {
            IsBusy = false;
            ReadCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task CheckRdpAsync(CancellationToken ct)
    {
        var online = Results.Where(i => i.Note == "controllo…").ToList();
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(online.Select(async item =>
        {
            var pc = item.Pc;
            // Si prova l'IP 100.x: non dipende dal DNS di Tailscale.
            var host = pc.Aliases.FirstOrDefault(a => IpRange.TryParseIPv4(a, out _)) ?? pc.Address;
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            bool ok;
            try
            {
                ok = await _owner.Sources.RdpCheck(host, pc.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                gate.Release();
            }
            item.Note = ok ? "risponde" : "non risponde";
            if (!ok)
                item.IsChecked = false;
        }));
    }
}

/// <summary>
/// «Strumenti → Cerca PC in rete» (#15): schede Rete locale, Tailscale e Altre VPN. Niente parte da solo. I PC spuntati
/// (o selezionati) vanno nella cartella scelta, senza doppioni; la finestra resta aperta per altre ricerche.
/// </summary>
public sealed class NetworkScanViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<DiscoveredPc>, string, DiscoveryImportResult> _add;
    private readonly Func<string, Task<bool>> _confirm;
    private ScanTabViewModel _selectedTab;
    private string _folder = DiscoveryImport.DefaultFolder;
    private string _summary = "";

    /// <param name="add">Porta nell'elenco i PC scelti (senza doppioni), salva e restituisce l'esito.</param>
    /// <param name="confirm">Domanda sì/no per le conferme della scansione.</param>
    public NetworkScanViewModel(ConnectionsDocument doc, Func<IReadOnlyList<DiscoveredPc>, string, DiscoveryImportResult> add,
        Func<string, Task<bool>> confirm, NetworkScanSources? sources = null)
    {
        Document = doc;
        _add = add;
        _confirm = confirm;
        Sources = sources ?? new NetworkScanSources();

        var adapters = SafeAdapters();
        LocalTab = new RangeScanTabViewModel(this, "Rete locale", adapters.Where(a => !a.IsVpn).ToList(),
            "Cerca i PC con il Desktop remoto acceso nella rete di questo computer. Scegli una rete oppure scrivi un intervallo " +
            "(es. 192.168.1.0/24 oppure 192.168.1.10-192.168.1.50). La ricerca parte solo quando premi «Avvia ricerca»: " +
            "usala solo su reti tue o che hai il permesso di controllare (nelle reti aziendali chiedi prima all'amministratore).",
            "Nessuna rete locale attiva trovata: scrivi un intervallo a mano.");
        TailscaleTab = new TailscaleTabViewModel(this);
        VpnTab = new RangeScanTabViewModel(this, "Altre VPN", adapters.Where(a => a.IsVpn).ToList(),
            "Reti VPN attive riconosciute su questo computer (ZeroTier, WireGuard, OpenVPN, Tailscale e altre). Si cerca nella " +
            "sottorete della VPN scelta; se è più grande di una /22 si propone la /24 del tuo indirizzo. Per Tailscale è " +
            "meglio la scheda «Tailscale», che conosce già i nomi dei dispositivi.",
            "Nessuna VPN attiva riconosciuta (ZeroTier, WireGuard, OpenVPN o altre). Se la tua VPN usa un'altra rete, scrivi l'intervallo a mano.");
        Tabs = [LocalTab, TailscaleTab, VpnTab];
        _selectedTab = LocalTab;
        Folders = [.. doc.AllGroups().Prepend(DiscoveryImport.DefaultFolder).Distinct(StringComparer.CurrentCultureIgnoreCase)];
        AddCommand = new RelayCommand(() => AddSelected());
        CheckAllCommand = new RelayCommand(CheckAll);
    }

    public string WindowTitle => "Cerca PC in rete";

    internal ConnectionsDocument Document { get; }

    internal NetworkScanSources Sources { get; }

    internal Task<bool> Confirm(string text) => _confirm(text);

    public RangeScanTabViewModel LocalTab { get; }

    public TailscaleTabViewModel TailscaleTab { get; }

    public RangeScanTabViewModel VpnTab { get; }

    public IReadOnlyList<ScanTabViewModel> Tabs { get; }

    public IReadOnlyList<string> Folders { get; }

    public RelayCommand AddCommand { get; }

    public RelayCommand CheckAllCommand { get; }

    public ScanTabViewModel SelectedTab
    {
        get => _selectedTab;
        set { if (value is not null) Set(ref _selectedTab, value); }
    }

    /// <summary>Cartella dove aggiungere i PC (vuota = radice).</summary>
    public string Folder { get => _folder; set => Set(ref _folder, value); }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Ferma le ricerche in corso (alla chiusura della finestra).</summary>
    public void CancelAll()
    {
        LocalTab.Cancel();
        VpnTab.Cancel();
        TailscaleTab.Cancel();
    }

    private IReadOnlyList<AdapterInfo> SafeAdapters()
    {
        try
        {
            return Sources.Adapters();
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
        {
            AppLog.Default?.Warning("Elenco delle schede di rete non disponibile: " + ex.Message);
            return [];
        }
    }

    private void CheckAll()
    {
        bool all = SelectedTab.Results.All(i => i.IsChecked);
        foreach (var i in SelectedTab.Results)
            i.IsChecked = !all;
    }

    /// <summary>Aggiunge i PC spuntati della scheda attuale.</summary>
    public DiscoveryImportResult? AddSelected()
    {
        var items = SelectedTab.Results.Where(i => i.IsChecked).ToList();
        if (items.Count == 0)
        {
            Summary = "Spunta almeno un PC.";
            return null;
        }
        var folder = RdpConnection.NormalizeGroup(Folder);
        var result = _add(items.Select(i => i.Pc).ToList(), folder);
        foreach (var i in items)
        {
            i.IsChecked = false;
            i.Note = "già presente";
        }
        var where = folder.Length > 0 ? $"in «{folder}»" : "nella radice";
        Summary = result.Added.Count == 0
            ? $"Nessun PC nuovo: {result.Duplicates} già nell'elenco."
            : $"Aggiunti {result.Added.Count} PC {where}" + (result.Duplicates > 0 ? $"; {result.Duplicates} già presenti, saltati." : ".");
        return result;
    }
}
