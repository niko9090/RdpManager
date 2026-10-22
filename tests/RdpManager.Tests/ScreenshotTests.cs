using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Fact eseguito solo se RDPMANAGER_SCREENSHOTS indica una cartella (altrimenti saltato).</summary>
public sealed class ScreenshotFactAttribute : FactAttribute
{
    public ScreenshotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScreenshotTests.FolderVariable)))
            Skip = $"Schermate dell'interfaccia: imposta {ScreenshotTests.FolderVariable}=<cartella> per salvarle.";
    }
}

/// <summary>
/// Schermate dell'interfaccia 1.2 (tema scuro, Panoramica, ricerca rapida, vista affiancata, impostazioni) con
/// connessioni inventate verso host inesistenti; lo stato «connessa» è simulato, quindi le miniature mostrano il
/// segnaposto. Uso: $env:RDPMANAGER_SCREENSHOTS = "C:\cartella"; dotnet test --filter "FullyQualifiedName~Screenshot".
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class ScreenshotTests : IDisposable
{
    public const string FolderVariable = "RDPMANAGER_SCREENSHOTS";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerShots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [ScreenshotFact]
    public void SaveScreenshotsOfTheV12Interface()
    {
        var outDir = Environment.GetEnvironmentVariable(FolderVariable)!;
        Directory.CreateDirectory(outDir);
        var profile = new CredentialProfile { Name = "Amministratore", Username = "admin", Domain = "UFFICIO" };
        RdpConnection C(string name, string group, string label = "", string? color = null, bool favorite = false) =>
            new() { Name = name, Host = name.ToLowerInvariant().Replace(' ', '-') + ".invalid", Group = group, Label = label, Color = color, IsFavorite = favorite, CredentialProfileId = profile.Id };
        var doc = new ConnectionsDocument
        {
            Connections =
            [
                C("Magazzino", "Clienti/Rossi", "PROD", "#E67E22", favorite: true),
                C("Ufficio", "Clienti/Rossi", color: "#2E86DE"),
                C("Laboratorio", "Interni", "TEST", "#27AE60"),
                C("Server posta", "Interni", "PROD", "#C0392B", favorite: true),
            ],
            CredentialProfiles = [profile],
            QuickTexts = [new QuickText { Name = "Configurazione IP", Text = "ipconfig /all\n" }],
        };
        doc.Settings.Theme = AppTheme.Dark;
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(1280, 800) };
                form.Show();
                Pump(400);
                foreach (var c in form.Document.Connections)
                    form.OpenSession(c);
                Pump(300);
                var views = form.AllViews.ToList();
                foreach (var v in views.Take(3))
                    v.SetStateForTests(SessionState.Connected, "Connessa");
                form.Tabs.SelectedIndex = 0;
                Pump(300);
                Save(form, Path.Combine(outDir, "1-finestra-tema-scuro.png"));

                form.ExecuteShortcut(ShortcutAction.Overview);
                Pump(1200);
                Save(form.Overview!, Path.Combine(outDir, "2-panoramica.png"));
                form.Overview!.Close();
                Pump(300);

                form.QuickSearchShown = dlg =>
                {
                    dlg.QueryBox.Text = "magaz";
                    Pump(300);
                    Save(dlg, Path.Combine(outDir, "3-ricerca-rapida.png"));
                    dlg.DialogResult = DialogResult.Cancel;
                };
                form.ExecuteShortcut(ShortcutAction.QuickSearch);
                Pump(300);

                form.EnterSplit(views[0], views[1]);
                Pump(400);
                Save(form, Path.Combine(outDir, "4-vista-affiancata.png"));
                form.LeaveSplit();

                using (var settings = new SettingsDialog(form.Document.Settings.Clone()) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) })
                {
                    settings.Show();
                    Pump(400);
                    Save(settings, Path.Combine(outDir, "5-impostazioni.png"));
                }
                using (var edit = new ConnectionDialog(views[0].Connection.Clone(), form.Document.AllGroups(), "Modifica: Magazzino", form.Document.CredentialProfiles)
                       { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) })
                {
                    ThemeManager.Apply(edit);
                    edit.Show();
                    Pump(400);
                    Save(edit, Path.Combine(outDir, "6-connessione-profilo-gateway.png"));
                }

                // 1.3: sessione disconnessa con la spiegazione dell'errore (host inesistente → DNS).
                PumpUntil(() => views[3].State == SessionState.Disconnected, 30_000);
                form.Tabs.SelectedIndex = 3;
                Pump(400);
                Save(form, Path.Combine(outDir, "7-errore-spiegato.png"));

                using (var settings = new SettingsDialog(form.Document.Settings.Clone()) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) })
                {
                    settings.Show();
                    var tabs = settings.Controls.OfType<TabControl>().First();
                    tabs.SelectedIndex = 2;
                    Pump(400);
                    Save(settings, Path.Combine(outDir, "8-impostazioni-avvio-aggiornamenti.png"));
                }

                var setup = Path.Combine(_dir, "RdpManager-Setup-9.9.9.exe");
                File.WriteAllText(setup, "finto");
                var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "https://github.com/niko9090/RdpManager/releases/tag/v9.9.9", null)
                {
                    Notes = string.Join(Environment.NewLine, "**Esempio di novità**", "- Panoramica più veloce", "- Correzioni varie"),
                };
                form.PortableOverride = false;
                form.Updates.Accept(release, new UpdateDownloadResult(UpdateDownloadStatus.Verified, setup, "ok", Sha256Sums.ComputeHex(setup)));
                form.ShowUpdateOffer();
                Pump(500);
                Save(form.UpdateOffer!, Path.Combine(outDir, "9-avviso-aggiornamento.png"));
                form.UpdateOffer!.Finish(UpdateChoice.Later);

                foreach (var v in views.Where(v => v.State == SessionState.Connected))
                    v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
                // 1.4: selezione multipla, F1, anteprima CSV, schermata di blocco.
                var conns = form.Document.Connections;
                form.SelectForTests(conns[0], Keys.None);
                form.SelectForTests(conns[1], Keys.Control);
                form.SelectForTests(conns[3], Keys.Control);
                Pump(300);
                Save(form, Path.Combine(outDir, "10-selezione-multipla.png"));
                form.SelectForTests(null, Keys.None);

                using (var f1 = new ShortcutsDialog(form.ShortcutLines()) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 60) })
                {
                    f1.Show();
                    Pump(400);
                    Save(f1, Path.Combine(outDir, "11-scorciatoie-F1.png"));
                }

                var csv = CsvImport.Parse("Nome;Indirizzo;Porta;Utente;Cartella\nUfficio Nord;nord.example.invalid;3389;SEDE\\anna;Sedi\nMagazzino;magazzino.invalid;;;\nSenza host;;3389;;\n");
                using (var csvDlg = new CsvImportDialog("connessioni.csv", csv, form.Document, "Importate") { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) })
                {
                    csvDlg.Show();
                    Pump(400);
                    Save(csvDlg, Path.Combine(outDir, "12-importazione-csv.png"));
                }

                form.Document.Settings.LockPin = PinLock.Create("2580", 10_000);
                form.LockApp();
                Pump(600);
                if (form.LockScreen is { } lockScreen)
                {
                    lockScreen.PinBox.Text = "9";
                    lockScreen.TryUnlock();
                    Pump(200);
                    Save(lockScreen, Path.Combine(outDir, "13-blocco-pin.png"));
                    lockScreen.PinBox.Text = "2580";
                    lockScreen.TryUnlock();
                    Pump(300);
                }
                form.StartInstaller = _ => { };

                form.ExitApp();
                Pump(200);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>Schermo intero 1.5: niente in alto, barra a sinistra (con miniature), a destra, in basso e nascosta.</summary>
    [ScreenshotFact]
    public void SaveScreenshotsOfTheV15FullScreenBar()
    {
        var outDir = Environment.GetEnvironmentVariable(FolderVariable)!;
        Directory.CreateDirectory(outDir);
        RdpConnection C(string name, string group, string label = "", string? color = null, bool favorite = false) =>
            new() { Name = name, Host = name.ToLowerInvariant().Replace(' ', '-') + ".invalid", Group = group, Label = label, Color = color, IsFavorite = favorite };
        var doc = new ConnectionsDocument
        {
            Connections =
            [
                C("Magazzino", "Clienti/Rossi", "PROD", "#E67E22", favorite: true),
                C("Ufficio", "Clienti/Rossi", color: "#2E86DE"),
                C("Laboratorio", "Interni", "TEST", "#27AE60"),
                C("Server posta", "Interni", "PROD", "#C0392B", favorite: true),
                C("Contabilità", "Interni"),
            ],
            QuickTexts = [new QuickText { Name = "Configurazione IP", Text = "ipconfig /all\n" }],
        };
        doc.SaveWorkspace("Mattina", new WorkspaceLayout([doc.Connections[0].Id, doc.Connections[1].Id], null, null, true, null, null, 0.5));
        doc.Settings.Theme = AppTheme.Dark;
        Directory.CreateDirectory(_dir);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(store) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(1280, 800) };
                form.Show();
                Pump(400);
                foreach (var c in form.Document.Connections.Take(4))
                    form.OpenSession(c);
                Pump(300);
                var views = form.AllViews.ToList();
                PumpUntil(() => views.All(v => v.State == SessionState.Disconnected), 30_000);
                foreach (var v in views.Take(3))
                    v.SetStateForTests(SessionState.Connected, "Connessa");
                form.Tabs.SelectedIndex = 0;
                Pump(300);

                (string File, FullScreenBarSettings Bar, bool Reveal)[] shots =
                [
                    ("14-schermo-intero-barra-sinistra.png", new FullScreenBarSettings { Position = BarPosition.Left, Mode = BarMode.Fixed, SessionStyle = SessionListStyle.Thumbnails }, false),
                    ("15-schermo-intero-barra-destra.png", new FullScreenBarSettings { Position = BarPosition.Right, Mode = BarMode.Fixed, Width = 280 }, false),
                    ("16-schermo-intero-barra-in-basso.png", new FullScreenBarSettings { Position = BarPosition.Bottom, Mode = BarMode.Fixed, Width = 210 }, false),
                    ("17-schermo-intero-barra-nascosta.png", new FullScreenBarSettings { Position = BarPosition.Left, Mode = BarMode.Hidden }, false),
                    ("18-schermo-intero-barra-nascosta-richiamata.png", new FullScreenBarSettings { Position = BarPosition.Left, Mode = BarMode.Hidden }, true),
                ];
                form.ToggleFullScreen();
                foreach (var (file, bar, reveal) in shots)
                {
                    form.ApplyFullScreenBarForTests(bar);
                    form.HideFullScreenBarOverlayForTests();
                    if (reveal)
                        form.ToggleSidebar();
                    Pump(500);
                    Save(form, Path.Combine(outDir, file));
                }
                form.ToggleFullScreen();
                foreach (var v in views)
                    v.SetStateForTests(SessionState.Disconnected, "Disconnessa");
                form.ExitApp();
                Pump(200);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)));
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Save(Form form, string path)
    {
        form.Activate();
        Pump(250);
        var b = form.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        if (form is OverviewForm)
        {
            // La panoramica è esclusa dalle catture dello schermo: si fa disegnare direttamente.
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, b.Size));
        }
        else
        {
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Schermo non copiabile (sessione di Windows bloccata): la finestra si disegna da sola, senza il
                // contenuto dei controlli ActiveX.
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, b.Size));
            }
        }
        bmp.Save(path, ImageFormat.Png);

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

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }
}
