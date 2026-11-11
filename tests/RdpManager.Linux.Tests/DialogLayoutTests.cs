using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RdpManager.Core;
using RdpManager.Linux;
using RdpManager.Linux.Dialogs;
using RdpManager.Linux.Features;
using RdpManager.Linux.Platform;
using RdpManager.Platform.Linux;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Tools;

namespace RdpManager.Linux.Tests;

/// <summary>Avvio dell'interfaccia senza schermo per le prove: gli stili sono quelli veri dell'app (App.axaml), le sue finestre no.</summary>
public static class DialogLayoutAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Disposizione dei dialoghi (k-84): ogni finestra che si apre dal programma sta nell'area di lavoro, si apre sopra la
/// principale, senza controlli sovrapposti, fuori dalla finestra o testi tagliati. Si usano i dialoghi veri (DialogService,
/// AvaloniaShellUi), con l'area di lavoro ricavata dallo schermo e poi ridotta, mai misure fisse in pixel.
/// </summary>
[Collection("Globali")]
public class DialogLayoutTests
{
    /// <summary>Nessun programma vero: la finestra della VPN si apre senza toccare wg, pkexec o ip.</summary>
    private sealed class NoRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(1, "", ""));
    }

    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(DialogLayoutAppBuilder));

    private sealed record Seen(string Name, List<string> Problems);

    private static readonly Rect NoRect = default;

    private static bool Overlap(Rect a, Rect b)
    {
        var i = a.Intersect(b);
        return i.Width > 1.5 && i.Height > 1.5;
    }

    private static IEnumerable<Control> Walk(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is not Control c)
                continue;
            yield return c;
            // L'interno di questi controlli è del controllo (modello, righe virtuali): non si guarda.
            if (c is TextBox or ComboBox or AutoCompleteBox or ListBox or TreeView or NumericUpDown or ScrollBar or Slider or ProgressBar)
                continue;
            foreach (var d in Walk(c))
                yield return d;
        }
    }

    private static string Describe(Control c) =>
        (c.Name is { Length: > 0 } n ? n : c.GetType().Name) + (c is TextBlock t && t.Text is { Length: > 0 } s ? ":" + (s.Length > 28 ? s[..28] : s) : "");

    /// <summary>Controlla la finestra aperta e restituisce i problemi trovati.</summary>
    private static List<string> Check(string name, Window w, Window owner, Size area)
    {
        var problems = new List<string>();
        var max = DialogFit.MaxSize(area);
        if (w.Bounds.Width > max.Width + 1 || w.Bounds.Height > max.Height + 1)
            problems.Add($"{name}: la finestra {w.Bounds.Width:0}x{w.Bounds.Height:0} è più grande dell'area di lavoro {area.Width:0}x{area.Height:0}");
        var frame = w.FrameSize ?? w.Bounds.Size;
        if (w.Position.X < -1 || w.Position.Y < -1 || w.Position.X + frame.Width > area.Width + 1 || w.Position.Y + frame.Height > area.Height + 1)
            problems.Add($"{name}: la finestra in {w.Position} ({frame.Width:0}x{frame.Height:0}) esce dall'area di lavoro {area.Width:0}x{area.Height:0}");

        foreach (var parent in new Visual[] { w }.Concat(Walk(w)))
        {
            var kids = parent.GetVisualChildren().OfType<Control>().Where(c => c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToList();
            bool isGrid = parent is Grid;
            bool isPanel = parent is StackPanel or WrapPanel or DockPanel or UniformGrid || isGrid;
            if (!isPanel)
                continue;
            foreach (var c in kids)
                if (parent is not Window && (c.Bounds.Right > parent.Bounds.Width + 1.5 || c.Bounds.Bottom > parent.Bounds.Height + 1.5) && parent is not Grid)
                    problems.Add($"{name}: «{Describe(c)}» {c.Bounds} esce da «{Describe((Control)parent)}» {parent.Bounds.Size}");
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    var a = kids[i];
                    var b = kids[j];
                    if (isGrid && Grid.GetRow(a) == Grid.GetRow(b) && Grid.GetColumn(a) == Grid.GetColumn(b))
                        continue; // stessa cella: sovrapposti per scelta (uno visibile alla volta)
                    if (isGrid && (Grid.GetRowSpan(a) > 1 || Grid.GetColumnSpan(a) > 1 || Grid.GetRowSpan(b) > 1 || Grid.GetColumnSpan(b) > 1))
                        continue;
                    if (Overlap(a.Bounds, b.Bounds))
                        problems.Add($"{name}: «{Describe(a)}» {a.Bounds} si sovrappone a «{Describe(b)}» {b.Bounds}");
                }
        }

        foreach (var t in Walk(w).OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0))
        {
            // Dentro un riquadro che scorre l'altezza in più non è un taglio.
            if (t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && t.TextTrimming == Avalonia.Media.TextTrimming.None
                && t.DesiredSize.Width - t.Margin.Left - t.Margin.Right > t.Bounds.Width + 1.5)
                problems.Add($"{name}: testo «{Describe(t)}» tagliato (serve {t.DesiredSize.Width:0}, ha {t.Bounds.Width:0})");
            if (t.TextWrapping != Avalonia.Media.TextWrapping.NoWrap && t.DesiredSize.Height - t.Margin.Top - t.Margin.Bottom > t.Bounds.Height + 1.5)
                problems.Add($"{name}: testo «{Describe(t)}» tagliato in altezza (serve {t.DesiredSize.Height:0}, ha {t.Bounds.Height:0})");
        }

        // I pulsanti di scelta (OK, Annulla, Chiudi...) devono vedersi: dentro la finestra, o dentro un riquadro che scorre in cui basta scorrere.
        foreach (var b in Walk(w).OfType<Button>().Where(b => b.IsEffectivelyVisible && (b.IsDefault || b.IsCancel)))
        {
            if (b.FindAncestorOfType<ScrollViewer>() is not null)
                continue;
            var p = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), w);
            if (p is { } pt && (pt.X > w.Bounds.Width + 1 || pt.Y > w.Bounds.Height + 1))
                problems.Add($"{name}: il pulsante «{Describe(b)}» è fuori dalla finestra ({pt.X:0},{pt.Y:0} in {w.Bounds.Width:0}x{w.Bounds.Height:0})");
        }
        return problems;
    }

    private static readonly Func<ConnectionsDocument> NewDoc = () =>
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Name = "Uno", Host = "uno.invalid", Username = "utente" });
        doc.CredentialProfiles.Add(new CredentialProfile { Name = "Admin", Username = "admin" });
        return doc;
    };

    /// <summary>Apre ogni dialogo con la sua funzione vera. Ognuno passa dal tracker, dove la prova prende la finestra.</summary>
    private static List<(string Name, Func<Task> Open)> Scenarios(DialogService dialogs, AvaloniaShellUi shell, Window owner, TempData data)
    {
        var doc = NewDoc();
        var fake = new FakeDialogs();
        var table = new CsvTable(';', ["Nome", "Host"], [["A", "a.invalid"], ["B", "b.invalid"]]);
        var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "https://example.invalid/releases/tag/v9.9.9", "9.9.9")
        {
            Notes = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"- novità numero {i} con una frase abbastanza lunga da andare a capo nella finestra")),
        };
        var history = new ConnectionHistory();
        var sync = new SyncService(Path.Combine(Path.GetDirectoryName(data.FilePath)!, "sync.state"));
        var longMessage = string.Join(" ", Enumerable.Repeat("Messaggio piuttosto lungo che deve andare a capo senza uscire dalla finestra.", 12));
        return
        [
            ("Conferma", () => dialogs.ConfirmAsync("Conferma", longMessage)),
            ("Conferma distruttiva", () => dialogs.ConfirmDestructiveAsync("Elimina", "Eliminare davvero?")),
            ("Messaggio", () => dialogs.ShowMessageAsync("Avviso", longMessage)),
            ("Domanda", () => dialogs.PromptAsync("Cartella", "Come si chiama la nuova cartella?", "")),
            ("Domanda segreta", () => dialogs.PromptSecretAsync("Password del file", "Scrivi la password")),
            ("Scelta modificabile", () => dialogs.ChooseAsync("Cartella", "Scegli", ["A", "B"], "A", editable: true)),
            ("Scelta", () => dialogs.ChooseAsync("Cartella", "Scegli", ["A", "B"], "A", editable: false)),
            ("Modifica connessione", () => dialogs.EditConnectionAsync(new ConnectionEditViewModel(doc, doc.Connections[0], false))),
            ("Nuova connessione", () => dialogs.EditConnectionAsync(new ConnectionEditViewModel(doc, new RdpConnection(), true))),
            ("Profili di credenziali", () => dialogs.ShowProfilesAsync(new ProfilesViewModel(doc, () => true, fake))),
            ("Impostazioni", () => dialogs.ShowSettingsAsync(new SettingsViewModel(new AppSettings(), "/x/connections.json"))),
            ("Impostazioni cartella", () => dialogs.ShowAsync(new FolderSettingsViewModel(doc, "Uffici"))),
            ("Ricerca rapida", () => dialogs.ShowAsync(new QuickSearchViewModel([doc.Connections[0]], [doc.Connections[0].Id]))),
            ("Cronologia", () => dialogs.ShowAsync(new HistoryViewModel(history, doc, null, _ => Task.FromResult(true)))),
            ("Testi rapidi", () => dialogs.ShowAsync(new QuickTextsViewModel(doc, _ => Task.FromResult(true)))),
            ("Esporta", () => dialogs.ShowAsync(new RdpmExportViewModel(doc, _ => Task.FromResult(true)))),
            ("Importa", () => dialogs.ShowAsync(new RdpmImportViewModel(3, 2, true, true, _ => Task.FromResult(true)))),
            ("Importa CSV", () => dialogs.ShowAsync(new CsvImportViewModel("elenco.csv", table, doc, ""))),
            ("Cerca PC in rete", () => dialogs.ShowAsync(new NetworkScanViewModel(doc, (_, _) => new DiscoveryImportResult([], 0), _ => Task.FromResult(true), new NetworkScanSources { Adapters = () => [] }))),
            ("PIN", () => dialogs.ShowAsync(new PinSetViewModel())),
            ("Spazi di lavoro", () => shell.ShowWorkspacesAsync(new WorkspacesViewModel(doc, () => true, _ => { }, fake))),
            ("Account", () => shell.ShowAccountAsync(new AccountViewModel(sync, () => Task.FromResult(new SyncOutcome(SyncStatus.UpToDate, "")), () => null, new NullShellUi(), fake))),
            ("Testo", () => shell.ShowTextAsync("Scorciatoie", string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Ctrl+Alt+{i}   azione numero {i}")))),
            ("Password del server", () => shell.AskSecretAsync("Password", "Scrivi la password del server remoto")),
            ("Avviso certificato", () => shell.AskCertificateAsync(new CertificatePrompt("pc.invalid", string.Join(" ", Enumerable.Repeat("Il certificato del server non corrisponde a quello visto la volta scorsa.", 8))))),
            ("Aggiornamento", () => shell.OfferUpdateAsync(release, "1.0.0")),
            ("VPN", () => shell.ShowVpnAsync(new VpnViewModel(
                new LinuxWireGuard(new NoRunner(), Path.Combine(Path.GetDirectoryName(data.FilePath)!, "vpn.conf"), LinuxVpnBackend.WgQuick), null,
                new NullShellUi(), fake, Path.Combine(Path.GetDirectoryName(data.FilePath)!, "vpn.json")))),
        ];
    }

    private static IEnumerable<Size> Areas(Window owner)
    {
        // L'area vera dello schermo (o, senza schermo, quella della finestra principale) e due più piccole ricavate da quella.
        var screen = owner.Screens?.Primary;
        var full = screen is null ? owner.Bounds.Size : new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        yield return full;
        yield return new Size(full.Width * 0.6, full.Height * 0.6);
        yield return new Size(full.Width * 0.33, full.Height * 0.4);
    }

    [Fact]
    public async Task Ogni_dialogo_sta_nell_area_di_lavoro_senza_sovrapposizioni_ne_testi_tagliati()
    {
        using var data = new TempData();
        var all = new List<string>();
        int opened = 0;
        await Session.Dispatch(async () =>
        {
            var owner = new Window { Width = 800, Height = 600, Content = new TextBlock { Text = "principale" } };
            owner.Show();
            var dialogs = new DialogService { Owner = owner };
            var shell = new AvaloniaShellUi { Owner = owner };
            string current = "";
            Size area = default;
            DialogTracker.PresenterForTests = async (w, o) =>
            {
                w.Show(o);
                // Le sezioni a scomparsa (es. «Avanzate» della VPN) si provano aperte: è il caso peggiore.
                foreach (var e in Walk(w).OfType<Expander>().ToList())
                    e.IsExpanded = true;
                for (int i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    w.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
                opened++;
                all.AddRange(Check(current, w, o, area).Select(p => $"[area {area.Width:0}x{area.Height:0}] {p}"));
                w.Close();
                await Task.CompletedTask;
            };
            try
            {
                foreach (var a in Areas(owner))
                {
                    area = a;
                    DialogFit.WorkAreaForTests = a;
                    foreach (var (name, open) in Scenarios(dialogs, shell, owner, data))
                    {
                        current = name;
                        await open();
                    }
                }
            }
            finally
            {
                DialogTracker.PresenterForTests = null;
                DialogFit.WorkAreaForTests = null;
                owner.Close();
            }
        }, CancellationToken.None);
        Assert.True(all.Count == 0, string.Join(Environment.NewLine, all.Distinct()));
        Assert.True(opened >= 3 * 25, $"dialoghi aperti: {opened}");
    }

    [Fact]
    public void Constrain_riduce_le_misure_e_mette_il_contenuto_in_un_riquadro_che_scorre()
    {
        Session.Dispatch(() =>
        {
            var w = new Window { Width = 900, Height = 700, MinWidth = 800, MinHeight = 600, SizeToContent = SizeToContent.Height, Content = new StackPanel() };
            DialogFit.Constrain(w, new Size(400, 300));
            var max = DialogFit.MaxSize(new Size(400, 300));
            Assert.True(w.Width <= max.Width && w.Height <= max.Height);
            Assert.True(w.MinWidth <= max.Width && w.MinHeight <= max.Height);
            Assert.True(w.MaxWidth <= max.Width && w.MaxHeight <= max.Height);
            Assert.IsType<ScrollViewer>(w.Content);
            DialogFit.Constrain(w, new Size(400, 300)); // idempotente: non annida un altro riquadro
            Assert.IsType<StackPanel>(((ScrollViewer)w.Content!).Content);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void ClampPosition_tiene_la_finestra_dentro_l_area()
    {
        var area = new PixelRect(0, 0, 640, 432);
        var size = new PixelSize(560, 400);
        Assert.Equal(new PixelPoint(80, 32), DialogFit.ClampPosition(new PixelPoint(300, 300), size, area));
        Assert.Equal(new PixelPoint(0, 0), DialogFit.ClampPosition(new PixelPoint(-50, -50), size, area));
        Assert.Equal(new PixelPoint(0, 0), DialogFit.ClampPosition(new PixelPoint(10, 10), new PixelSize(900, 900), area));
    }
}
