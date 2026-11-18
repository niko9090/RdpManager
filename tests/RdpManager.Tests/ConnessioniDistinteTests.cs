using RdpManager.Core;
using RdpManager.Platform.Linux;
using RdpManager.Rdp;

namespace RdpManager.Tests;

/// <summary>
/// Due connessioni salvate con indirizzi diversi devono portare a due computer diversi: file .rdp, argomenti di FreeRDP,
/// chiave della scheda e della riga Preferiti/Recenti. Una causa trovata: due connessioni con lo stesso Id (dati vecchi,
/// importazioni) venivano confuse, e la scheda aperta per la prima veniva riusata per la seconda.
/// </summary>
public sealed class ConnessioniDistinteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static RdpConnection A() => new() { Name = "Ufficio", Host = "pc-ufficio.example.local", Username = "mario" };
    private static RdpConnection B() => new() { Name = "Casa", Host = "10.0.0.77", Username = "mario" };

    [Fact]
    public void FileRdp_HannoIndirizziDiversi()
    {
        var a = RdpFile.Generate(A());
        var b = RdpFile.Generate(B());
        Assert.Contains("full address:s:pc-ufficio.example.local", a);
        Assert.Contains("full address:s:10.0.0.77", b);
        Assert.DoesNotContain("10.0.0.77", a);
        Assert.DoesNotContain("pc-ufficio", b);
    }

    [Fact]
    public void FileRdpTemporanei_SonoDueFileConIndirizziDiversi()
    {
        var pa = MstscLauncher.CreateTempRdpFile(A(), _dir);
        var pb = MstscLauncher.CreateTempRdpFile(B(), _dir);
        Assert.NotEqual(pa, pb);
        Assert.Contains("pc-ufficio.example.local", File.ReadAllText(pa));
        Assert.Contains("10.0.0.77", File.ReadAllText(pb));
    }

    [Fact]
    public void ArgomentiFreeRdp_HannoDestinazioniDiverse()
    {
        var x3 = new FreeRdpClient("xfreerdp3", true, false);
        var a = FreeRdpCommandBuilder.Build(A(), x3);
        var b = FreeRdpCommandBuilder.Build(B(), x3);
        Assert.Contains("/v:pc-ufficio.example.local", a.Arguments);
        Assert.Contains("/v:10.0.0.77", b.Arguments);
        Assert.DoesNotContain("/v:10.0.0.77", a.Arguments);
    }

    [Fact]
    public void ChiaviDiScheda_DiverseQuandoGliIndirizziSonoDiversi()
    {
        var a = A();
        var b = B();
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(SyncFingerprints.Key(a), SyncFingerprints.Key(b));
    }

    [Fact]
    public void Duplica_DaUnNuovoIdEModificaDelloIndirizzoRestaDistinta()
    {
        var a = A();
        var copy = a.Duplicate();
        copy.Host = "altro.example.local";
        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal("pc-ufficio.example.local", a.Host);
    }

    [Fact]
    public void Caricamento_IdDuplicatiNelFile_SiRigenerano_SenzaPerdereDati()
    {
        var shared = Guid.NewGuid();
        var a = A();
        var b = B();
        a.Id = b.Id = shared;
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = [a, b] });

        var loaded = store.Load();

        Assert.Equal(2, loaded.Connections.Count);
        Assert.Equal(2, loaded.Connections.Select(c => c.Id).Distinct().Count());
        Assert.Equal(shared, loaded.Connections[0].Id); // la prima tiene l'Id (cronologia, sessioni da riaprire)
        Assert.Equal("pc-ufficio.example.local", loaded.Connections[0].Host);
        Assert.Equal("10.0.0.77", loaded.Connections[1].Host);
        // Find per Id (usato da Recenti, sessioni da riaprire, spazi di lavoro) arriva al computer giusto.
        Assert.Equal("10.0.0.77", loaded.Find(loaded.Connections[1].Id)!.Host);
    }

    [Fact]
    public void Caricamento_IdRigeneratoRestaStabileFraDueCaricamenti()
    {
        var shared = Guid.NewGuid();
        var a = A();
        var b = B();
        a.Id = b.Id = shared;
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = [a, b] });

        var first = store.Load().Connections[1].Id;
        var second = store.Load().Connections[1].Id;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Caricamento_IdVuoto_SiRigenera()
    {
        var a = A();
        var b = B();
        a.Id = b.Id = Guid.Empty;
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(new ConnectionsDocument { Connections = [a, b] });

        var loaded = store.Load();

        Assert.DoesNotContain(loaded.Connections, c => c.Id == Guid.Empty);
        Assert.NotEqual(loaded.Connections[0].Id, loaded.Connections[1].Id);
    }

    [Fact]
    public void Importazione_StessoIdMaIndirizzoDiverso_NonSaltaLaConnessione()
    {
        var existing = A();
        var imported = B();
        imported.Id = existing.Id;
        var target = new ConnectionsDocument { Connections = [existing] };
        var file = new ConnectionsDocument { Connections = [imported] };

        var result = RdpmFile.ApplyImport(target, file, ImportMode.Merge);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, target.Connections.Count);
        Assert.Equal(2, target.Connections.Select(c => c.Id).Distinct().Count());
        Assert.Contains(target.Connections, c => c.Host == "10.0.0.77");
        Assert.Contains(target.Connections, c => c.Host == "pc-ufficio.example.local");
    }

    [Fact]
    public void Importazione_StessoIdEStessoIndirizzo_RestaUnDoppione()
    {
        var existing = A();
        var imported = A();
        imported.Id = existing.Id;
        var target = new ConnectionsDocument { Connections = [existing] };

        var result = RdpmFile.ApplyImport(target, new ConnectionsDocument { Connections = [imported] }, ImportMode.Merge);

        Assert.Equal(0, result.Added);
        Assert.Single(target.Connections);
    }
}
