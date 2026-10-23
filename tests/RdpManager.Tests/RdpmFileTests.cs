using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

public class RdpmFileTests
{
    // Poche iterazioni nei test per tenerli veloci; il formato registra il numero usato.
    private const int FastIterations = 1000;
    private const string Secret1 = "Segreta-Uno-àè€";
    private const string Secret2 = "AltraPassword#42";
    private const string FilePassword = "password del file";

    private static List<RdpConnection> Sample() =>
    [
        new RdpConnection
        {
            Name = "Server A", Host = "a.example.test", Port = 3390, Username = "mario", Domain = "ACME", Password = Secret1,
            Group = "Clienti/Uno", IsFavorite = true, Color = "#FF8800", Label = "PROD", MacAddress = "00:11:22:33:44:55",
            AudioMode = AudioMode.Remote, RedirectPrinters = true, RedirectMicrophone = true, UseAllMonitors = true,
            AutoReconnect = true, AutoReconnectMaxAttempts = 5, ResolutionMode = ResolutionMode.Fixed, DesktopWidth = 1280, DesktopHeight = 720,
        },
        new RdpConnection { Name = "Server B", Host = "b.example.test", Password = Secret2 },
        new RdpConnection { Name = "Senza password", Host = "c.example.test" },
    ];

    [Fact]
    public void RoundTrip_WithFilePassword_RestoresEverythingIncludingPasswords()
    {
        var original = Sample();
        var json = RdpmFile.Export(original, ["Clienti/Uno", "Vuota"], FilePassword, FastIterations);
        Assert.True(RdpmFile.IsEncrypted(json));

        var doc = RdpmFile.Import(json, FilePassword);

        Assert.Equal(["Clienti/Uno", "Vuota"], doc.Groups);
        Assert.Equal(3, doc.Connections.Count);
        var a = doc.Connections[0];
        var o = original[0];
        Assert.Equal(o.Id, a.Id);
        Assert.Equal(o.Name, a.Name);
        Assert.Equal(o.Host, a.Host);
        Assert.Equal(o.Port, a.Port);
        Assert.Equal(o.Username, a.Username);
        Assert.Equal(o.Domain, a.Domain);
        Assert.Equal(Secret1, a.Password);
        Assert.Null(a.ProtectedPassword);
        Assert.Equal(o.Group, a.Group);
        Assert.True(a.IsFavorite);
        Assert.Equal("#FF8800", a.Color);
        Assert.Equal("PROD", a.Label);
        Assert.Equal("00:11:22:33:44:55", a.MacAddress);
        Assert.Equal(AudioMode.Remote, a.AudioMode);
        Assert.True(a.RedirectPrinters);
        Assert.True(a.RedirectMicrophone);
        Assert.True(a.UseAllMonitors);
        Assert.True(a.AutoReconnect);
        Assert.Equal(5, a.AutoReconnectMaxAttempts);
        Assert.Equal(ResolutionMode.Fixed, a.ResolutionMode);
        Assert.Equal(1280, a.DesktopWidth);
        Assert.Equal(Secret2, doc.Connections[1].Password);
        Assert.Null(doc.Connections[2].Password);
    }

    [Fact]
    public void Export_WithoutFilePassword_ExcludesPasswords()
    {
        var json = RdpmFile.Export(Sample());
        Assert.False(RdpmFile.IsEncrypted(json));
        Assert.DoesNotContain(Secret1, json);
        Assert.DoesNotContain(Secret2, json);
        Assert.DoesNotContain("Password\":", json);
        Assert.DoesNotContain("\"encryption\"", json);

        var doc = RdpmFile.Import(json);
        Assert.Equal(3, doc.Connections.Count);
        Assert.All(doc.Connections, c => Assert.False(c.HasPassword));
        Assert.Equal("a.example.test", doc.Connections[0].Host);
    }

    [Fact]
    public void Export_NeverContainsPasswordsInClearText()
    {
        var json = RdpmFile.Export(Sample(), filePassword: FilePassword, iterations: FastIterations);
        foreach (var secret in new[] { Secret1, Secret2 })
        {
            Assert.DoesNotContain(secret, json);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), json);
            Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(secret)), json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(FilePassword, json);
        Assert.Contains("AES-256-GCM", json);
        Assert.Contains("PBKDF2", json);
    }

    [Fact]
    public void Export_DoesNotIncludeDpapiBlobOrTouchConnection()
    {
        var c = new RdpConnection { Host = "x", Password = "p", ProtectedPassword = "QkxPQi1EUEFQSQ==" };
        var json = RdpmFile.Export([c]);
        Assert.DoesNotContain("QkxPQi1EUEFQSQ==", json);
        Assert.Equal("p", c.Password);
        Assert.Equal("QkxPQi1EUEFQSQ==", c.ProtectedPassword);
    }

    [Fact]
    public void Import_WrongFilePassword_ThrowsClearError()
    {
        var json = RdpmFile.Export(Sample(), filePassword: FilePassword, iterations: FastIterations);
        var ex = Assert.Throws<RdpmPasswordException>(() => RdpmFile.Import(json, "sbagliata"));
        Assert.False(ex.PasswordMissing);
        Assert.Contains("sbagliata", ex.Message);
    }

    [Fact]
    public void Import_EncryptedFileWithoutPassword_AsksForIt()
    {
        var json = RdpmFile.Export(Sample(), filePassword: FilePassword, iterations: FastIterations);
        var ex = Assert.Throws<RdpmPasswordException>(() => RdpmFile.Import(json, null));
        Assert.True(ex.PasswordMissing);
    }

    [Fact]
    public void Import_TamperedPassword_IsDetected()
    {
        var json = RdpmFile.Export([new RdpConnection { Host = "x", Password = "abc" }], filePassword: FilePassword, iterations: FastIterations);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var sealedText = (string)doc["connections"]![0]!["encryptedPassword"]!;
        var bytes = Convert.FromBase64String(sealedText);
        bytes[^1] ^= 0x01;
        doc["connections"]![0]!["encryptedPassword"] = Convert.ToBase64String(bytes);
        Assert.Throws<InvalidDataException>(() => RdpmFile.Import(doc.ToJsonString(), FilePassword));
    }

    [Theory]
    [InlineData("")]
    [InlineData("non json")]
    [InlineData("{\"format\":\"altro\",\"version\":1}")]
    [InlineData("{\"format\":\"rdpm\",\"version\":99}")]
    public void Import_InvalidFile_ThrowsInvalidData(string json) =>
        Assert.Throws<InvalidDataException>(() => RdpmFile.Import(json));

    [Fact]
    public void SaveAndLoad_File()
    {
        var path = Path.Combine(Path.GetTempPath(), "rdpm-" + Guid.NewGuid().ToString("N") + RdpmFile.Extension);
        try
        {
            RdpmFile.Save(path, Sample(), ["Clienti"], FilePassword);
            var doc = RdpmFile.Load(path, FilePassword);
            Assert.Equal(Secret1, doc.Connections[0].Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_SkipsDuplicatesByIdAndByTarget()
    {
        var existing = new RdpConnection { Name = "Mio A", Host = "A.example.test", Port = 3390, Username = "MARIO", Domain = "acme" };
        var sameId = new RdpConnection { Name = "Mio D", Host = "d.example.test" };
        var target = new ConnectionsDocument { Groups = ["Mia"], Connections = [existing, sameId] };

        var exported = Sample();
        exported.Add(sameId.Clone());                                           // stesso Id
        exported.Add(new RdpConnection { Name = "Nuovo", Host = "e.example.test" });
        exported.Add(new RdpConnection { Name = "Nuovo bis", Host = "E.example.test" }); // doppione dentro il file
        var imported = RdpmFile.Import(RdpmFile.Export(exported, ["Clienti/Uno"], FilePassword, FastIterations), FilePassword);

        var result = RdpmFile.ApplyImport(target, imported, ImportMode.Merge);

        Assert.Equal(new ImportResult(Added: 3, Skipped: 3, Removed: 0, PasswordsImported: 2), result);
        Assert.Equal(5, target.Connections.Count);
        Assert.Equal(["Mio A", "Mio D", "Server B", "Senza password", "Nuovo"], target.Connections.Select(c => c.Name));
        // Il doppione resta quello locale, ma riceve la password che non aveva.
        Assert.Equal(Secret1, target.Connections[0].Password);
        Assert.Contains("Clienti/Uno", target.Groups);
        Assert.Contains("Mia", target.Groups);

        // Una seconda importazione dello stesso file non aggiunge nulla.
        var again = RdpmFile.ApplyImport(target, RdpmFile.Import(RdpmFile.Export(exported)), ImportMode.Merge);
        Assert.Equal(0, again.Added);
        Assert.Equal(5, target.Connections.Count);
    }

    [Fact]
    public void Replace_SwapsConnectionsAndGroupsKeepsSettings()
    {
        var old = new RdpConnection { Host = "vecchio" };
        var target = new ConnectionsDocument { Groups = ["Vecchia"], Connections = [old] };
        target.Settings.PreferredMonitor = "DISPLAY9";
        target.Settings.OpenSessions = [old.Id];

        var imported = RdpmFile.Import(RdpmFile.Export(Sample(), ["Nuova"]));
        var result = RdpmFile.ApplyImport(target, imported, ImportMode.Replace);

        Assert.Equal(new ImportResult(3, 0, 1), result);
        Assert.Equal(["a.example.test", "b.example.test", "c.example.test"], target.Connections.Select(c => c.Host));
        Assert.Equal(["Nuova"], target.Groups);
        Assert.Equal("DISPLAY9", target.Settings.PreferredMonitor);
        Assert.Empty(target.Settings.OpenSessions);
    }
}
