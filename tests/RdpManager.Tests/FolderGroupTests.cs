using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Gruppi (cartelle) di connessioni: struttura, eliminazione senza perdite, impostazioni ereditate, salvataggio e .rdpm.</summary>
public sealed class FolderGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RdpManagerGroups-" + Guid.NewGuid().ToString("N"));

    public FolderGroupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static RdpConnection Conn(string name, string group = "", string host = "") =>
        new() { Name = name, Host = host.Length > 0 ? host : name.ToLowerInvariant() + ".local", Group = group };

    private static (ConnectionsDocument Doc, CredentialProfile Profile) Sample()
    {
        var profile = new CredentialProfile { Name = "Admin cliente", Username = "admin", Domain = "ACME", Password = "Segreta-Gruppo-1!" };
        var doc = new ConnectionsDocument
        {
            Connections = [Conn("A", "Clienti"), Conn("B", "Clienti/Rossi"), Conn("C", "Clienti/Rossi"), Conn("D")],
            CredentialProfiles = [profile],
        };
        doc.AddFolder("", "Vuota");
        return (doc, profile);
    }

    [Fact]
    public void Creazione_e_annidamento()
    {
        var doc = new ConnectionsDocument();
        Assert.Equal("Clienti", doc.AddFolder("", "Clienti"));
        Assert.Equal("Clienti/Rossi", doc.AddFolder("Clienti", "Rossi"));
        Assert.Null(doc.AddFolder("", "  "));
        Assert.Equal(["Clienti", "Clienti/Rossi"], doc.AllGroups());
    }

    [Fact]
    public void Eliminare_un_gruppo_non_perde_connessioni_e_sposta_nel_padre()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti/Rossi", profile.Id, ResolutionMode.Fixed, 1280, 720);

        doc.DeleteFolder("Clienti/Rossi");

        Assert.Equal(4, doc.Connections.Count);
        Assert.Equal("Clienti", doc.Connections.Single(c => c.Name == "B").Group);
        Assert.Equal("Clienti", doc.Connections.Single(c => c.Name == "C").Group);
        Assert.DoesNotContain("Clienti/Rossi", doc.AllGroups());
        Assert.Empty(doc.FolderSettings);
    }

    [Fact]
    public void Eliminare_un_gruppo_di_primo_livello_manda_le_connessioni_in_senza_gruppo()
    {
        var (doc, _) = Sample();
        doc.DeleteFolder("Clienti");
        Assert.Equal(4, doc.Connections.Count);
        Assert.All(doc.Connections, c => Assert.Equal("", c.Group));
    }

    [Fact]
    public void Rinominare_e_spostare_un_gruppo_porta_con_se_connessioni_e_impostazioni()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti/Rossi", profile.Id, null);

        Assert.Equal("Clienti/Bianchi", doc.RenameFolder("Clienti/Rossi", "Bianchi"));
        Assert.NotNull(doc.FindFolderSettings("Clienti/Bianchi"));
        Assert.Null(doc.FindFolderSettings("Clienti/Rossi"));

        Assert.Equal("Vuota/Bianchi", doc.MoveFolder("Clienti/Bianchi", "Vuota"));
        Assert.Equal("Vuota/Bianchi", doc.Connections.Single(c => c.Name == "B").Group);
        Assert.NotNull(doc.FindFolderSettings("Vuota/Bianchi"));
        Assert.Equal("Bianchi", doc.MoveFolder("Vuota/Bianchi", ""));
    }

    [Fact]
    public void Un_gruppo_non_si_sposta_dentro_se_stesso()
    {
        var (doc, _) = Sample();
        Assert.Null(doc.MoveFolder("Clienti", "Clienti/Rossi"));
        Assert.Null(doc.MoveFolder("Clienti", "Clienti"));
        Assert.Equal("Clienti/Rossi", doc.Connections.Single(c => c.Name == "B").Group);
    }

    [Fact]
    public void Spostare_connessioni_fra_gruppi()
    {
        var (doc, _) = Sample();
        var d = doc.Connections.Single(c => c.Name == "D");
        Assert.True(doc.MoveConnection(d.Id, null, "Vuota"));
        Assert.Equal("Vuota", d.Group);
        Assert.Equal(1, GroupActions.MoveToFolder(doc, [d.Id], ""));
        Assert.Equal("", d.Group);
    }

    [Fact]
    public void Le_credenziali_si_ereditano_dal_gruppo_piu_vicino()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, null);
        var a = doc.Connections.Single(c => c.Name == "A");
        var b = doc.Connections.Single(c => c.Name == "B");
        var d = doc.Connections.Single(c => c.Name == "D");

        Assert.Equal(("admin", "ACME", "Segreta-Gruppo-1!"), (doc.ResolveCredentials(a).Username, doc.ResolveCredentials(a).Domain, doc.ResolveCredentials(a).Password));
        Assert.Equal(profile.Id, doc.ResolveCredentials(b).ProfileId); // sottocartella: eredita dal padre
        Assert.Equal("", doc.ResolveCredentials(d).Username);          // senza gruppo: nulla

        var other = new CredentialProfile { Name = "Altro", Username = "altro" };
        doc.CredentialProfiles.Add(other);
        doc.SetFolderSettings("Clienti/Rossi", other.Id, null);
        Assert.Equal("altro", doc.ResolveCredentials(b).Username);     // il più vicino vince
        Assert.Equal("admin", doc.ResolveCredentials(a).Username);
    }

    [Fact]
    public void Le_credenziali_proprie_della_connessione_non_si_sovrascrivono()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, null);
        var a = doc.Connections.Single(c => c.Name == "A");
        a.Username = "mio";
        Assert.Equal("mio", doc.ResolveCredentials(a).Username);
        a.Username = "";
        a.Password = "pwd";
        Assert.Equal("pwd", doc.ResolveCredentials(a).Password);
        a.Password = null;
        a.CredentialProfileId = profile.Id;
        Assert.Equal(profile.Id, doc.ResolveCredentials(a).ProfileId);
    }

    [Fact]
    public void La_connessione_passata_al_controllo_porta_credenziali_e_risoluzione_del_gruppo()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, ResolutionMode.Fixed, 1366, 768);
        var b = doc.Connections.Single(c => c.Name == "B");

        var plain = doc.WithResolvedCredentials(b);
        Assert.Equal("admin", plain.Username);
        Assert.Equal(ResolutionMode.FitToWindow, plain.ResolutionMode); // non eredita finché non lo chiede

        b.InheritFolderResolution = true;
        var inherited = doc.WithResolvedCredentials(b);
        Assert.Equal((ResolutionMode.Fixed, 1366, 768), (inherited.ResolutionMode, inherited.DesktopWidth, inherited.DesktopHeight));
        Assert.Equal(ResolutionMode.FitToWindow, b.ResolutionMode); // l'originale non cambia
    }

    [Fact]
    public void Eliminando_il_profilo_le_impostazioni_del_gruppo_lo_dimenticano()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, null);
        doc.DeleteProfile(profile.Id);
        Assert.Empty(doc.FolderSettings);
    }

    [Fact]
    public void Collega_tutto_il_gruppo_comprende_le_sottocartelle()
    {
        var (doc, _) = Sample();
        Assert.Equal(["A", "B", "C"], doc.ConnectionsInFolder("Clienti").Select(c => c.Name));
        Assert.Equal(["B", "C"], doc.ConnectionsInFolder("Clienti/Rossi").Select(c => c.Name));
    }

    [Fact]
    public void Salvataggio_e_caricamento_conservano_gruppi_e_impostazioni()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, ResolutionMode.Fixed, 1600, 900);
        doc.Connections[0].InheritFolderResolution = true;
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"));
        store.Save(doc);

        var loaded = store.Load();
        Assert.Equal(["Clienti", "Clienti/Rossi", "Vuota"], loaded.AllGroups());
        var fs = Assert.Single(loaded.FolderSettings);
        Assert.Equal(("Clienti", profile.Id, ResolutionMode.Fixed, 1600, 900), (fs.Path, fs.CredentialProfileId, fs.Resolution, fs.DesktopWidth, fs.DesktopHeight));
        Assert.True(loaded.Connections[0].InheritFolderResolution);
        Assert.Equal("admin", loaded.ResolveCredentials(loaded.Connections[1]).Username);
    }

    [Fact]
    public void Un_file_vecchio_senza_impostazioni_di_gruppo_si_carica()
    {
        var path = Path.Combine(_dir, "vecchio.json");
        File.WriteAllText(path, """
            { "version": 5, "groups": ["Uffici"],
              "connections": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "PC1", "host": "pc1", "group": "Uffici" } ] }
            """);
        var doc = new ConnectionStore(path).Load();
        Assert.Empty(doc.FolderSettings);
        Assert.Equal(["Uffici"], doc.AllGroups());
        Assert.False(doc.Connections[0].InheritFolderResolution);
        Assert.Equal("", doc.ResolveCredentials(doc.Connections[0]).Username);
    }

    [Fact]
    public void Impostazioni_di_gruppo_danneggiate_nel_file_vengono_riparate()
    {
        var path = Path.Combine(_dir, "rotto.json");
        File.WriteAllText(path, """
            { "version": 7, "folderSettings": [ null, { "path": "", "resolution": "Fixed" }, { "path": "X\\Y", "resolution": "Fixed", "desktopWidth": 5 } ] }
            """);
        var doc = new ConnectionStore(path).Load();
        var fs = Assert.Single(doc.FolderSettings);
        Assert.Equal("X/Y", fs.Path);
        Assert.Equal(640, fs.DesktopWidth);
    }

    [Fact]
    public void Andata_e_ritorno_rdpm_porta_gruppi_e_impostazioni_con_il_profilo()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, ResolutionMode.Fixed, 1280, 720);

        var json = RdpmFile.Export(doc, filePassword: "pw-file", iterations: 1000);
        var imported = RdpmFile.Import(json, "pw-file");
        Assert.Equal(["Clienti", "Clienti/Rossi", "Vuota"], imported.AllGroups());
        Assert.Equal(ResolutionMode.Fixed, imported.FindFolderSettings("Clienti")!.Resolution);

        var target = new ConnectionsDocument();
        RdpmFile.ApplyImport(target, imported, ImportMode.Merge);
        Assert.Equal(["Clienti", "Clienti/Rossi", "Vuota"], target.AllGroups());
        var fs = target.FindFolderSettings("Clienti")!;
        Assert.Equal((1280, 720), (fs.DesktopWidth, fs.DesktopHeight));
        Assert.Equal("Segreta-Gruppo-1!", target.ResolveCredentials(target.Connections.Single(c => c.Name == "A")).Password);
    }

    [Fact]
    public void Importazione_in_unione_non_cambia_le_impostazioni_gia_presenti_e_in_sostituzione_le_rimpiazza()
    {
        var (doc, profile) = Sample();
        doc.SetFolderSettings("Clienti", profile.Id, ResolutionMode.Fixed, 1280, 720);
        var imported = RdpmFile.Import(RdpmFile.Export(doc, iterations: 1000));

        var target = new ConnectionsDocument();
        target.SetFolderSettings("Clienti", null, ResolutionMode.FitToWindow);
        RdpmFile.ApplyImport(target, imported, ImportMode.Merge);
        Assert.Equal(ResolutionMode.FitToWindow, target.FindFolderSettings("Clienti")!.Resolution);

        RdpmFile.ApplyImport(target, imported, ImportMode.Replace);
        Assert.Equal(ResolutionMode.Fixed, target.FindFolderSettings("Clienti")!.Resolution);
    }

    [Fact]
    public void Un_rdpm_senza_impostazioni_di_gruppo_si_importa()
    {
        var json = RdpmFile.Export([Conn("Z", "G")], ["G"]);
        var imported = RdpmFile.Import(json);
        Assert.Empty(imported.FolderSettings);
        Assert.Equal(["G"], imported.AllGroups());
    }
}
