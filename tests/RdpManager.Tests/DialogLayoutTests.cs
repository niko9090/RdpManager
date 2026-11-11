using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.Platform;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// Disposizione dei dialoghi (k-84): ogni finestra che si apre dal programma deve stare nell'area di lavoro, aprirsi
/// centrata sopra la proprietaria, senza controlli sovrapposti né testi tagliati. Le misure derivano dall'area di lavoro
/// vera dello schermo (intera e ridotta), mai da pixel fissi.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class DialogLayoutTests
{
    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { TouchUi.WorkAreaForTests = null; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "La prova UI non è terminata entro 180 s");
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            // L'interno di numeri, tendine, elenchi e schede lo gestisce il controllo stesso.
            if (c is NumericUpDown or ComboBox or ListView or TreeView or ListBox or TextBox or ToolStrip)
                continue;
            foreach (var d in All(c))
                yield return d;
        }
    }

    private static object Make(Type type, params object[] args) =>
        Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, args, null)!;

    /// <summary>Ogni dialogo con il nome per il messaggio d'errore. Fabbriche: si crea un dialogo nuovo a ogni giro.</summary>
    private static List<(string Name, Func<Form> Make)> Dialogs()
    {
        var doc = new ConnectionsDocument
        {
            Connections = [new RdpConnection { Name = "Uno", Host = "uno.invalid", Username = "utente" }],
            CredentialProfiles = [new CredentialProfile { Name = "Admin", Username = "admin" }],
            QuickTexts = [new QuickText { Text = "dir" }],
        };
        var history = new ConnectionHistory();
        history.End(history.Start(doc.Connections[0], DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow, "Disconnessione locale", 1);
        var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "https://example.invalid/r", "9.9.9")
        {
            Notes = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"- novità numero {i} con una frase abbastanza lunga da andare a capo nel riquadro delle note")),
        };
        var table = new CsvTable(';', ["Nome", "Host"], [["A", "a.invalid"], ["B", "b.invalid"]]);
        return
        [
            ("ConnectionDialog", () => new ConnectionDialog(doc.Connections[0], doc.AllGroups(), "Modifica connessione", doc.CredentialProfiles)),
            ("ConnectionDialog nuova", () => new ConnectionDialog(new RdpConnection(), doc.AllGroups(), "Nuova connessione")),
            ("SettingsDialog", () => new SettingsDialog(new AppSettings { GlobalHotkey = "" })),
            ("FolderSettingsDialog", () => new FolderSettingsDialog("Uffici", null, doc.CredentialProfiles)),
            ("RdpmExportDialog", () => new RdpmExportDialog(doc)),
            ("RdpmImportDialog", () => new RdpmImportDialog(3, 5, hasPasswords: true, hasSettings: true)),
            ("FilePasswordDialog", () => (Form)Make(typeof(FilePasswordDialog), "connessioni-molto-lunghe.rdpm", false)),
            ("TextPromptDialog", () => (Form)Make(typeof(TextPromptDialog), "Nome della cartella", "Come si chiama la nuova cartella?", "", false)),
            ("NetworkScanForm", () => new NetworkScanForm(doc, (_, _) => new DiscoveryImportResult([], 0))),
            ("HistoryDialog", () => new HistoryDialog(history, doc, null)),
            ("CredentialProfilesDialog", () => new CredentialProfilesDialog(doc)),
            ("ProfileEditDialog", () => new ProfileEditDialog(new CredentialProfile(), "Nuovo profilo")),
            ("QuickTextsDialog", () => new QuickTextsDialog(doc)),
            ("QuickTextEditDialog", () => new QuickTextEditDialog(new QuickText(), "Nuovo testo")),
            ("MstscImportDialog", () => new MstscImportDialog([new MstscHistoryItem("pc.example.test", "mario")], doc)),
            ("WorkspacesDialog", () => new WorkspacesDialog(doc)),
            ("PinSetDialog", () => new PinSetDialog()),
            ("CsvImportDialog", () => new CsvImportDialog("elenco.csv", table, doc, "")),
            ("ShortcutsDialog", () => new ShortcutsDialog([new ShortcutLine("In sessione", "Ctrl+Alt+Home", "Mostra la barra")])),
            ("ChoiceDialog", () => new ChoiceDialog("Cartella", "Scegli", ["A", "B"], "A", editable: true)),
            ("SyncAccountDialog", () => new SyncAccountDialog(new SyncService(Path.Combine(Path.GetTempPath(), "rdpm-dl-" + Guid.NewGuid().ToString("N") + ".sync")),
                () => Task.FromResult(new SyncOutcome(SyncStatus.UpToDate, "")), () => null)),
            ("UpdateOfferForm", () => new UpdateOfferForm(new PendingUpdate(release, "x.exe", new string('0', 64)), "1.0.0")),
        ];
    }

    /// <summary>Aree di lavoro da provare: quella vera e due più piccole, tutte ricavate da quella dello schermo.</summary>
    private static IEnumerable<Rectangle> Areas()
    {
        var full = Screen.PrimaryScreen!.WorkingArea;
        yield return full;
        yield return new Rectangle(full.X, full.Y, full.Width * 3 / 4, full.Height * 3 / 4);
        yield return new Rectangle(full.X, full.Y, full.Width / 2, full.Height / 2);
    }

    private static bool Overlap(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        return i.Width > 1 && i.Height > 1;
    }

    internal static List<string> Problems(Form d, Rectangle area)
    {
        var problems = new List<string>();
        string name = d.GetType().Name;
        if (!area.Contains(d.Bounds))
            problems.Add($"{name}: la finestra {d.Bounds} esce dall'area di lavoro {area}");
        foreach (var parent in new[] { (Control)d }.Concat(All(d)).Where(p => p.Controls.Count > 0 && p is not (NumericUpDown or ComboBox or TabControl or ToolStrip or SplitContainer)))
        {
            var kids = parent.Controls.Cast<Control>().Where(c => c.Visible && c.Width > 0 && c.Height > 0).ToList();
            foreach (var c in kids)
            {
                // Contenuto più grande del contenitore: va bene solo se il contenitore scorre.
                if (!parent.GetType().IsSubclassOf(typeof(ScrollableControl)) || !((ScrollableControl)parent).AutoScroll)
                {
                    var box = parent is Form ? parent.ClientRectangle : parent.DisplayRectangle;
                    if (c.Dock == DockStyle.None && (c.Right > box.Right + 1 || c.Bottom > box.Bottom + 1 || c.Left < box.Left - 1 || c.Top < box.Top - 1))
                        problems.Add($"{name}: «{Describe(c)}» {c.Bounds} esce da «{Describe(parent)}» {box}");
                }
            }
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    var a = kids[i];
                    var b = kids[j];
                    if (a.Dock == DockStyle.Fill || b.Dock == DockStyle.Fill)
                        continue;
                    if (Overlap(a.Bounds, b.Bounds))
                        problems.Add($"{name}: «{Describe(a)}» {a.Bounds} si sovrappone a «{Describe(b)}» {b.Bounds}");
                }
        }
        foreach (var c in All(d).Where(c => c.Visible))
        {
            if (c is Label l && !string.IsNullOrWhiteSpace(l.Text))
            {
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                var need = TextRenderer.MeasureText(l.Text, l.Font, new Size(l.ClientSize.Width, int.MaxValue / 2), flags);
                if (!l.AutoEllipsis && (need.Height > l.ClientSize.Height + 2 || (!l.Text.Contains(' ') && need.Width > l.ClientSize.Width + 2)))
                    problems.Add($"{name}: etichetta «{Describe(l)}» tagliata (serve {need}, ha {l.ClientSize})");
            }
            else if (c is ButtonBase bb && !string.IsNullOrWhiteSpace(bb.Text) && !bb.AutoEllipsis && !bb.AutoSize)
            {
                int glyph = bb is Button ? 8 : TouchMetrics.Scale(24, bb.DeviceDpi);
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                var need = bb.Text.Contains('\n') ? Size.Empty : TextRenderer.MeasureText(bb.Text, bb.Font, new Size(int.MaxValue / 2, int.MaxValue / 2), flags);
                if (need.Width + glyph > bb.ClientSize.Width || (need.Height > bb.ClientSize.Height && need != Size.Empty))
                    problems.Add($"{name}: «{Describe(bb)}» ha il testo tagliato (serve {need.Width + glyph}x{need.Height}, ha {bb.ClientSize})");
            }
        }
        return problems;
    }

    private static string Describe(Control c) =>
        string.IsNullOrWhiteSpace(c.Text) ? $"{c.GetType().Name}#{c.Name}" : $"{c.GetType().Name}:{(c.Text.Length > 30 ? c.Text[..30] : c.Text)}";

    [Fact]
    public void EveryDialog_OpensCenteredOnItsOwner_InsideTheWorkArea_WithoutOverlapsOrClippedText()
    {
        OnSta(() =>
        {
            var all = new List<string>();
            foreach (var area in Areas())
            {
                TouchUi.WorkAreaForTests = area;
                using var owner = new Form
                {
                    FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(area.X + area.Width / 8, area.Y + area.Height / 8, area.Width * 3 / 4, area.Height * 3 / 4),
                    ShowInTaskbar = false,
                };
                owner.Show();
                Pump(60);
                foreach (var (name, make) in Dialogs())
                {
                    using var d = make();
                    d.Show(owner);
                    Pump(120);
                    foreach (var p in Problems(d, area))
                        all.Add($"[area {area.Width}x{area.Height}] {p}");
                    // Centrata sopra la proprietaria, salvo che l'area la costringa a spostarsi (tolleranza: 2 px per il bordo).
                    var wanted = new Point(owner.Bounds.X + (owner.Width - d.Width) / 2, owner.Bounds.Y + (owner.Height - d.Height) / 2);
                    wanted.X = Math.Max(area.Left, Math.Min(wanted.X, area.Right - d.Width));
                    wanted.Y = Math.Max(area.Top, Math.Min(wanted.Y, area.Bottom - d.Height));
                    if (Math.Abs(d.Left - wanted.X) > 3 || Math.Abs(d.Top - wanted.Y) > 3)
                        all.Add($"[area {area.Width}x{area.Height}] {name}: non è centrata sopra la proprietaria (sta in {d.Location}, doveva stare in {wanted})");
                    d.Close();
                }
            }
            Assert.True(all.Count == 0, string.Join(Environment.NewLine, all.Distinct()));
        });
    }
}
