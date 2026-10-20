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

                foreach (var v in views.Where(v => v.State == SessionState.Connected))
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
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        }
        bmp.Save(path, ImageFormat.Png);
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
