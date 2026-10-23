using System.Net;
using RdpManager.Core;

namespace RdpManager.UI;

/// <summary>Da dove vengono i dati della finestra «Cerca PC in rete» (sostituibili nelle prove).</summary>
internal sealed class NetworkScanSources
{
    public Func<IReadOnlyList<AdapterInfo>> Adapters { get; init; } = NetworkAdapters.ReadActive;
    public Func<CancellationToken, Task<TailscaleLookup>> Tailscale { get; init; } = ct => TailscaleCli.GetStatusAsync(cancellationToken: ct);
    public NetworkScanner Scanner { get; init; } = new();

    /// <summary>Prova della porta RDP dei dispositivi Tailscale (predefinito: connessione TCP).</summary>
    public Func<string, int, CancellationToken, Task<bool>> RdpCheck { get; init; } = async (host, port, ct) =>
        (await HostReachability.CheckAsync(host, port, TimeSpan.FromSeconds(2), ct)).IsReachable;

    /// <summary>Domanda sì/no all'utente (conferme della scansione).</summary>
    public Func<IWin32Window, string, bool> Confirm { get; init; } = (owner, text) =>
        MessageBox.Show(owner, text, "Cerca PC in rete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}

/// <summary>
/// «Strumenti → Cerca PC in rete»: schede Rete locale, Tailscale e Altre VPN. Niente parte da solo: la scansione e la
/// lettura di Tailscale cominciano solo con i pulsanti. I PC spuntati (o selezionati) vanno nella cartella scelta,
/// senza doppioni; la finestra resta aperta per altre ricerche.
/// </summary>
internal sealed class NetworkScanForm : Form
{
    private readonly ConnectionsDocument _doc;
    private readonly NetworkScanSources _sources;
    private readonly Func<IReadOnlyList<DiscoveredPc>, string, DiscoveryImportResult> _add;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _folder = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _addButton = DialogStyle.Button("Aggiungi i selezionati");
    private readonly Button _checkAll = DialogStyle.Button("Spunta tutti");
    private readonly Button _close = DialogStyle.Button("Chiudi", DialogResult.Cancel);
    private readonly Label _summary = new() { AutoSize = true, Padding = new Padding(0, 8, 12, 0) };

    private readonly Button _tsRead = DialogStyle.Button("Leggi la rete Tailscale");
    private readonly CheckBox _tsCheckRdp = new() { Text = "Controlla se il Desktop remoto risponde (porta 3389)", AutoSize = true, Checked = true, Margin = new Padding(12, 7, 3, 3) };
    private readonly Label _tsMessage = new() { AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 6, 3, 6) };
    private readonly ListView _tsList = NewList(("Nome", 150), ("Nome MagicDNS", 230), ("IP", 110), ("Sistema", 80), ("In linea", 70), ("Desktop remoto", 110), ("Nell'elenco", 90));
    private CancellationTokenSource? _tsCts;

    public NetworkScanForm(ConnectionsDocument doc, Func<IReadOnlyList<DiscoveredPc>, string, DiscoveryImportResult> add,
        NetworkScanSources? sources = null)
    {
        _doc = doc;
        _add = add;
        _sources = sources ?? new NetworkScanSources();
        DialogStyle.Setup(this, "Cerca PC in rete", resizable: true);
        ShowInTaskbar = true;
        Size = new Size(900, 600);
        MinimumSize = new Size(720, 460);

        var adapters = SafeAdapters();
        LocalTab = new ScanTab(this, "Rete locale", adapters.Where(a => !a.IsVpn).ToList(),
            "Cerca i PC con il Desktop remoto acceso nella rete di questo PC. Scegli una rete oppure scrivi un intervallo " +
            "(es. 192.168.1.0/24 oppure 192.168.1.10-192.168.1.50). La ricerca parte solo quando premi «Avvia ricerca»: " +
            "usala solo su reti tue o che hai il permesso di controllare (nelle reti aziendali chiedi prima all'amministratore).",
            "Nessuna rete locale attiva trovata: scrivi un intervallo a mano.");
        VpnTab = new ScanTab(this, "Altre VPN", adapters.Where(a => a.IsVpn).ToList(),
            "Reti VPN attive riconosciute su questo PC (ZeroTier, WireGuard, OpenVPN, Tailscale e altre). Si cerca nella " +
            "sottorete della VPN scelta; se è più grande di una /22 si propone la /24 del tuo indirizzo. Per Tailscale è " +
            "meglio la scheda «Tailscale», che conosce già i nomi dei dispositivi.",
            "Nessuna VPN attiva riconosciuta (ZeroTier, WireGuard, OpenVPN o altre). Se la tua VPN usa un'altra rete, scrivi l'intervallo a mano.");
        _tabs.TabPages.Add(LocalTab.Page);
        _tabs.TabPages.Add(BuildTailscalePage());
        _tabs.TabPages.Add(VpnTab.Page);

        _folder.Items.AddRange(_doc.AllGroups().Prepend(DiscoveryImport.DefaultFolder).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray<object>());
        _folder.Text = DiscoveryImport.DefaultFolder;
        _addButton.Click += (_, _) => AddSelected();
        _checkAll.Click += (_, _) => CheckAll();
        _close.Click += (_, _) => Close();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(new Label { Text = "Cartella:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        bottom.Controls.Add(_folder);
        bottom.Controls.Add(_checkAll);
        bottom.Controls.Add(_addButton);
        bottom.Controls.Add(_summary);
        var closeRow = DialogStyle.Buttons(_close);
        Controls.Add(_tabs);
        Controls.Add(bottom);
        Controls.Add(closeRow);
        CancelButton = _close;
        FormClosing += (_, _) =>
        {
            LocalTab.Cancel();
            VpnTab.Cancel();
            _tsCts?.Cancel();
        };
        ThemeManager.Apply(this);
    }

    internal ScanTab LocalTab { get; }
    internal ScanTab VpnTab { get; }
    internal TabControl Tabs => _tabs;
    internal ListView TailscaleList => _tsList;
    internal string TailscaleMessage => _tsMessage.Text;
    internal string Summary => _summary.Text;
    internal ComboBox FolderBox => _folder;

    private IReadOnlyList<AdapterInfo> SafeAdapters()
    {
        try
        {
            return _sources.Adapters();
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
        {
            AppLog.Default?.Warning("Elenco delle schede di rete non disponibile: " + ex.Message);
            return [];
        }
    }

    internal static ListView NewList(params (string Text, int Width)[] columns)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = true,
            HideSelection = false, GridLines = false,
        };
        foreach (var (text, width) in columns)
            list.Columns.Add(text, width);
        return list;
    }

    // ---------- Tailscale ----------

    private TabPage BuildTailscalePage()
    {
        var page = new TabPage("Tailscale") { Padding = new Padding(8) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        top.Controls.Add(DialogStyle.Note(
            "Elenca i dispositivi della tua rete Tailscale leggendo «tailscale status --json» (sola lettura: non cambia " +
            "niente in Tailscale). Le connessioni useranno il nome MagicDNS, che resta valido anche se l'IP cambia.", 820));
        top.SetFlowBreak(top.Controls[^1], true);
        top.Controls.Add(_tsRead);
        top.Controls.Add(_tsCheckRdp);
        top.SetFlowBreak(_tsCheckRdp, true);
        top.Controls.Add(_tsMessage);
        _tsRead.Click += async (_, _) => await ReadTailscaleAsync();
        page.Controls.Add(_tsList);
        page.Controls.Add(top);
        return page;
    }

    internal async Task ReadTailscaleAsync()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _tsCts?.Cancel();
        _tsCts = new CancellationTokenSource();
        var ct = _tsCts.Token;
        _tsRead.Enabled = false;
        _tsList.Items.Clear();
        _tsMessage.ForeColor = ForeColor;
        _tsMessage.Text = "Lettura della rete Tailscale…";
        try
        {
            TailscaleLookup lookup;
            try
            {
                lookup = await _sources.Tailscale(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (IsDisposed)
                return;
            if (lookup.Outcome != TailscaleLookupOutcome.Ok || lookup.Status is null)
            {
                ShowTailscaleProblem(lookup.Message);
                return;
            }
            var status = lookup.Status;
            if (!status.IsRunning)
            {
                ShowTailscaleProblem(status.StateMessage);
                return;
            }
            var peers = status.Peers.Where(p => !p.IsSelf).ToList();
            foreach (var p in peers)
            {
                var pc = DiscoveredPc.FromTailscale(p);
                bool present = DiscoveryImport.IsAlreadyPresent(_doc, pc);
                var item = new ListViewItem([p.DisplayName, p.DnsName.Length > 0 ? p.DnsName : "(nessuno)", (p.IPv4 ?? p.IPv6)?.ToString() ?? "",
                    p.OS, p.Online ? "sì" : "no", p.Online ? (_tsCheckRdp.Checked ? "controllo…" : "") : "-", present ? "già presente" : ""])
                {
                    Tag = pc, Checked = p.Online && !present,
                };
                if (!p.Online)
                    item.ForeColor = SystemColors.GrayText;
                _tsList.Items.Add(item);
            }
            int online = peers.Count(p => p.Online);
            _tsMessage.Text = peers.Count == 0
                ? "Tailscale è connesso, ma nella tua rete non ci sono altri dispositivi."
                : $"{peers.Count} dispositivi nella rete Tailscale, {online} in linea" +
                  (status.MagicDnsSuffix.Length > 0 ? $" (rete {status.MagicDnsSuffix})." : ".");
            if (_tsCheckRdp.Checked)
                await CheckTailscaleRdpAsync(ct);
        }
        finally
        {
            if (!IsDisposed)
                _tsRead.Enabled = true;
        }
    }

    private void ShowTailscaleProblem(string message)
    {
        _tsMessage.ForeColor = Color.Firebrick;
        _tsMessage.Text = message;
    }

    private async Task CheckTailscaleRdpAsync(CancellationToken ct)
    {
        var items = _tsList.Items.Cast<ListViewItem>().Where(i => i.SubItems[4].Text == "sì").ToList();
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(items.Select(async item =>
        {
            var pc = (DiscoveredPc)item.Tag!;
            // Si prova l'IP 100.x: non dipende dal DNS di Tailscale.
            var host = pc.Aliases.FirstOrDefault(a => IpRange.TryParseIPv4(a, out _)) ?? pc.Address;
            bool ok;
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return; // finestra chiusa mentre si aspettava il turno
            }
            try
            {
                ok = await _sources.RdpCheck(host, pc.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                gate.Release();
            }
            if (!IsDisposed && item.ListView is not null)
            {
                item.SubItems[5].Text = ok ? "risponde" : "non risponde";
                if (!ok)
                    item.Checked = false;
            }
        }));
    }

    // ---------- aggiunta ----------

    private ListView CurrentList => _tabs.SelectedTab == LocalTab.Page ? LocalTab.List
        : _tabs.SelectedTab == VpnTab.Page ? VpnTab.List : _tsList;

    private void CheckAll()
    {
        var list = CurrentList;
        bool all = list.Items.Cast<ListViewItem>().All(i => i.Checked);
        foreach (ListViewItem i in list.Items)
            i.Checked = !all;
    }

    /// <summary>Aggiunge i PC spuntati della scheda attuale (se nessuno è spuntato, quelli selezionati).</summary>
    internal DiscoveryImportResult? AddSelected()
    {
        var list = CurrentList;
        var items = list.CheckedItems.Cast<ListViewItem>().ToList();
        if (items.Count == 0)
            items = list.SelectedItems.Cast<ListViewItem>().ToList();
        if (items.Count == 0)
        {
            _summary.Text = "Spunta o seleziona almeno un PC.";
            return null;
        }
        var folder = RdpConnection.NormalizeGroup(_folder.Text);
        var result = _add(items.Select(i => (DiscoveredPc)i.Tag!).ToList(), folder);
        foreach (var i in items)
        {
            i.Checked = false;
            i.SubItems[^1].Text = "già presente";
        }
        var where = folder.Length > 0 ? $"in «{folder}»" : "nella radice";
        _summary.Text = result.Added.Count == 0
            ? $"Nessun PC nuovo: {result.Duplicates} già nell'elenco."
            : $"Aggiunti {result.Added.Count} PC {where}" + (result.Duplicates > 0 ? $"; {result.Duplicates} già presenti, saltati." : ".");
        return result;
    }

    // ---------- scheda di scansione (Rete locale e Altre VPN) ----------

    /// <summary>Scheda con scelta della rete o intervallo scritto a mano, porta, avvio, annullamento e risultati.</summary>
    internal sealed class ScanTab
    {
        private readonly NetworkScanForm _owner;
        private readonly ComboBox _network = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        private readonly TextBox _range = new() { Width = 230 };
        private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = RdpConnection.DefaultPort, Width = 70 };
        private readonly Button _start = DialogStyle.Button("Avvia ricerca");
        private readonly Button _cancel = DialogStyle.Button("Annulla");
        private readonly ProgressBar _progress = new() { Width = 220, Height = 18, Margin = new Padding(3, 8, 3, 3) };
        private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3), MaximumSize = new Size(820, 0) };
        private CancellationTokenSource? _cts;
        private const string ManualText = "Intervallo scritto a mano";

        public ScanTab(NetworkScanForm owner, string title, IReadOnlyList<AdapterInfo> adapters, string note, string emptyNote)
        {
            _owner = owner;
            Adapters = adapters;
            Page = new TabPage(title) { Padding = new Padding(8) };
            List = NewList(("Nome", 200), ("Indirizzo", 140), ("Porta", 60), ("Nome DNS", 220), ("Nell'elenco", 100));

            foreach (var a in adapters)
                _network.Items.Add($"{a.Name} - {a.KindName} - {a.Address}/{a.Prefix}");
            _network.Items.Add(ManualText);
            _network.SelectedIndexChanged += (_, _) =>
            {
                if (SelectedAdapter is { } a)
                    _range.Text = $"{a.SuggestedRange.FirstAddress}-{a.SuggestedRange.LastAddress}";
            };
            _range.TextChanged += (_, _) =>
            {
                if (SelectedAdapter is { } a && _range.Text != $"{a.SuggestedRange.FirstAddress}-{a.SuggestedRange.LastAddress}")
                    _network.SelectedItem = ManualText;
            };
            _network.SelectedIndex = 0;
            _start.Click += async (_, _) => await StartAsync();
            _cancel.Click += (_, _) => Cancel();
            _cancel.Enabled = false;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            top.Controls.Add(DialogStyle.Note(note, 820));
            top.SetFlowBreak(top.Controls[^1], true);
            if (adapters.Count == 0)
            {
                var empty = DialogStyle.Note(emptyNote, 820);
                empty.ForeColor = Color.DarkGoldenrod;
                top.Controls.Add(empty);
                top.SetFlowBreak(empty, true);
            }
            top.Controls.Add(Lbl("Rete:"));
            top.Controls.Add(_network);
            top.SetFlowBreak(_network, true);
            top.Controls.Add(Lbl("Intervallo:"));
            top.Controls.Add(_range);
            top.Controls.Add(Lbl("Porta:"));
            top.Controls.Add(_port);
            top.Controls.Add(_start);
            top.Controls.Add(_cancel);
            top.SetFlowBreak(_cancel, true);
            top.Controls.Add(_progress);
            top.Controls.Add(_status);
            Page.Controls.Add(List);
            Page.Controls.Add(top);
        }

        private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        public TabPage Page { get; }
        public ListView List { get; }
        public IReadOnlyList<AdapterInfo> Adapters { get; }
        public bool IsScanning => _cts is not null;
        internal string StatusText => _status.Text;
        internal TextBox RangeBox => _range;
        internal NumericUpDown PortBox => _port;
        internal ComboBox NetworkBox => _network;
        internal Task? LastScan { get; private set; }

        private AdapterInfo? SelectedAdapter =>
            _network.SelectedIndex >= 0 && _network.SelectedIndex < Adapters.Count ? Adapters[_network.SelectedIndex] : null;

        public void Cancel() => _cts?.Cancel();

        /// <summary>Avvia la ricerca sull'intervallo scritto, dopo le conferme richieste da <see cref="ScanPolicy"/>.</summary>
        internal Task StartAsync() => LastScan = RunAsync();

        private async Task RunAsync()
        {
            if (_cts is not null)
                return;
            // Le continuazioni dopo gli await devono tornare sul thread della finestra.
            if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (!IpRange.TryParse(_range.Text, out var range, out var error))
            {
                SetStatus(error, problem: true);
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
                if (!_owner._sources.Confirm(_owner, ScanPolicy.Describe(check, range)))
                {
                    SetStatus("Ricerca non avviata.", problem: false);
                    return;
                }
                if (check == ScanCheck.NeedsSizeConfirmation)
                    sizeOk = true;
                else
                    publicOk = true;
            }

            int port = (int)_port.Value;
            var source = SelectedAdapter?.KindName ?? (_owner.VpnTab == this ? "VPN" : "Rete locale");
            _cts = new CancellationTokenSource();
            _start.Enabled = false;
            _cancel.Enabled = true;
            List.Items.Clear();
            _progress.Maximum = (int)Math.Min(range.Count, int.MaxValue);
            _progress.Value = 0;
            SetStatus($"Ricerca in {range} sulla porta {port}…", problem: false);
            var progress = new UiProgress<ScanProgress>(_owner, p =>
            {
                if (_cts is null)
                    return; // arrivato dopo la fine della ricerca
                _progress.Value = Math.Min(p.Done, _progress.Maximum);
                _status.Text = $"Controllati {p.Done:N0} di {p.Total:N0} indirizzi, trovati {p.Found}…";
            });
            try
            {
                var hits = await _owner._sources.Scanner.ScanAsync(range, new ScanOptions { Port = port }, progress, _cts.Token);
                if (_owner.IsDisposed)
                    return;
                foreach (var h in hits)
                    AddRow(DiscoveredPc.FromScan(h, source), h.HostName);
                SetStatus(hits.Count == 0
                    ? $"Nessun PC con il Desktop remoto acceso in {range} (porta {port})."
                    : $"Trovati {hits.Count} PC con il Desktop remoto acceso in {range} (porta {port}).", problem: false);
            }
            catch (OperationCanceledException)
            {
                if (!_owner.IsDisposed)
                    SetStatus("Ricerca annullata.", problem: false);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                if (!_owner.IsDisposed)
                {
                    _start.Enabled = true;
                    _cancel.Enabled = false;
                }
            }
        }

        private void AddRow(DiscoveredPc pc, string? dnsName)
        {
            bool present = DiscoveryImport.IsAlreadyPresent(_owner._doc, pc);
            List.Items.Add(new ListViewItem([pc.Name, pc.Address, pc.Port.ToString(), dnsName ?? "", present ? "già presente" : ""])
            {
                Tag = pc, Checked = !present,
            });
        }

        private void SetStatus(string text, bool problem)
        {
            _status.ForeColor = problem ? Color.Firebrick : _owner.ForeColor;
            _status.Text = text;
        }
    }

    /// <summary>
    /// Avanzamento riportato sul thread della finestra con BeginInvoke; scartato se la finestra è già chiusa
    /// (le ultime notizie dello scanner possono arrivare dopo l'annullamento).
    /// </summary>
    private sealed class UiProgress<T>(Control owner, Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            if (owner.IsDisposed || !owner.IsHandleCreated)
                return;
            try
            {
                owner.BeginInvoke(() =>
                {
                    if (!owner.IsDisposed)
                        handler(value);
                });
            }
            catch (InvalidOperationException)
            {
                // finestra chiusa nel frattempo
            }
        }
    }
}
