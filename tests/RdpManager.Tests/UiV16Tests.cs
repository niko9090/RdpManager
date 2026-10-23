using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Prove della finestra «Cerca PC in rete» (1.6.0) con fonti finte (schede di rete, scanner, Tailscale): nessuna
/// ricerca all'apertura, menu Strumenti e pulsante nella barra laterale, conferma oltre la /22, selezione multipla,
/// aggiunta a una cartella senza doppioni, messaggio chiaro se Tailscale manca. Nomi e indirizzi inventati.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class UiV16Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerUi16-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Fonti finte: conta scansioni e letture di Tailscale; nella rete rispondono .10, .20 e .30.</summary>
    private sealed class Fake
    {
        public int Probes, TailscaleCalls, Confirms;
        public bool ConfirmAnswer;
        public TailscaleLookup Tailscale = new(TailscaleLookupOutcome.NotInstalled, null, TailscaleCli.NotInstalledMessage);

        public static readonly IReadOnlyList<AdapterInfo> Adapters =
        [
            new("Ethernet", "Scheda di rete di prova", NetworkInterfaceType.Ethernet, IPAddress.Parse("192.168.50.5"), 24, NetworkKind.Local),
            new("ZeroTier One", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet, IPAddress.Parse("10.147.20.7"), 24, NetworkKind.ZeroTier),
            new("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, IPAddress.Parse("100.64.0.1"), 10, NetworkKind.Tailscale),
        ];

        public NetworkScanSources Sources => new()
        {
            Adapters = () => Adapters,
            Tailscale = _ => { TailscaleCalls++; return Task.FromResult(Tailscale); },
            Scanner = new NetworkScanner(
                (ip, _) => Task.FromResult<string?>(ip.GetAddressBytes()[3] == 10 ? "server-prova.esempio.lan" : null),
                async (ip, _, _, ct) =>
                {
                    Interlocked.Increment(ref Probes);
                    await Task.Delay(1, ct);
                    return ip.GetAddressBytes()[3] is 10 or 20 or 30;
                }),
            RdpCheck = (host, _, _) => Task.FromResult(host.EndsWith(".2")),
            Confirm = (_, _) => { Confirms++; return ConfirmAnswer; },
        };
    }

    private ConnectionStore StoreWith(params RdpConnection[] connections)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = connections.ToList() });
        return store;
    }

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "La prova UI non è terminata entro 120 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void WithScanWindow(ConnectionStore store, Fake fake, Action<MainForm, NetworkScanForm> body) => Run(() =>
    {
        using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1280, 800) };
        form.NetworkScanSourcesForTests = fake.Sources;
        form.Show();
        Pump(200);
        form.ShowNetworkScan();
        Pump(200);
        var scan = form.NetworkScan;
        Assert.NotNull(scan);
        body(form, scan!);
        scan!.Close();
        form.Close();
        Pump(100);
    });

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static bool PumpUntil(Func<bool> condition, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return condition();
    }

    private static void Await(Task? task)
    {
        Assert.NotNull(task);
        Assert.True(PumpUntil(() => task!.IsCompleted, 30_000), "operazione non terminata");
        task!.GetAwaiter().GetResult();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    [Fact]
    public void Opening_ReachableFromToolsMenuAndSidebar_AndStartsNothing()
    {
        var fake = new Fake();
        WithScanWindow(StoreWith(), fake, (form, scan) =>
        {
            var menu = Descendants(form).OfType<MenuStrip>().Single();
            var tools = menu.Items.Cast<ToolStripMenuItem>().Single(i => i.Text == "S&trumenti");
            Assert.Contains(tools.DropDownItems.OfType<ToolStripMenuItem>(), i => i.Text == "Cerca &PC in rete…");
            var sidebarButton = Descendants(form).OfType<ToolStrip>().SelectMany(t => t.Items.OfType<ToolStripButton>())
                .Single(b => b.Text == "Cerca PC");
            Assert.Contains("Tailscale", sidebarButton.ToolTipText);

            Assert.Equal(["Rete locale", "Tailscale", "Altre VPN"], scan.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
            Pump(300);
            Assert.Equal(0, fake.Probes);
            Assert.Equal(0, fake.TailscaleCalls);
            Assert.Empty(scan.LocalTab.List.Items);
            Assert.Empty(scan.TailscaleList.Items);

            // Rete locale: solo la scheda Ethernet, con la sua /24 proposta; Altre VPN: ZeroTier e Tailscale.
            Assert.Equal([NetworkKind.Local], scan.LocalTab.Adapters.Select(a => a.Kind));
            Assert.Equal("192.168.50.1-192.168.50.254", scan.LocalTab.RangeBox.Text);
            Assert.Equal([NetworkKind.ZeroTier, NetworkKind.Tailscale], scan.VpnTab.Adapters.Select(a => a.Kind));
            Assert.Equal("10.147.20.1-10.147.20.254", scan.VpnTab.RangeBox.Text);
            Assert.True(scan.LocalTab.List.CheckBoxes && scan.LocalTab.List.MultiSelect);

            // Una seconda apertura riporta in primo piano la stessa finestra.
            form.ShowNetworkScan();
            Assert.Same(scan, form.NetworkScan);
        });
    }

    [Fact]
    public void LocalScan_FindsPcs_AndAddsTheCheckedOnesWithoutDuplicates()
    {
        var fake = new Fake();
        var existing = new RdpConnection { Name = "Già qui", Host = "192.168.50.20" };
        WithScanWindow(StoreWith(existing), fake, (form, scan) =>
        {
            Await(scan.LocalTab.StartAsync());
            Assert.Equal(254, fake.Probes);
            Assert.Equal(0, fake.Confirms);
            var rows = scan.LocalTab.List.Items.Cast<ListViewItem>().ToList();
            Assert.Equal(["192.168.50.10", "192.168.50.20", "192.168.50.30"], rows.Select(r => r.SubItems[1].Text));
            Assert.Equal("server-prova", rows[0].Text);
            Assert.Equal("server-prova.esempio.lan", rows[0].SubItems[3].Text);
            Assert.Equal([true, false, true], rows.Select(r => r.Checked));
            Assert.Equal("già presente", rows[1].SubItems[^1].Text);
            Assert.Contains("Trovati 3 PC", scan.LocalTab.StatusText);

            // Si aggiunge anche quello già presente: viene saltato.
            rows[1].Checked = true;
            scan.FolderBox.Text = "Ufficio/Rete";
            var result = scan.AddSelected();
            Assert.NotNull(result);
            Assert.Equal(2, result!.Added.Count);
            Assert.Equal(1, result.Duplicates);
            Assert.Contains("Aggiunti 2 PC in «Ufficio/Rete»", scan.Summary);
            var doc = form.Document;
            Assert.Equal(3, doc.Connections.Count);
            Assert.All(doc.Connections.Skip(1), c => Assert.Equal("Ufficio/Rete", c.Group));
            Assert.All(rows, r => Assert.False(r.Checked));

            // Salvato su disco.
            var saved = new ConnectionStore(Path.Combine(_dir, "connections.json")).Load();
            Assert.Equal(3, saved.Connections.Count);

            // Di nuovo con le righe selezionate (non spuntate): nessun doppione.
            foreach (ListViewItem r in scan.LocalTab.List.Items)
                r.Selected = true;
            var again = scan.AddSelected();
            Assert.Empty(again!.Added);
            Assert.Equal(3, again.Duplicates);
            Assert.Equal(3, form.Document.Connections.Count);
        });
    }

    [Fact]
    public void LargeOrPublicRanges_AskFirst_AndInvalidRangesExplainWhy()
    {
        var fake = new Fake { ConfirmAnswer = false };
        WithScanWindow(StoreWith(), fake, (_, scan) =>
        {
            scan.LocalTab.RangeBox.Text = "10.0.0.0/21";
            Await(scan.LocalTab.StartAsync());
            Assert.Equal(1, fake.Confirms);
            Assert.Equal(0, fake.Probes);
            Assert.Contains("non avviata", scan.LocalTab.StatusText);

            scan.LocalTab.RangeBox.Text = "203.0.113.1-203.0.113.4";
            Await(scan.LocalTab.StartAsync());
            Assert.Equal(2, fake.Confirms);
            Assert.Equal(0, fake.Probes);

            fake.ConfirmAnswer = true;
            Await(scan.LocalTab.StartAsync());
            Assert.Equal(4, fake.Probes);

            scan.LocalTab.RangeBox.Text = "10.0.0.0/8";
            Await(scan.LocalTab.StartAsync());
            Assert.Contains("il massimo", scan.LocalTab.StatusText);

            scan.LocalTab.RangeBox.Text = "192.168.1";
            Await(scan.LocalTab.StartAsync());
            Assert.Contains("non è un", scan.LocalTab.StatusText);
            Assert.Equal(4, fake.Probes);
        });
    }

    [Fact]
    public void Scan_CanBeCancelled()
    {
        var fake = new Fake();
        WithScanWindow(StoreWith(), fake, (_, scan) =>
        {
            var slow = new NetworkScanSources
            {
                Adapters = () => Fake.Adapters,
                Scanner = new NetworkScanner((_, _) => Task.FromResult<string?>(null), async (_, _, _, ct) => { await Task.Delay(200, ct); return false; }),
            };
            using var slowScan = new NetworkScanForm(new ConnectionsDocument(), (_, _) => new DiscoveryImportResult([], 0), slow);
            slowScan.Show();
            var task = slowScan.LocalTab.StartAsync();
            Assert.True(PumpUntil(() => slowScan.LocalTab.IsScanning, 2000));
            slowScan.LocalTab.Cancel();
            Await(task);
            Assert.False(slowScan.LocalTab.IsScanning);
            Assert.Equal("Ricerca annullata.", slowScan.LocalTab.StatusText);
        });
    }

    [Fact]
    public void Tailscale_Missing_GivesAClearMessage()
    {
        var fake = new Fake();
        WithScanWindow(StoreWith(), fake, (_, scan) =>
        {
            scan.Tabs.SelectedIndex = 1;
            Await(scan.ReadTailscaleAsync());
            Assert.Equal(1, fake.TailscaleCalls);
            Assert.Contains("Tailscale non è installato", scan.TailscaleMessage);
            Assert.Empty(scan.TailscaleList.Items);
        });
    }

    [Fact]
    public void Tailscale_ListsDevices_AndAddsTheSelectedOnes()
    {
        var status = TailscaleStatus.Parse("""
        {
          "BackendState": "Running", "MagicDNSSuffix": "esempio.ts.net",
          "Self": { "HostName": "questo", "DNSName": "questo.esempio.ts.net.", "TailscaleIPs": ["100.64.0.1"], "Online": true },
          "Peer": {
            "a": { "HostName": "PC-Ufficio", "DNSName": "pc-ufficio.esempio.ts.net.", "OS": "windows", "TailscaleIPs": ["100.64.0.2"], "Online": true },
            "b": { "HostName": "Portatile", "DNSName": "portatile.esempio.ts.net.", "OS": "windows", "TailscaleIPs": ["100.64.0.3"], "Online": false },
            "c": { "HostName": "nas", "DNSName": "", "OS": "linux", "TailscaleIPs": ["100.64.0.4"], "Online": true }
          }
        }
        """);
        var fake = new Fake { Tailscale = new TailscaleLookup(TailscaleLookupOutcome.Ok, status, "") };
        var existing = new RdpConnection { Name = "NAS", Host = "100.64.0.4" };
        WithScanWindow(StoreWith(existing), fake, (form, scan) =>
        {
            scan.Tabs.SelectedIndex = 1;
            Await(scan.ReadTailscaleAsync());
            var rows = scan.TailscaleList.Items.Cast<ListViewItem>().ToList();
            Assert.Equal(["nas", "pc-ufficio", "portatile"], rows.Select(r => r.Text));
            Assert.Contains("3 dispositivi nella rete Tailscale, 2 in linea", scan.TailscaleMessage);
            Assert.Equal("(nessuno)", rows[0].SubItems[1].Text);
            Assert.Equal(["non risponde", "risponde", "-"], rows.Select(r => r.SubItems[5].Text));
            Assert.Equal([false, true, false], rows.Select(r => r.Checked)); // nas già presente (e senza RDP), portatile non in linea

            rows[2].Checked = true; // anche un PC spento si può aggiungere
            var result = scan.AddSelected();
            Assert.Equal(["pc-ufficio.esempio.ts.net", "portatile.esempio.ts.net"], result!.Added.Select(c => c.Host));
            Assert.All(result.Added, c => Assert.Equal(DiscoveryImport.DefaultFolder, c.Group));
            Assert.Equal(3, form.Document.Connections.Count);
        });
    }
}
