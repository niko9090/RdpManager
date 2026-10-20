using RdpManager.Core;

namespace RdpManager.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sub", "connections.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmptyDocument()
    {
        var doc = new ConnectionStore(FilePath).Load();
        Assert.Empty(doc.Connections);
        Assert.Empty(doc.Groups);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        var original = new RdpConnection
        {
            Name = "Server ufficio", Host = "srv01.example.local", Port = 3390,
            Username = "mario", Domain = "ACME", Password = "S3greta!àè",
            ResolutionMode = ResolutionMode.Fixed, DesktopWidth = 1600, DesktopHeight = 900,
            RedirectClipboard = false, RedirectDrives = true, Group = "Clienti/Rossi",
        };
        var store = new ConnectionStore(FilePath);
        store.Save(new ConnectionsDocument { Groups = ["Vuota"], Connections = [original, new RdpConnection { Host = "pc2" }] });

        var loaded = new ConnectionStore(FilePath).Load();

        Assert.Equal(2, loaded.Connections.Count);
        var c = loaded.Connections[0];
        Assert.Equal(original.Id, c.Id);
        Assert.Equal("Server ufficio", c.Name);
        Assert.Equal("srv01.example.local", c.Host);
        Assert.Equal(3390, c.Port);
        Assert.Equal("mario", c.Username);
        Assert.Equal("ACME", c.Domain);
        Assert.Equal("S3greta!àè", c.Password);
        Assert.Null(c.ProtectedPassword);
        Assert.Equal(ResolutionMode.Fixed, c.ResolutionMode);
        Assert.Equal(1600, c.DesktopWidth);
        Assert.Equal(900, c.DesktopHeight);
        Assert.False(c.RedirectClipboard);
        Assert.True(c.RedirectDrives);
        Assert.Equal("Clienti/Rossi", c.Group);

        var second = loaded.Connections[1];
        Assert.Equal(RdpConnection.DefaultPort, second.Port);
        Assert.Null(second.Password);
        Assert.Equal(["Vuota"], loaded.Groups);
        Assert.Equal(["Clienti", "Clienti/Rossi", "Vuota"], loaded.AllGroups());
    }

    [Fact]
    public void Save_DoesNotWritePasswordInClearText()
    {
        const string secret = "PasswordMoltoSegreta-12345";
        var store = new ConnectionStore(FilePath);
        store.Save(new ConnectionsDocument { Connections = [new RdpConnection { Host = "pc", Password = secret }] });

        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain(secret, json);
        Assert.Contains("protectedPassword", json);
    }

    [Fact]
    public void Save_DoesNotModifyInMemoryConnection()
    {
        var c = new RdpConnection { Host = "pc", Password = "abc" };
        new ConnectionStore(FilePath).Save(new ConnectionsDocument { Connections = [c] });
        Assert.Equal("abc", c.Password);
        Assert.Null(c.ProtectedPassword);
    }

    [Fact]
    public void Load_KeepsUndecryptablePasswordForNextSave()
    {
        var store = new ConnectionStore(FilePath);
        var foreignBlob = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });
        store.Save(new ConnectionsDocument { Connections = [new RdpConnection { Host = "pc", ProtectedPassword = foreignBlob }] });

        var loaded = store.Load();
        Assert.Null(loaded.Connections[0].Password);
        Assert.Equal(foreignBlob, loaded.Connections[0].ProtectedPassword);

        store.Save(loaded);
        Assert.Equal(foreignBlob, store.Load().Connections[0].ProtectedPassword);
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var store = new ConnectionStore(FilePath);
        store.Save(new ConnectionsDocument { Connections = [new RdpConnection { Host = "a" }, new RdpConnection { Host = "b" }] });
        store.Save(new ConnectionsDocument { Connections = [new RdpConnection { Host = "c" }] });
        Assert.Equal("c", Assert.Single(store.Load().Connections).Host);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
