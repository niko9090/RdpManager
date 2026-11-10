using RdpManager.Core;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

[Collection("Globali")]
public class MainWindowViewModelTests
{
    private static IEnumerable<TreeNodeViewModel> All(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));

    private static List<string> Titles(MainWindowViewModel vm) => All(vm.Roots).Select(n => n.Title).ToList();

    private static TreeNodeViewModel Node(MainWindowViewModel vm, string title) => All(vm.Roots).First(n => n.Title == title);

    [Fact]
    public void Elenco_vuoto_senza_file()
    {
        using var data = new TempData();
        var vm = Seed.Main(data);
        Assert.Empty(vm.Roots);
        Assert.False(vm.NoResults);
    }

    [Fact]
    public void Albero_con_cartelle_sottocartelle_e_connessioni_alla_radice()
    {
        using var data = new TempData();
        Seed.Write(data.Store,
            Seed.Conn("Zeta", "z.local"),
            Seed.Conn("Alfa", "a.local", "Uffici"),
            Seed.Conn("Beta", "b.local", "Uffici/Piano 1"));

        var vm = Seed.Main(data);

        Assert.Equal(["Uffici", "Zeta"], vm.Roots.Select(n => n.Title));
        var uffici = Node(vm, "Uffici");
        Assert.True(uffici.IsFolder);
        Assert.Equal(["Piano 1", "Alfa"], uffici.Children.Select(n => n.Title));
        Assert.Equal("a.local", Node(vm, "Alfa").Subtitle);
    }

    [Fact]
    public void Filtro_tiene_solo_le_connessioni_che_corrispondono_e_le_loro_cartelle()
    {
        using var data = new TempData();
        Seed.Write(data.Store,
            Seed.Conn("Server contabilità", "srv1", "Uffici"),
            Seed.Conn("Magazzino", "mag", "Sedi/Nord"),
            Seed.Conn("Router", "10.0.0.1"));
        var vm = Seed.Main(data);

        vm.SearchText = "contab";

        Assert.Equal(["Uffici", "Server contabilità"], Titles(vm));
        Assert.True(Node(vm, "Uffici").IsExpanded);
        Assert.Equal(1, vm.VisibleConnectionCount);
        Assert.False(vm.NoResults);

        vm.SearchText = "nonesiste";
        Assert.Empty(vm.Roots);
        Assert.True(vm.NoResults);

        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(6, Titles(vm).Count); // cartelle Sedi, Nord, Uffici e le tre connessioni
    }

    [Fact]
    public void Filtro_cerca_anche_in_host_e_utente_con_piu_parole()
    {
        using var data = new TempData();
        var c = Seed.Conn("Casa", "nas.casa");
        c.Username = "mario";
        Seed.Write(data.Store, c, Seed.Conn("Ufficio", "nas.ufficio"));
        var vm = Seed.Main(data);

        vm.SearchText = "nas mario";

        Assert.Equal(["Casa"], Titles(vm));
    }

    [Fact]
    public void Dopo_il_filtro_le_cartelle_tornano_come_le_aveva_lasciate_l_utente()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("A", "a", "Uno"), Seed.Conn("B", "b", "Due"));
        var vm = Seed.Main(data);
        Node(vm, "Uno").IsExpanded = true;

        vm.SearchText = "a";
        vm.SearchText = "";

        Assert.True(Node(vm, "Uno").IsExpanded);
        Assert.False(Node(vm, "Due").IsExpanded);
    }

    [Fact]
    public async Task Nuova_connessione_viene_salvata_nella_cartella_selezionata()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Esistente", "e", "Uffici"));
        var dialogs = new FakeDialogs
        {
            OnEdit = e => { e.Name = "Nuova"; e.Host = "nuova.local"; e.Port = "3390"; e.Password = "segreto"; },
        };
        var vm = Seed.Main(data, dialogs);
        vm.SelectedNode = Node(vm, "Uffici");

        await vm.NewConnectionAsync();

        var saved = data.Store.Load().Connections.Single(c => c.Name == "Nuova");
        Assert.Equal("Uffici", saved.Group);
        Assert.Equal(3390, saved.Port);
        Assert.Equal("segreto", saved.Password);
        Assert.Contains("\"protectedPassword\"", File.ReadAllText(data.FilePath));
        Assert.DoesNotContain("segreto", File.ReadAllText(data.FilePath));
        Assert.Equal(saved.Id, vm.SelectedNode?.Connection?.Id);
    }

    [Fact]
    public async Task Modifica_connessione_cambia_i_campi_e_tiene_la_password_se_il_campo_e_vuoto()
    {
        using var data = new TempData();
        var original = Seed.Conn("Vecchio", "vecchio.local");
        original.Password = "tienila";
        Seed.Write(data.Store, original);
        var dialogs = new FakeDialogs { OnEdit = e => { e.Name = "Nuovo nome"; e.Group = "Casa\\Sala"; } };
        var vm = Seed.Main(data, dialogs);
        vm.SelectedNode = Node(vm, "Vecchio");

        await vm.EditSelectedAsync();

        var saved = data.Store.Load().Connections.Single();
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Nuovo nome", saved.Name);
        Assert.Equal("Casa/Sala", saved.Group);
        Assert.Equal("tienila", saved.Password);
    }

    [Fact]
    public async Task Modifica_annullata_non_cambia_nulla()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Resta", "resta"));
        var dialogs = new FakeDialogs { OnEdit = e => { e.Name = "Cambiato"; e.Port = "non un numero"; } };
        var vm = Seed.Main(data, dialogs);
        vm.SelectedNode = Node(vm, "Resta");

        await vm.EditSelectedAsync();

        Assert.Equal("Resta", data.Store.Load().Connections.Single().Name);
        Assert.Equal("Resta", vm.Document.Connections.Single().Name);
    }

    [Fact]
    public async Task Elimina_connessione_chiede_conferma()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Da tenere", "a"), Seed.Conn("Da togliere", "b"));
        var dialogs = new FakeDialogs { Confirm = false };
        var vm = Seed.Main(data, dialogs);
        vm.SelectedNode = Node(vm, "Da togliere");

        await vm.DeleteSelectedAsync();
        Assert.Equal(2, data.Store.Load().Connections.Count);

        dialogs.Confirm = true;
        await vm.DeleteSelectedAsync();
        Assert.Equal(["Da tenere"], data.Store.Load().Connections.Select(c => c.Name));
        Assert.Equal(["Da tenere"], Titles(vm));
    }

    [Fact]
    public async Task Elimina_cartella_sposta_le_connessioni_al_livello_superiore()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Dentro", "d", "A/B"));
        var vm = Seed.Main(data, new FakeDialogs());
        vm.SelectedNode = Node(vm, "B");

        await vm.DeleteSelectedAsync();

        var doc = data.Store.Load();
        Assert.Equal("A", doc.Connections.Single().Group);
        Assert.DoesNotContain("A/B", doc.AllGroups());
    }

    [Fact]
    public async Task Nuova_cartella_e_rinomina()
    {
        using var data = new TempData();
        var dialogs = new FakeDialogs { PromptAnswer = "Uffici" };
        var vm = Seed.Main(data, dialogs);

        await vm.NewFolderAsync();
        Assert.Equal(["Uffici"], data.Store.Load().Groups);

        vm.SelectedNode = Node(vm, "Uffici");
        dialogs.PromptAnswer = "Sedi";
        await vm.EditSelectedAsync();
        Assert.Equal(["Sedi"], data.Store.Load().Groups);
    }

    [Fact]
    public void Duplica_e_preferita()
    {
        using var data = new TempData();
        Seed.Write(data.Store, Seed.Conn("Originale", "o"));
        var vm = Seed.Main(data);
        vm.SelectedNode = Node(vm, "Originale");

        vm.DuplicateSelected();
        vm.SelectedNode = Node(vm, "Originale");
        vm.ToggleFavorite();

        var saved = data.Store.Load();
        Assert.Equal(2, saved.Connections.Count);
        Assert.Contains(saved.Connections, c => c.Name == "Originale (copia)");
        Assert.Single(saved.Favorites());
        Assert.StartsWith("★ ", Node(vm, "★ Originale").Title);
    }

    [Fact]
    public void File_illeggibile_non_viene_sovrascritto()
    {
        using var data = new TempData();
        File.WriteAllText(data.FilePath, "{ non è json");
        var vm = Seed.Main(data);

        Assert.Empty(vm.Roots);
        Assert.Contains("non è leggibile", vm.StatusMessage);
        Assert.False(vm.Save());
        Assert.Equal("{ non è json", File.ReadAllText(data.FilePath));
    }

    [Fact]
    public void Connetti_apre_una_scheda_col_segnaposto_e_annota_l_ultima_connessione()
    {
        using var data = new TempData();
        var c = Seed.Conn("PC", "pc.local");
        c.Username = "mario";
        Seed.Write(data.Store, c);
        var vm = Seed.Main(data);
        vm.SelectedNode = Node(vm, "PC");

        vm.ConnectSelected();

        var tab = Assert.Single(vm.Tabs);
        Assert.Equal("PC", tab.Title);
        Assert.Same(tab, vm.SelectedTab);
        Assert.True(vm.HasTabs);
        Assert.IsType<Sessions.PlaceholderSessionContent>(tab.Content);
        Assert.Contains("non sono ancora disponibili", vm.StatusMessage);
        Assert.NotNull(data.Store.Load().Connections.Single().LastConnectedUtc);

        tab.CloseCommand.Execute(null);
        Assert.Empty(vm.Tabs);
        Assert.False(vm.HasTabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public void La_sessione_riceve_le_credenziali_del_profilo()
    {
        using var data = new TempData();
        var doc = new ConnectionsDocument();
        var profile = new CredentialProfile { Name = "Admin", Username = "root", Domain = "DOM", Password = "pw" };
        doc.CredentialProfiles.Add(profile);
        var c = Seed.Conn("PC", "pc");
        c.CredentialProfileId = profile.Id;
        doc.Connections.Add(c);
        data.Store.Save(doc);
        var host = new RecordingHost();
        var vm = Seed.Main(data, host: host);
        vm.SelectedNode = Node(vm, "PC");

        vm.ConnectSelected();

        Assert.Equal(("root", "DOM", "pw"), (host.Opened.Username, host.Opened.Domain, host.Opened.Password));
    }

    private sealed class RecordingHost : Sessions.ISessionHost
    {
        public RdpConnection Opened { get; private set; } = new();
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Sessions.ISession Open(RdpConnection connection)
        {
            Opened = connection;
            return new Sessions.PlaceholderSession(connection);
        }
    }
}
