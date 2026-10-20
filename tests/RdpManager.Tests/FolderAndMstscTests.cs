using System.Text;
using RdpManager.Core;
using RdpManager.Rdp;

namespace RdpManager.Tests;

public class FolderTests
{
    private static ConnectionsDocument SampleDoc() => new()
    {
        Groups = ["Clienti", "Vuota"],
        Connections =
        [
            new RdpConnection { Name = "r1", Host = "r1", Group = "Clienti/Rossi" },
            new RdpConnection { Name = "r2", Host = "r2", Group = "Clienti/Rossi/Sede2" },
            new RdpConnection { Name = "b", Host = "b", Group = "Clienti/Bianchi" },
            new RdpConnection { Name = "x", Host = "x", Group = "ClientiBis" },
        ],
    };

    [Fact]
    public void AddFolder_CreatesNestedPathOnce()
    {
        var doc = SampleDoc();
        Assert.Equal("Clienti/Verdi", doc.AddFolder("Clienti", " Verdi "));
        Assert.Equal("Clienti/Verdi", doc.AddFolder("Clienti", "Verdi"));
        Assert.Single(doc.Groups, g => g == "Clienti/Verdi");
        Assert.Null(doc.AddFolder("Clienti", "  "));
        Assert.Contains("Clienti/Verdi", doc.AllGroups());
    }

    [Fact]
    public void RenameFolder_MovesSubfoldersAndConnectionsButNotSimilarNames()
    {
        var doc = SampleDoc();
        Assert.Equal("Clienti/Rossi Srl", doc.RenameFolder("Clienti/Rossi", "Rossi Srl"));

        Assert.Equal("Clienti/Rossi Srl", doc.Connections[0].Group);
        Assert.Equal("Clienti/Rossi Srl/Sede2", doc.Connections[1].Group);
        Assert.Equal("Clienti/Bianchi", doc.Connections[2].Group);
        Assert.Equal("ClientiBis", doc.Connections[3].Group);
        Assert.DoesNotContain(doc.AllGroups(), g => g.StartsWith("Clienti/Rossi/", StringComparison.Ordinal) || g == "Clienti/Rossi");
    }

    [Fact]
    public void RenameTopFolder_DoesNotTouchFolderWithSamePrefix()
    {
        var doc = SampleDoc();
        doc.RenameFolder("Clienti", "Aziende");
        Assert.Equal("Aziende/Rossi", doc.Connections[0].Group);
        Assert.Equal("ClientiBis", doc.Connections[3].Group);
        Assert.DoesNotContain("Clienti", doc.AllGroups());
    }

    [Fact]
    public void DeleteFolder_KeepsConnectionsMovingThemToParent()
    {
        var doc = SampleDoc();
        Assert.Equal("Clienti", doc.DeleteFolder("Clienti/Rossi"));
        Assert.Equal(4, doc.Connections.Count);
        Assert.Equal("Clienti", doc.Connections[0].Group);
        Assert.Equal("Clienti", doc.Connections[1].Group);
        Assert.Equal("Clienti/Bianchi", doc.Connections[2].Group);
        Assert.DoesNotContain(doc.AllGroups(), g => g.Contains("Rossi"));
        Assert.Contains("Vuota", doc.AllGroups());
    }

    [Fact]
    public void DeleteTopFolder_MovesConnectionsToRoot()
    {
        var doc = SampleDoc();
        Assert.Equal("", doc.DeleteFolder("Clienti"));
        Assert.All(doc.Connections.Take(3), c => Assert.Equal("", c.Group));
        Assert.Equal(["ClientiBis", "Vuota"], doc.AllGroups());
    }

    [Fact]
    public void FolderChanges_SurviveSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"RdpManagerTests-{Guid.NewGuid():N}.json");
        try
        {
            var doc = SampleDoc();
            doc.AddFolder("", "Nuova vuota");
            doc.RenameFolder("Clienti/Bianchi", "Neri");
            var store = new ConnectionStore(path);
            store.Save(doc);
            var loaded = store.Load();
            Assert.Contains("Nuova vuota", loaded.AllGroups());
            Assert.Equal("Clienti/Neri", loaded.Connections[2].Group);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class MstscLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerMstsc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateTempRdpFile_WritesUsableFileWithoutClearPassword()
    {
        var c = new RdpConnection { Name = "Srv: prod/1", Host = "srv", Port = 3390, Username = "u", Domain = "D", Password = "ChiaroNonDeveComparire" };
        var path = MstscLauncher.CreateTempRdpFile(c, _dir);

        Assert.True(File.Exists(path));
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.EndsWith(".rdp", path);
        Assert.DoesNotContain(':', Path.GetFileName(path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        var text = Encoding.Unicode.GetString(bytes);
        Assert.DoesNotContain("ChiaroNonDeveComparire", text);
        Assert.DoesNotContain("ChiaroNonDeveComparire", Encoding.UTF8.GetString(bytes));

        var parsed = RdpFile.Load(path);
        Assert.Equal("srv", parsed.Host);
        Assert.Equal(3390, parsed.Port);
        Assert.Equal("D", parsed.Domain);
        Assert.Equal("ChiaroNonDeveComparire", parsed.Password);
    }

    [Fact]
    public void CreateTempRdpFile_TwoCallsGiveDistinctFiles()
    {
        var c = new RdpConnection { Host = "h" };
        Assert.NotEqual(MstscLauncher.CreateTempRdpFile(c, _dir), MstscLauncher.CreateTempRdpFile(c, _dir));
    }

    [Fact]
    public void MstscExe_IsPresentOnThisSystem()
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, "mstsc.exe")));
    }
}
