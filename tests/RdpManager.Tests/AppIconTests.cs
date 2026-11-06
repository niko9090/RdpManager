using System.Drawing;
using System.Windows.Forms;
using System.Xml.Linq;
using RdpManager.Core;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>
/// L'icona dell'app (1.10.x): file .ico a più dimensioni, dichiarato nei csproj e negli installer, incorporato nella dll;
/// l'exe e le finestre non devono mostrare l'icona generica di Windows.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public sealed class AppIconTests
{
    private static readonly string Root = FindRoot();
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 64, 128, 256];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RdpManager.sln non trovato");
    }

    private static string IconPath => Path.Combine(Root, "src", "RdpManager", "app.ico");

    [Fact]
    public void IconFile_HasAllSizes()
    {
        using var fs = File.OpenRead(IconPath);
        using var r = new BinaryReader(fs);
        Assert.Equal(0, r.ReadInt16());
        Assert.Equal(1, r.ReadInt16()); // tipo: icona
        int count = r.ReadInt16();
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int w = r.ReadByte();
            fs.Seek(15, SeekOrigin.Current);
            sizes.Add(w == 0 ? 256 : w);
        }
        Assert.Equal(ExpectedSizes, sizes.OrderBy(s => s).ToArray());
    }

    [Theory]
    [InlineData("src/RdpManager/RdpManager.csproj", "app.ico")]
    [InlineData("src/RdpManagerVpn/RdpManagerVpn.csproj", "..\\RdpManager\\app.ico")]
    public void Csproj_DeclaresApplicationIcon_AndFileExists(string csproj, string expected)
    {
        var path = Path.Combine(Root, csproj.Replace('/', Path.DirectorySeparatorChar));
        var icon = XDocument.Load(path).Descendants("ApplicationIcon").Single().Value;
        Assert.Equal(expected, icon);
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, icon))));
    }

    [Theory]
    [InlineData("RdpManager.iss")]
    [InlineData("RdpManagerVpn.iss")]
    public void Installers_UseIcon_ForSetupAndShortcuts(string iss)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "installer", iss));
        Assert.Contains(lines, l => l.StartsWith("SetupIconFile=") && l.EndsWith("app.ico"));
        var shortcuts = lines.Where(l => l.StartsWith("Name: \"{auto")).ToList();
        Assert.NotEmpty(shortcuts);
        Assert.All(shortcuts, l => Assert.Contains("IconFilename: \"{app}\\{#AppExe}\"", l));
    }

    [Fact]
    public void BuiltExe_HasEmbeddedIcon_DifferentFromSystemOne()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "RdpManager.exe");
        Assert.True(File.Exists(exe), "RdpManager.exe non è accanto ai file di prova");
        using var icon = Icon.ExtractAssociatedIcon(exe)!;
        using var generic = SystemIcons.Application;
        using var mine = new Bitmap(icon.ToBitmap());
        using var sys = new Bitmap(generic.ToBitmap());
        Assert.False(SamePixels(mine, sys), "l'exe mostra l'icona generica di Windows");
    }

    [Fact]
    public void AppIcon_IsLoadedFromResource_AndNotTheDefault()
    {
        var icon = AppIcon.Instance;
        Assert.Same(icon, AppIcon.Instance);
        using var small = new Icon(icon, 16, 16);
        Assert.Equal(16, small.Width);
        using var a = new Bitmap(icon.ToBitmap());
        using var b = new Bitmap(SystemIcons.Application.ToBitmap());
        Assert.False(SamePixels(a, b));
    }

    [Fact]
    public void MainForm_AndDialogs_UseAppIcon()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RdpManagerIcon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                TouchUi.TouchScreenForTests = () => false;
                var store = new ConnectionStore(Path.Combine(dir, "connections.json"));
                using var main = new MainForm(store, systemIntegration: false);
                Assert.Same(AppIcon.Instance, main.Icon);
                using var dialog = new TouchForm();
                Assert.Same(AppIcon.Instance, dialog.Icon);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size)
            return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return false;
        return true;
    }
}
