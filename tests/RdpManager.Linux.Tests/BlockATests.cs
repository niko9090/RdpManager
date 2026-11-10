using System.Net;
using System.Net.Sockets;
using System.Text;
using RdpManager.Core;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Settings;
using RdpManager.Linux.ViewModels.Tools;
using static RdpManager.Linux.Tests.A;

namespace RdpManager.Linux.Tests;

/// <summary>Cartelle (#5, #7), selezione multipla (#6), ordine (#8), ricerca (#11, #12), preferiti e recenti (#10).</summary>
public class GroupsAndSearchTests
{
    [Fact]
    public async Task Impostazioni_della_cartella_si_salvano_e_la_risoluzione_si_eredita()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var c = Seed.Conn("PC", "pc1", "Uffici");
        c.InheritFolderResolution = true;
        var vm = Vm(data, dialogs, c);
        vm.SelectedNode = Node(vm, "Uffici");
        dialogs.OnShow = o =>
        {
            var f = (FolderSettingsViewModel)o;
            f.UseResolution = true;
            f.FixedResolution = true;
            f.DesktopWidth = "1600";
            f.DesktopHeight = "900";
            return f.TryCommit();
        };

        await vm.FolderSettingsAsync();

        var reloaded = data.Store.Load();
        var s = reloaded.FindFolderSettings("Uffici")!;
        Assert.Equal((ResolutionMode.Fixed, 1600, 900), (s.Resolution, s.DesktopWidth, s.DesktopHeight));
        var resolved = reloaded.WithResolvedCredentials(reloaded.Connections[0]);
        Assert.Equal((ResolutionMode.Fixed, 1600, 900), (resolved.ResolutionMode, resolved.DesktopWidth, resolved.DesktopHeight));
    }

    [Fact]
    public void Impostazioni_della_cartella_rifiutano_misure_non_valide()
    {
        var doc = new ConnectionsDocument();
        doc.AddFolder("", "Uffici");
        var vm = new FolderSettingsViewModel(doc, "Uffici") { UseResolution = true, FixedResolution = true, DesktopWidth = "10", DesktopHeight = "abc" };
        Assert.False(vm.TryCommit());
        Assert.NotNull(vm.Error);
        Assert.Null(doc.FindFolderSettings("Uffici"));
    }

    [Fact]
    public async Task Connetti_tutta_la_cartella_apre_una_scheda_per_connessione_e_chiede_conferma_oltre_sei()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs { Confirm = false };
        var list = Enumerable.Range(1, 8).Select(i => Seed.Conn("PC" + i, "h" + i, "Sala")).ToArray();
        var vm = Vm(data, dialogs, list);
        vm.SelectedNode = Node(vm, "Sala");

        await vm.ConnectFolderAsync();
        Assert.Empty(vm.Tabs);
        Assert.Contains("8 sessioni", dialogs.ConfirmMessages.Single());

        dialogs.Confirm = true;
        await vm.ConnectFolderAsync();
        Assert.Equal(8, vm.Tabs.Count);
    }

    [Fact]
    public async Task Selezione_multipla_connette_sposta_assegna_ed_elimina()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("A", "a"), Seed.Conn("B", "b"), Seed.Conn("C", "c"));
        var profile = new CredentialProfile { Name = "Profilo", Username = "u" };
        vm.Document.CredentialProfiles.Add(profile);
        vm.SetSelection([Node(vm, "A"), Node(vm, "B")]);
        Assert.True(vm.HasMultiSelection);
        Assert.False(vm.EditCommand.CanExecute(null));

        vm.ConnectSelected();
        await Task.Yield();
        Assert.Equal(2, vm.Tabs.Count);

        dialogs.Choice = "Clienti/Rossi";
        await vm.MoveSelectedToFolderAsync();
        Assert.All(vm.Document.Connections.Where(c => c.DisplayName is "A" or "B"), c => Assert.Equal("Clienti/Rossi", c.Group));
        Assert.Equal("", vm.Document.Connections.Single(c => c.DisplayName == "C").Group);

        vm.SetSelection([Node(vm, "A"), Node(vm, "B")]);
        dialogs.Choice = profile.DisplayName;
        await vm.AssignProfileToSelectedAsync();
        Assert.Equal(2, vm.Document.Connections.Count(c => c.CredentialProfileId == profile.Id));

        vm.SetSelection([Node(vm, "A"), Node(vm, "B")]);
        await vm.DeleteSelectedAsync();
        Assert.Contains("A, B", dialogs.ConfirmMessages.Last());
        Assert.Equal(["C"], data.Store.Load().Connections.Select(c => c.DisplayName));
    }

    [Fact]
    public async Task Eliminare_con_no_alla_conferma_non_toglie_nulla()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs { Confirm = false };
        var vm = Vm(data, dialogs, Seed.Conn("A", "a"), Seed.Conn("B", "b"));
        vm.SetSelection([Node(vm, "A"), Node(vm, "B")]);
        await vm.DeleteGroupAsync();
        Assert.Equal(2, vm.Document.Connections.Count);
    }

    [Fact]
    public void Trascinare_riordina_sposta_nelle_cartelle_e_passa_all_ordine_manuale()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("A", "a"), Seed.Conn("B", "b"), Seed.Conn("C", "c", "Cart"));

        Assert.True(vm.MoveNode(Node(vm, "B"), Node(vm, "A")));
        Assert.Equal(ConnectionOrder.Manual, vm.Document.Settings.ConnectionOrder);
        Assert.Equal(["B", "A", "C"], vm.Document.Connections.Select(c => c.DisplayName));

        Assert.True(vm.MoveNode(Node(vm, "A"), Node(vm, "Cart")));
        Assert.Equal("Cart", vm.Document.Connections.Single(c => c.DisplayName == "A").Group);

        Assert.True(vm.MoveNode(Node(vm, "C"), null));
        Assert.Equal("", vm.Document.Connections.Single(c => c.DisplayName == "C").Group);
        Assert.Equal("Cart", data.Store.Load().Connections.Single(c => c.DisplayName == "A").Group);
    }

    [Fact]
    public void Trascinare_una_cartella_dentro_un_altra_la_sposta()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("A", "a", "Uno"), Seed.Conn("B", "b", "Due"));
        Assert.True(vm.MoveNode(Node(vm, "Uno"), Node(vm, "Due")));
        Assert.Equal("Due/Uno", vm.Document.Connections.Single(c => c.DisplayName == "A").Group);
        Assert.False(vm.MoveNode(Node(vm, "Due"), Node(vm, "Due/Uno")));
    }

    [Fact]
    public void Ordina_per_nome_torna_all_ordine_alfabetico()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("B", "b"), Seed.Conn("A", "a"));
        vm.MoveNode(Node(vm, "A"), null);
        Assert.Equal(ConnectionOrder.Manual, vm.Document.Settings.ConnectionOrder);
        vm.SortByNameCommand.Execute(null);
        Assert.Equal(ConnectionOrder.Alphabetical, data.Store.Load().Settings.ConnectionOrder);
        Assert.Equal(["A", "B"], data.Store.Load().Connections.Select(c => c.DisplayName));
    }

    [Fact]
    public void La_ricerca_nell_albero_trova_anche_con_un_refuso()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("server-prod", "10.0.0.1"), Seed.Conn("stampante", "10.0.0.2"));
        vm.SearchText = "10.0.0.2";
        Assert.Equal(["stampante"], A.Flatten(vm.Roots).Select(n => n.Connection?.DisplayName));
        vm.SearchText = "sevrer";
        Assert.Equal(["server-prod"], A.Flatten(vm.Roots).Select(n => n.Connection?.DisplayName));
        vm.SearchText = "zzzzqqq";
        Assert.True(vm.NoResults);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(2, vm.VisibleConnectionCount);
    }

    [Fact]
    public async Task Ricerca_rapida_apre_la_connessione_o_passa_alla_sessione_gia_aperta()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("Contabilita", "cont"), Seed.Conn("Magazzino", "mag"));
        dialogs.OnShow = o =>
        {
            var q = (QuickSearchViewModel)o;
            q.Query = "magaz";
            Assert.Equal(dialogs.Shown.Count == 1 ? "Apri" : "Passa alla sessione", q.Results[0].Action);
            return q.Accept();
        };

        await vm.ShowQuickSearchAsync();
        Assert.Equal("Magazzino", vm.Tabs.Single().Title);

        var count = vm.Tabs.Count;
        await vm.ShowQuickSearchAsync();
        Assert.Equal(count, vm.Tabs.Count); // già aperta: si passa alla scheda
        Assert.Equal("Passa alla sessione", ((QuickSearchViewModel)dialogs.Shown[^1]).Results[0].Action);
    }

    [Fact]
    public void Ricerca_rapida_senza_testo_mette_prima_le_sessioni_aperte_e_le_preferite()
    {
        var a = Seed.Conn("A", "a");
        var b = Seed.Conn("B", "b");
        b.IsFavorite = true;
        var vm = new QuickSearchViewModel([a, b], [a.Id]);
        Assert.Equal(["A", "B"], vm.Results.Select(r => r.Connection.DisplayName));
        Assert.Equal("Passa alla sessione", vm.Results[0].Action);
        vm.MoveSelection(1);
        Assert.Equal("B", vm.Selected!.Connection.DisplayName);
    }

    [Fact]
    public void Preferiti_e_recenti_alimentano_i_menu()
    {
        using var data = new TempData();
        var fav = Seed.Conn("Fav", "f");
        fav.IsFavorite = true;
        var vm = Vm(data, new ScriptedDialogs(), fav, Seed.Conn("Altro", "o"));
        Assert.Equal(["Fav"], vm.FavoriteItems.Select(i => i.Title));
        Assert.True(vm.HasFavorites);
        Assert.False(vm.HasRecents);

        vm.Connect(vm.Document.Connections.Single(c => c.DisplayName == "Altro"));
        Assert.Equal(["Altro"], vm.RecentItems.Select(i => i.Title));

        vm.FavoriteItems[0].Command.Execute(null);
        Assert.Equal(2, vm.Tabs.Count);
        vm.FavoriteItems[0].Command.Execute(null);
        Assert.Equal(2, vm.Tabs.Count); // già aperta
    }
}

/// <summary>Importa ed esporta (#20, #21, #22, #24, #25).</summary>
public class ImportExportTests
{
    private static string Rdp(string host) => RdpFile.Generate(new RdpConnection { Host = host, Name = host });

    [Fact]
    public async Task Importa_file_rdp_nella_cartella_selezionata_e_riferisce_gli_errori()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("X", "x", "Uffici"));
        vm.SelectedNode = Node(vm, "Uffici");
        var good = TempFile(data, "buono.rdp");
        var bad = TempFile(data, "vuoto.rdp");
        File.WriteAllText(good, Rdp("nuovo-pc"), Encoding.Unicode);
        File.WriteAllText(bad, "screen mode id:i:2\r\n", Encoding.Unicode);
        dialogs.FilesToOpen = [good, bad];

        await vm.ImportRdpAsync();

        var imported = data.Store.Load().Connections.Single(c => c.Host == "nuovo-pc");
        Assert.Equal("Uffici", imported.Group);
        Assert.Contains("Importate 1 connessioni", dialogs.Messages.Single());
        Assert.Contains("vuoto.rdp", dialogs.Messages.Single());
        Assert.Equal("nuovo-pc", vm.SelectedNode!.Connection!.Host);
    }

    [Fact]
    public async Task Esporta_una_connessione_come_rdp_e_si_puo_rileggere()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs { SaveTo = TempFile(data, "pc.rdp") };
        var c = Seed.Conn("PC/uno", "pc-uno");
        c.Password = "segreta";
        var vm = Vm(data, dialogs, c);
        vm.SelectedNode = Node(vm, "PC/uno");

        await vm.ExportRdpFileAsync();

        var back = RdpFile.Load(dialogs.SaveTo!);
        Assert.Equal("pc-uno", back.Host);
        Assert.DoesNotContain("segreta", File.ReadAllText(dialogs.SaveTo!, Encoding.Unicode));
        Assert.Contains("senza password", vm.StatusMessage);
    }

    [Fact]
    public async Task Esporta_e_importa_rdpm_con_password_del_file()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs { SaveTo = TempFile(data, "backup.rdpm") };
        var c = Seed.Conn("PC", "pc1", "Uffici");
        c.Password = "pw-del-pc";
        var vm = Vm(data, dialogs, c);
        dialogs.OnShow = o =>
        {
            var e = (RdpmExportViewModel)o;
            Assert.True(e.WithPasswords); // ci sono password salvate: proposta di default
            e.Password = e.ConfirmPassword = "password-lunga";
            return e.TryAcceptAsync().GetAwaiter().GetResult();
        };
        await vm.ExportRdpmAsync();
        Assert.True(File.Exists(dialogs.SaveTo));
        Assert.Contains("1 password cifrate", vm.StatusMessage);

        using var data2 = new TempData();
        var dialogs2 = new ScriptedDialogs { FilesToOpen = [dialogs.SaveTo!] };
        dialogs2.Secrets.Enqueue("sbagliata");
        dialogs2.Secrets.Enqueue("password-lunga");
        var vm2 = Vm(data2, dialogs2);
        dialogs2.OnShow = o =>
        {
            var i = (RdpmImportViewModel)o;
            Assert.True(i.Merge);
            return i.TryAcceptAsync().GetAwaiter().GetResult();
        };
        await vm2.ImportRdpmAsync();

        var imported = data2.Store.Load().Connections.Single();
        Assert.Equal(("pc1", "Uffici", "pw-del-pc"), (imported.Host, imported.Group, imported.Password));
        Assert.Contains("1", dialogs2.Messages.Single());
    }

    [Fact]
    public async Task Esportare_senza_password_chiede_conferma_dell_avviso_e_con_password_corta_rifiuta()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Host = "h", Password = "p" });
        var asked = new List<string>();
        var vm = new RdpmExportViewModel(doc, t => { asked.Add(t); return Task.FromResult(false); });
        vm.WithPasswords = true;
        vm.Password = vm.ConfirmPassword = "corta";
        Assert.False(await vm.TryAcceptAsync());
        Assert.Contains("almeno 8", vm.Error);
        vm.Password = "lunghissima1";
        Assert.False(await vm.TryAcceptAsync());
        Assert.Contains("non coincidono", vm.Error);
        vm.ConfirmPassword = vm.Password;
        Assert.True(await vm.TryAcceptAsync());
        Assert.Equal("lunghissima1", vm.FilePassword);

        vm.WithoutPasswords = true;
        Assert.False(await vm.TryAcceptAsync());
        Assert.Single(asked);
        Assert.Null(vm.FilePassword);
    }

    [Fact]
    public async Task Esporta_senza_connessioni_lo_dice()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        await Vm(data, dialogs).ExportRdpmAsync();
        Assert.Contains("Non ci sono connessioni", dialogs.Messages.Single());
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task Importa_rdpm_in_modalita_sostituisci_chiede_conferma()
    {
        using var data = new TempData();
        var src = new ConnectionsDocument();
        src.Connections.Add(new RdpConnection { Host = "nuovo", Name = "nuovo" });
        var file = TempFile(data, "x.rdpm");
        File.WriteAllText(file, RdpmFile.Export(src));
        var dialogs = new ScriptedDialogs { FilesToOpen = [file], Confirm = false };
        var vm = Vm(data, dialogs, Seed.Conn("Vecchia", "vecchia"));
        dialogs.OnShow = o =>
        {
            var i = (RdpmImportViewModel)o;
            i.Replace = true;
            return i.TryAcceptAsync().GetAwaiter().GetResult();
        };

        await vm.ImportRdpmAsync();
        Assert.Equal(["Vecchia"], vm.Document.Connections.Select(c => c.DisplayName));
        Assert.Contains("Cancellare le 1 connessioni", dialogs.ConfirmMessages.Single());

        dialogs.Confirm = true;
        await vm.ImportRdpmAsync();
        Assert.Equal(["nuovo"], data.Store.Load().Connections.Select(c => c.DisplayName));
    }

    [Fact]
    public async Task Importa_rdpm_non_valido_mostra_l_errore()
    {
        using var data = new TempData();
        var file = TempFile(data, "rotto.rdpm");
        File.WriteAllText(file, "non è json");
        var dialogs = new ScriptedDialogs { FilesToOpen = [file] };
        await Vm(data, dialogs).ImportRdpmAsync();
        Assert.Contains("non si può importare", dialogs.Messages.Single());
    }

    [Fact]
    public async Task Importa_csv_riconosce_le_colonne_e_salta_doppioni_ed_errori()
    {
        using var data = new TempData();
        var file = TempFile(data, "pc.csv");
        File.WriteAllText(file, "Nome;Host;Porta;Utente\nUno;uno.local;3390;mario\nDue;;3389;x\nEsiste;esiste;3389;\n", new UTF8Encoding(true));
        var dialogs = new ScriptedDialogs { FilesToOpen = [file] };
        var vm = Vm(data, dialogs, Seed.Conn("Esiste", "esiste"));
        CsvImportViewModel? seen = null;
        dialogs.OnShow = o =>
        {
            seen = (CsvImportViewModel)o;
            seen.Folder = "Importati";
            return seen.CanImport;
        };

        await vm.ImportCsvAsync();

        Assert.Equal(1, seen!.ImportableCount);
        Assert.Equal("Importa 1", seen.ImportLabel);
        Assert.Equal(3, seen.Preview.Count);
        var added = data.Store.Load().Connections.Single(c => c.Host == "uno.local");
        Assert.Equal((3390, "mario", "Importati"), (added.Port, added.Username, added.Group));
        Assert.Contains("Importate 1", vm.StatusMessage);
    }

    [Fact]
    public async Task Importa_csv_vuoto_lo_segnala()
    {
        using var data = new TempData();
        var file = TempFile(data, "vuoto.csv");
        File.WriteAllText(file, "Nome;Host\n");
        var dialogs = new ScriptedDialogs { FilesToOpen = [file] };
        await Vm(data, dialogs).ImportCsvAsync();
        Assert.Contains("non ha righe", dialogs.Messages.Single());
    }

    [Fact]
    public void Il_csv_in_latin1_si_legge()
    {
        using var data = new TempData();
        var file = TempFile(data, "l1.csv");
        File.WriteAllBytes(file, Encoding.Latin1.GetBytes("Nome;Host\nCittà;pc\n"));
        Assert.Contains("Città", MainWindowViewModel.ReadText(file));
    }

    [Fact]
    public void Le_colonne_del_csv_si_possono_cambiare_a_mano()
    {
        var table = CsvImport.Parse("A;B\nuno;host1\n");
        var vm = new CsvImportViewModel("x.csv", table, new ConnectionsDocument(), "");
        Assert.False(vm.CanImport); // nessuna colonna riconosciuta come host
        vm.Columns[1].Field = vm.Columns[1].FieldChoices.First(c => c.Value == CsvField.Host);
        Assert.True(vm.CanImport);
    }
}

/// <summary>PIN e blocco (#28).</summary>
public class LockTests
{
    private static (MainWindowViewModel Vm, TempData Data, ScriptedDialogs Dialogs, DateTime[] Now) WithPin(string pin = "2468")
    {
        var data = new TempData();
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Host = "h", Name = "PC", Password = "segreta" });
        doc.Settings.LockPin = PinLock.Create(pin, 10_000);
        doc.Settings.LockAfterMinutes = 5;
        data.Store.Save(doc);
        var dialogs = new ScriptedDialogs();
        var now = new[] { new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        var vm = Vm(data, dialogs);
        vm.UtcNow = () => now[0];
        return (vm, data, dialogs, now);
    }

    [Fact]
    public void Con_il_PIN_impostato_l_app_parte_bloccata_e_non_apre_sessioni()
    {
        var (vm, data, _, _) = WithPin();
        using var _ = data;
        Assert.True(vm.IsLocked);
        Assert.False(vm.QuickSearchCommand.CanExecute(null));
        vm.Connect(vm.Document.Connections[0]);
        Assert.Empty(vm.Tabs);
    }

    [Fact]
    public void Sblocca_solo_con_il_PIN_giusto_e_poi_le_password_tornano_leggibili()
    {
        var (vm, data, _, now) = WithPin();
        using var _ = data;
        Assert.Null(vm.Document.Connections[0].Password);
        Assert.False(vm.Unlock("1111"));
        Assert.Equal("PIN sbagliato.", vm.LockError);
        Assert.True(vm.Unlock("2468"));
        Assert.False(vm.IsLocked);
        Assert.Equal("segreta", vm.Document.Connections[0].Password);
        Assert.Null(vm.LockError);
        Assert.Equal(now[0], now[0]);
    }

    [Fact]
    public void Dopo_cinque_errori_ci_sono_attese_crescenti()
    {
        var (vm, data, _, now) = WithPin();
        using var _ = data;
        for (int i = 0; i < 5; i++)
            Assert.False(vm.Unlock("0000"));
        Assert.False(vm.Unlock("2468")); // PIN giusto ma si deve aspettare
        Assert.Contains("Troppi tentativi", vm.LockError);
        now[0] += TimeSpan.FromSeconds(6);
        Assert.True(vm.Unlock("2468"));
    }

    [Fact]
    public void Il_blocco_a_tempo_scatta_dopo_i_minuti_di_inattivita_e_l_attivita_lo_rimanda()
    {
        var (vm, data, _, now) = WithPin();
        using var _ = data;
        vm.Unlock("2468");
        now[0] += TimeSpan.FromMinutes(4);
        Assert.False(vm.CheckIdleLock());
        vm.NotifyActivity();
        now[0] += TimeSpan.FromMinutes(4);
        Assert.False(vm.CheckIdleLock());
        now[0] += TimeSpan.FromMinutes(2);
        vm.Tick();
        Assert.True(vm.IsLocked);
    }

    [Fact]
    public void Con_zero_minuti_non_blocca_da_solo()
    {
        var (vm, data, _, now) = WithPin();
        using var _ = data;
        vm.Unlock("2468");
        vm.Document.Settings.LockAfterMinutes = 0;
        now[0] += TimeSpan.FromHours(10);
        Assert.False(vm.CheckIdleLock());
    }

    [Fact]
    public async Task Blocca_a_mano_senza_PIN_propone_di_impostarlo()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("PC", "h"));
        dialogs.OnShow = o =>
        {
            var p = (PinSetViewModel)o;
            p.Pin = p.ConfirmPin = "1357";
            return p.TryCommit();
        };

        await vm.LockAsync();
        Assert.Contains("serve un PIN", dialogs.ConfirmMessages.Single());
        Assert.True(data.Store.Load().Settings.LockEnabled);
        Assert.False(vm.IsLocked);

        await vm.LockAsync();
        Assert.True(vm.IsLocked);
        Assert.True(vm.Unlock("1357"));
    }

    [Fact]
    public async Task Ho_dimenticato_il_PIN_toglie_PIN_e_password_ma_non_le_connessioni()
    {
        var (vm, data, dialogs, _) = WithPin();
        using var _ = data;
        dialogs.Confirm = false;
        await vm.ForgotPinAsync();
        Assert.True(vm.IsLocked);

        dialogs.Confirm = true;
        await vm.ForgotPinAsync();
        Assert.Contains("1 password", dialogs.ConfirmMessages.Last());
        Assert.False(vm.IsLocked);
        var saved = data.Store.Load();
        Assert.False(saved.Settings.LockEnabled);
        Assert.Single(saved.Connections);
        Assert.False(saved.Connections[0].HasPassword);
    }

    [Fact]
    public void Il_PIN_si_controlla_come_in_Windows()
    {
        var vm = new PinSetViewModel { Pin = "11111", ConfirmPin = "11111" };
        Assert.False(vm.TryCommit());
        vm.Pin = vm.ConfirmPin = "12";
        Assert.False(vm.TryCommit());
        vm.Pin = "2468";
        vm.ConfirmPin = "2469";
        Assert.False(vm.TryCommit());
        Assert.Contains("non coincidono", vm.Error);
        vm.ConfirmPin = "2468";
        Assert.True(vm.TryCommit());
        Assert.True(PinLock.Verify("2468", vm.Result));
    }

    [Fact]
    public async Task La_pagina_PIN_delle_impostazioni_imposta_toglie_e_controlla_i_minuti()
    {
        var settings = new AppSettings();
        var dialogs = new ScriptedDialogs { OnShow = o => { var p = (PinSetViewModel)o; p.Pin = p.ConfirmPin = "2468"; return p.TryCommit(); } };
        var page = new PinPageViewModel(settings, dialogs);
        Assert.Equal("Nessun PIN: il blocco è spento", page.PinState);
        Assert.False(page.RemovePinCommand.CanExecute(null));

        await page.SetPinAsync();
        page.LockAfterMinutes = "15";
        page.Apply();
        Assert.True(settings.LockEnabled);
        Assert.Equal(15, settings.LockAfterMinutes);

        page.RemovePin();
        page.LockAfterMinutes = "x";
        Assert.False(page.Validate());
        page.LockAfterMinutes = "0";
        page.Apply();
        Assert.False(settings.LockEnabled);
        Assert.Equal(0, settings.LockAfterMinutes);
    }
}

/// <summary>Cronologia (#18, #19), testi rapidi (#17).</summary>
public class HistoryAndQuickTextsTests
{
    [Fact]
    public async Task La_cronologia_registra_inizio_e_fine_e_la_finestra_la_mostra()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("PC", "h"));
        vm.Connect(vm.Document.Connections[0]);
        Assert.Single(HistoryStore.Beside(data.FilePath).Load().Entries);
        Assert.True(HistoryStore.Beside(data.FilePath).Load().Entries[0].IsOpen);

        vm.Tabs[0].CloseCommand.Execute(null);
        var entry = HistoryStore.Beside(data.FilePath).Load().Entries.Single();
        Assert.False(entry.IsOpen);
        Assert.Equal("Chiusa dall'utente", entry.Reason);

        HistoryViewModel? shown = null;
        dialogs.OnShow = o => { shown = (HistoryViewModel)o; return true; };
        await vm.ShowHistoryAsync();
        Assert.Single(shown!.Rows);
        Assert.Equal("PC", shown.Rows[0].Name);
    }

    [Fact]
    public void Senza_cronologia_attiva_non_si_registra_nulla()
    {
        using var data = new TempData();
        var doc = new ConnectionsDocument();
        doc.Settings.HistoryEnabled = false;
        doc.Connections.Add(new RdpConnection { Host = "h" });
        data.Store.Save(doc);
        var vm = Vm(data, new ScriptedDialogs());
        vm.Connect(vm.Document.Connections[0]);
        Assert.False(File.Exists(HistoryStore.Beside(data.FilePath).FilePath));
    }

    [Fact]
    public void Le_sessioni_rimaste_aperte_si_chiudono_come_interrotte_al_riavvio()
    {
        using var data = new TempData();
        var store = HistoryStore.Beside(data.FilePath);
        var h = new ConnectionHistory();
        h.Start(new RdpConnection { Host = "h" }, DateTime.UtcNow);
        store.Save(h);
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs);
        Assert.Equal(ConnectionHistory.InterruptedReason, vm.History.Entries.Single().Reason);
    }

    [Fact]
    public async Task Svuota_la_cronologia_con_conferma_e_filtra_per_connessione()
    {
        var doc = new ConnectionsDocument();
        var a = new RdpConnection { Host = "a", Name = "A" };
        var b = new RdpConnection { Host = "b", Name = "B" };
        doc.Connections.AddRange([a, b]);
        var h = new ConnectionHistory();
        h.End(h.Start(a, DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow, "x");
        h.End(h.Start(b, DateTime.UtcNow.AddMinutes(-3)), DateTime.UtcNow, "y");
        bool confirm = false;
        var vm = new HistoryViewModel(h, doc, null, _ => Task.FromResult(confirm));
        Assert.Equal(2, vm.Rows.Count);
        vm.Filter = vm.Filters.First(f => f.Value == a.Id);
        Assert.Single(vm.Rows);
        Assert.Contains("1 sessioni", vm.Summary);

        await vm.ClearShownAsync();
        Assert.False(vm.Changed);
        confirm = true;
        await vm.ClearShownAsync();
        Assert.True(vm.Changed);
        Assert.Single(h.Entries);
    }

    [Fact]
    public async Task Testi_rapidi_nuovo_modifica_ordina_elimina()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs);
        dialogs.OnShow = o =>
        {
            var q = (QuickTextsViewModel)o;
            q.NewCommand.Execute(null);
            q.Name = "Saluto";
            q.Text = "ciao\nmondo";
            Assert.True(q.Save());
            q.NewCommand.Execute(null);
            q.Text = "";
            Assert.False(q.Save());
            Assert.Contains("vuoto", q.Error);
            q.Text = "secondo";
            Assert.True(q.Save());
            q.MoveUpCommand.Execute(null);
            return true;
        };

        await vm.ShowQuickTextsAsync();

        var saved = data.Store.Load().QuickTexts;
        Assert.Equal(["secondo", "ciao\nmondo"], saved.Select(t => t.Text));
        Assert.Equal("Saluto", saved[1].Name);

        dialogs.OnShow = o =>
        {
            var q = (QuickTextsViewModel)o;
            q.Selected = q.Items[0];
            q.DeleteCommand.Execute(null);
            return true;
        };
        await vm.ShowQuickTextsAsync();
        await Task.Yield();
        Assert.Single(data.Store.Load().QuickTexts);
    }
}

/// <summary>Raggiungibilità (#13), Wake-on-LAN (#14), cerca PC in rete (#15).</summary>
public class NetworkToolsTests
{
    [Fact]
    public async Task La_verifica_di_raggiungibilita_colora_l_albero_e_riassume()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("Su", "su"), Seed.Conn("Giu", "giu"));
        vm.ReachabilityProbe = c => Task.FromResult(new ReachabilityResult(
            c.Host == "su" ? ReachabilityStatus.Reachable : ReachabilityStatus.Unreachable, TimeSpan.Zero));

        await vm.CheckReachabilityAsync(announce: true);

        Assert.Equal("Raggiungibili 1 su 2", vm.StatusMessage);
        Assert.Equal("Raggiungibile", Node(vm, "Su").StatusText);
        Assert.Equal("#2EA043", Node(vm, "Su").StatusColor);
        Assert.Equal("Non raggiungibile", Node(vm, "Giu").StatusText);
        vm.SearchText = "su";
        Assert.Equal(ReachabilityStatus.Reachable, Node(vm, "Su").Reachability); // lo stato sopravvive alla ricostruzione
    }

    [Fact]
    public async Task La_raggiungibilita_vera_usa_una_connessione_tcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var data = new TempData();
        var open = Seed.Conn("Aperta", "127.0.0.1");
        open.Port = port;
        var vm = Vm(data, new ScriptedDialogs(), open);
        vm.SelectedNode = Node(vm, "Aperta");
        await vm.CheckReachabilityAsync(announce: true);
        Assert.Contains("raggiungibile", vm.StatusMessage);
        Assert.DoesNotContain("non raggiungibile", vm.StatusMessage);
    }

    [Fact]
    public async Task Wake_on_lan_invia_il_pacchetto_e_aspetta_che_il_pc_risponda()
    {
        using var data = new TempData();
        var c = Seed.Conn("PC", "pc");
        c.MacAddress = "AA:BB:CC:DD:EE:FF";
        var vm = Vm(data, new ScriptedDialogs(), c);
        vm.SelectedNode = Node(vm, "PC");
        var sent = new List<string>();
        int probes = 0;
        vm.WakeSender = mac => { sent.Add(mac); return Task.CompletedTask; };
        vm.WakeRecheckDelay = TimeSpan.Zero;
        vm.ReachabilityProbe = _ => Task.FromResult(new ReachabilityResult(++probes < 3 ? ReachabilityStatus.Unreachable : ReachabilityStatus.Reachable, TimeSpan.Zero));

        await vm.WakeAsync();

        Assert.Equal(["AA:BB:CC:DD:EE:FF"], sent);
        Assert.Equal(3, probes);
        Assert.Equal("«PC» è raggiungibile.", vm.StatusMessage);
    }

    [Fact]
    public async Task Wake_on_lan_senza_mac_propone_la_modifica()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("PC", "pc"));
        vm.SelectedNode = Node(vm, "PC");
        bool sent = false;
        vm.WakeSender = _ => { sent = true; return Task.CompletedTask; };
        await vm.WakeAsync();
        Assert.False(sent);
        Assert.Contains("indirizzo MAC", dialogs.ConfirmMessages.Single());
    }

    [Fact]
    public void Il_pacchetto_magico_e_quello_del_core()
    {
        Assert.Equal(WakeOnLan.MagicPacketLength, WakeOnLan.BuildMagicPacket("AA:BB:CC:DD:EE:FF").Length);
        Assert.True(WakeOnLan.IsValidMac("aa-bb-cc-dd-ee-ff"));
    }

    private static AdapterInfo Adapter(string name, string ip, NetworkKind kind) =>
        new(name, name, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, IPAddress.Parse(ip), 24, kind);

    [Fact]
    public async Task Cerca_pc_in_rete_trova_i_pc_li_aggiunge_senza_doppioni_e_li_mette_nella_cartella()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("Esistente", "192.168.1.11"));
        var sources = new NetworkScanSources
        {
            Adapters = () => [Adapter("eth0", "192.168.1.5", NetworkKind.Local)],
            Scanner = new NetworkScanner((_, _) => Task.FromResult<string?>("host.lan"), (ip, port, _, _) => Task.FromResult(ip.ToString() is "192.168.1.10" or "192.168.1.11")),
        };
        vm.ScanSources = sources;
        NetworkScanViewModel? scan = null;
        dialogs.OnShow = o =>
        {
            scan = (NetworkScanViewModel)o;
            scan.LocalTab.Range = "192.168.1.1-192.168.1.20";
            scan.LocalTab.StartAsync().GetAwaiter().GetResult();
            Assert.Equal(2, scan.LocalTab.Results.Count);
            Assert.Single(scan.LocalTab.Results, r => r.Note == "già presente");
            scan.Folder = "Rete/Ufficio";
            var result = scan.AddSelected();
            Assert.Single(result!.Added);
            return true;
        };

        await vm.ShowNetworkScanAsync();

        var added = data.Store.Load().Connections.Single(c => c.Host == "192.168.1.10");
        Assert.Equal("Rete/Ufficio", added.Group);
        Assert.Contains("Aggiunti 1 PC", scan!.Summary);
    }

    [Fact]
    public async Task Cerca_pc_in_rete_rifiuta_intervalli_non_validi_e_enormi()
    {
        var doc = new ConnectionsDocument();
        var sources = new NetworkScanSources { Adapters = () => [] };
        var vm = new NetworkScanViewModel(doc, (_, _) => new DiscoveryImportResult([], 0), _ => Task.FromResult(false), sources);
        Assert.NotNull(vm.LocalTab.EmptyNote);
        vm.LocalTab.Range = "non un indirizzo";
        await vm.LocalTab.StartAsync();
        Assert.True(vm.LocalTab.StatusIsProblem);
        vm.LocalTab.Range = "10.0.0.0/8";
        await vm.LocalTab.StartAsync();
        Assert.True(vm.LocalTab.StatusIsProblem);
        Assert.Empty(vm.LocalTab.Results);
    }

    [Fact]
    public async Task Scheda_tailscale_elenca_i_dispositivi_e_controlla_il_desktop_remoto()
    {
        var doc = new ConnectionsDocument();
        var json = """
        {"BackendState":"Running","MagicDNSSuffix":"x.ts.net","Self":{"HostName":"io","DNSName":"io.x.ts.net.","TailscaleIPs":["100.64.0.1"],"OS":"linux","Online":true},
         "Peer":{"k1":{"HostName":"uno","DNSName":"uno.x.ts.net.","TailscaleIPs":["100.64.0.2"],"OS":"windows","Online":true},
                 "k2":{"HostName":"due","DNSName":"due.x.ts.net.","TailscaleIPs":["100.64.0.3"],"OS":"linux","Online":false}}}
        """;
        var sources = new NetworkScanSources
        {
            Adapters = () => [],
            Tailscale = _ => Task.FromResult(TailscaleCli.Interpret(0, json, "")),
            RdpCheck = (_, _, _) => Task.FromResult(true),
        };
        var vm = new NetworkScanViewModel(doc, (pcs, folder) => DiscoveryImport.ImportInto(doc, pcs, folder), _ => Task.FromResult(true), sources);
        await vm.TailscaleTab.ReadAsync();
        Assert.Equal(2, vm.TailscaleTab.Results.Count);
        var uno = vm.TailscaleTab.Results.Single(r => r.Name == "uno");
        Assert.Equal("risponde", uno.Note);
        Assert.True(uno.IsChecked);
        Assert.False(vm.TailscaleTab.Results.Single(r => r.Name == "due").IsChecked); // fuori linea

        vm.SelectedTab = vm.TailscaleTab;
        vm.AddSelected();
        Assert.Single(doc.Connections);
    }

    [Fact]
    public async Task Scheda_tailscale_senza_programma_mostra_il_messaggio()
    {
        var sources = new NetworkScanSources
        {
            Adapters = () => [],
            Tailscale = _ => Task.FromResult(new TailscaleLookup(TailscaleLookupOutcome.NotInstalled, null, "non installato")),
        };
        var vm = new NetworkScanViewModel(new ConnectionsDocument(), (_, _) => new DiscoveryImportResult([], 0), _ => Task.FromResult(true), sources);
        await vm.TailscaleTab.ReadAsync();
        Assert.Equal("non installato", vm.TailscaleTab.Status);
        Assert.True(vm.TailscaleTab.StatusIsProblem);
    }

    [Fact]
    public void Il_programma_tailscale_si_cerca_senza_exe()
    {
        Assert.Equal("/opt/bin/tailscale", RdpManager.Linux.Services.LinuxTailscale.FindExecutable(p => p == "/opt/bin/tailscale", "/x:/opt/bin"));
        Assert.Null(RdpManager.Linux.Services.LinuxTailscale.FindExecutable(_ => false, "/x"));
    }
}

/// <summary>Impostazioni (pagine A), cartelle di sistema, informazioni, modulo di modifica (#4, #29, #44, #54, #55, #56, #57).</summary>
public class SettingsAndInfoTests
{
    [Fact]
    public async Task Le_impostazioni_si_applicano_e_aggiornano_registro_e_ordine()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = Vm(data, dialogs, Seed.Conn("B", "b"), Seed.Conn("A", "a"));
        bool changed = false;
        vm.SettingsChanged += (_, _) => changed = true;
        dialogs.OnSettings = s =>
        {
            s.Pages.OfType<AvvioPageViewModel>().Single().ReopenSessionsOnStartup = true;
            s.Pages.OfType<AvvioPageViewModel>().Single().VerboseLog = true;
            s.Pages.OfType<ElencoPageViewModel>().Single().ManualOrder = true;
            s.Pages.OfType<AspettoPageViewModel>().Single().Theme = Choices.Pick(Choices.Theme, AppTheme.Dark);
            return s.Validate();
        };

        await vm.ShowSettingsAsync();

        var saved = data.Store.Load().Settings;
        Assert.Equal((true, true, ConnectionOrder.Manual, AppTheme.Dark), (saved.ReopenSessionsOnStartup, saved.VerboseLog, saved.ConnectionOrder, saved.Theme));
        Assert.True(changed);
    }

    [Fact]
    public void La_pagina_avvio_ha_la_casella_di_riapertura_e_quella_della_cronologia_separate()
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Dialogs/Settings/PaginaAvvio.axaml"));
        Assert.Contains("Riapri all'avvio le sessioni lasciate aperte", axaml);
        Assert.Contains("ReopenSessionsOnStartup", axaml);
        Assert.Contains("Registra la cronologia delle connessioni", axaml);
        var settings = new AppSettings { ReopenSessionsOnStartup = false, HistoryEnabled = true };
        var page = new AvvioPageViewModel(settings, "/x/c.json");
        page.ReopenSessionsOnStartup = true;
        page.HistoryEnabled = false;
        page.Apply();
        Assert.Equal((true, false), (settings.ReopenSessionsOnStartup, settings.HistoryEnabled));
    }

    [Fact]
    public void Le_pagine_aspetto_avvio_elenco_e_pin_hanno_contenuto_proprio()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/c.json");
        foreach (var page in vm.Pages.Take(4))
            Assert.IsNotAssignableFrom<PlaceholderSettingsPageViewModel>(page);
        Assert.DoesNotContain("Notice", File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Dialogs/Settings/PaginaPin.axaml")));
    }

    [Fact]
    public void Le_cartelle_dei_dati_e_dei_log_si_aprono_con_la_shell()
    {
        using var data = new TempData();
        var shell = new FakeShell();
        var vm = Vm(data, new ScriptedDialogs(), shell);
        vm.OpenDataFolderCommand.Execute(null);
        vm.OpenLogFolderCommand.Execute(null);
        Assert.Equal(Path.GetDirectoryName(data.FilePath), shell.Folders[0]);
        Assert.Equal(AppLog.DefaultDirectory, shell.Folders[1]);

        shell.Succeeds = false;
        vm.OpenDataFolderCommand.Execute(null);
        Assert.Contains("Non riesco ad aprire", vm.StatusMessage);
    }

    [Fact]
    public async Task Informazioni_mostra_versione_progetto_e_dati_e_la_pagina_si_apre()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var shell = new FakeShell();
        var vm = Vm(data, dialogs, shell);
        await vm.ShowAboutAsync();
        var text = dialogs.Messages.Single();
        Assert.Contains(AppInfo.VersionText, text);
        Assert.Contains(AppInfo.RepositoryUrl, text);
        Assert.Contains(data.FilePath, text);

        vm.OpenProjectPageCommand.Execute(null);
        Assert.Equal([AppInfo.RepositoryUrl], shell.Urls);
    }

    [Fact]
    public void Registro_e_registro_dei_guasti_si_accendono_e_seguono_il_log_dettagliato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-log-" + Guid.NewGuid().ToString("N"));
        var oldLog = AppLog.Default;
        var oldCrash = CrashLog.Default;
        try
        {
            AppLog.Default = null;
            CrashLog.Default = null;
            var settings = new AppSettings { VerboseLog = true };
            RdpManager.Linux.Services.LogSetup.Initialize(settings, dir);
            Assert.NotNull(AppLog.Default);
            Assert.NotNull(CrashLog.Default);
            Assert.True(AppLog.Default!.Verbose);
            settings.VerboseLog = false;
            RdpManager.Linux.Services.LogSetup.ApplySettings(settings);
            Assert.False(AppLog.Default.Verbose);
            Assert.True(File.Exists(AppLog.Default.CurrentPath));
        }
        finally
        {
            AppLog.Default = oldLog;
            CrashLog.Default = oldCrash;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Il_modulo_di_modifica_ha_i_campi_mancanti()
    {
        var doc = new ConnectionsDocument();
        var c = new RdpConnection { Host = "pc" };
        var vm = new ConnectionEditViewModel(doc, c, true)
        {
            Color = "#abc",
            AutoReconnect = true,
            AutoReconnectMaxAttempts = "7",
            InheritFolderResolution = true,
            UseAllMonitors = true,
        };
        Assert.Equal("#AABBCC", vm.ColorPreview);
        Assert.True(vm.TryCommit());
        Assert.Equal(("#AABBCC", 7, true, true), (c.Color, c.AutoReconnectMaxAttempts, c.InheritFolderResolution, c.UseAllMonitors));

        vm.Color = "rosso";
        Assert.False(vm.TryCommit());
        vm.Color = "";
        vm.AutoReconnectMaxAttempts = "99";
        Assert.False(vm.TryCommit());
        vm.AutoReconnectMaxAttempts = "3";
        Assert.True(vm.TryCommit());
        Assert.Null(c.Color);
    }

    [Fact]
    public void Il_computer_puo_avere_la_porta_dopo_i_due_punti()
    {
        var c = new RdpConnection();
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), c, true) { Host = "pc:3390", Port = "xx" };
        Assert.True(vm.TryCommit());
        Assert.Equal(("pc", 3390), (c.Host, c.Port));
    }

    [Fact]
    public void Password_non_leggibile_da_questo_utente_si_segnala_e_si_puo_riscrivere()
    {
        var c = new RdpConnection { Host = "pc", Password = null, ProtectedPassword = "v1:altro-utente" };
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), c, false);
        Assert.True(vm.HasUnreadablePassword);
        Assert.Contains("non leggibile", vm.PasswordHint);
        Assert.NotNull(vm.PasswordWarning);
        Assert.True(vm.TryCommit());
        Assert.Equal("v1:altro-utente", c.ProtectedPassword); // vuota: non si tocca

        vm.Password = "nuova";
        Assert.True(vm.TryCommit());
        Assert.Equal("nuova", c.Password);
        Assert.Null(c.ProtectedPassword);

        var plain = new ConnectionEditViewModel(new ConnectionsDocument(), new RdpConnection { Host = "pc", Password = "x" }, false);
        Assert.False(plain.HasUnreadablePassword);
        Assert.Null(plain.PasswordWarning);
    }

    [Fact]
    public void Con_un_profilo_utente_dominio_e_password_sono_del_profilo_e_il_gateway_ha_le_sue_credenziali()
    {
        var doc = new ConnectionsDocument();
        var p = new CredentialProfile { Name = "P", Username = "u" };
        doc.CredentialProfiles.Add(p);
        var c = new RdpConnection { Host = "pc", UseGateway = true, GatewayHost = "gw" };
        var vm = new ConnectionEditViewModel(doc, c, true);
        Assert.True(vm.OwnCredentials);
        vm.Profile = vm.Profiles.First(x => x.Value == p.Id);
        Assert.False(vm.OwnCredentials);
        Assert.Equal("(dal profilo)", vm.PasswordHint);
        vm.GatewayProfile = vm.GatewayProfiles.First(x => x.Value == p.Id);
        Assert.True(vm.TryCommit());
        Assert.Equal((GatewayCredentialsSource.Profile, p.Id), (c.GatewayCredentials, c.GatewayProfileId));
    }

    [Fact]
    public void Il_colore_e_lo_stato_arrivano_alla_riga_dell_albero()
    {
        var c = new RdpConnection { Host = "pc", Color = "#ff0000" };
        var node = TreeNodeViewModel.ForConnection(c);
        Assert.Equal("#FF0000", node.LabelColor);
        Assert.True(node.HasLabelColor);
        Assert.Equal("Non verificata", node.StatusText);
        Assert.Equal("", node.StatusColor);
        Assert.False(TreeNodeViewModel.ForConnection(new RdpConnection { Host = "x" }).HasLabelColor);
    }
}

/// <summary>Il menu e la finestra usano davvero i comandi del blocco A.</summary>
public class BlockAWiringTests
{
    [Fact]
    public void I_comandi_del_blocco_A_sono_abilitati_secondo_la_selezione()
    {
        using var data = new TempData();
        var vm = Vm(data, new ScriptedDialogs(), Seed.Conn("PC", "pc", "Cart"));
        Assert.True(vm.ImportRdpCommand.CanExecute(null));
        Assert.True(vm.HistoryCommand.CanExecute(null));
        Assert.True(vm.AboutCommand.CanExecute(null));
        Assert.False(vm.FolderSettingsCommand.CanExecute(null));
        Assert.False(vm.WakeCommand.CanExecute(null));

        vm.SelectedNode = Node(vm, "Cart");
        Assert.True(vm.FolderSettingsCommand.CanExecute(null));
        Assert.True(vm.ConnectFolderCommand.CanExecute(null));
        Assert.False(vm.ExportRdpFileCommand.CanExecute(null));

        vm.SelectedNode = Node(vm, "PC");
        Assert.True(vm.WakeCommand.CanExecute(null));
        Assert.True(vm.ExportRdpFileCommand.CanExecute(null));
        Assert.True(vm.MoveToFolderCommand.CanExecute(null));
        Assert.False(vm.FolderSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void Il_menu_ha_preferiti_recenti_e_le_azioni_di_gruppo()
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Menus/MainMenu.axaml"));
        Assert.Contains("ItemsSource=\"{Binding FavoriteItems}\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding RecentItems}\"", axaml);
        Assert.Contains("MoveToFolderCommand", axaml);
        Assert.Contains("AssignProfileCommand", axaml);
    }

    [Fact]
    public void La_finestra_ha_selezione_multipla_trascinamento_blocco_e_scorciatoie()
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/MainWindow.axaml"));
        Assert.Contains("WindowBehaviors.MultiSelect", axaml);
        Assert.Contains("WindowBehaviors.DragDrop", axaml);
        Assert.Contains("WindowBehaviors.BackgroundWork", axaml);
        Assert.Contains("IsLocked", axaml);
        Assert.Contains("Ho dimenticato il PIN", axaml);
        // Ctrl+Alt+Spazio e Ctrl+Alt+L stanno nella ShortcutMap configurabile del blocco B, non più fra i tasti fissi.
        var map = new RdpManager.Linux.Features.ShortcutMap();
        Assert.Equal("Ctrl+Alt+Spazio", map[RdpManager.Linux.Features.ShortcutAction.QuickSearch].ToString());
        Assert.Equal("Ctrl+Alt+L", map[RdpManager.Linux.Features.ShortcutAction.Lock].ToString());
        Assert.DoesNotContain("Ctrl+Alt+L\"", axaml);
        Assert.Contains("<KeyBinding Gesture=\"Escape\"", axaml);
    }

    [Fact]
    public void I_testi_dei_dialoghi_sono_in_italiano()
    {
        var doc = new ConnectionsDocument();
        Assert.Equal("Esporta connessioni", new RdpmExportViewModel(doc, _ => Task.FromResult(true)).WindowTitle);
        Assert.Equal("Importa connessioni", new RdpmImportViewModel(1, 0, false, false, _ => Task.FromResult(true)).WindowTitle);
        Assert.Equal("Cerca PC in rete", new NetworkScanViewModel(doc, (_, _) => new DiscoveryImportResult([], 0), _ => Task.FromResult(true),
            new NetworkScanSources { Adapters = () => [] }).WindowTitle);
    }
}
