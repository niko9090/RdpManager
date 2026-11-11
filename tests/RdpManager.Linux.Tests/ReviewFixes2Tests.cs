using Avalonia.Input;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Platform;
using RdpManager.Linux.ViewModels;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

/// <summary>Correzioni della revisione del blocco A (k-72): il PIN non si aggira, FreeRDP non riceve righe iniettate.</summary>
public class LockBypassTests
{
    private static (MainWindowViewModel Vm, TempData Data, ScriptedDialogs Dialogs) Locked()
    {
        var data = new TempData();
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Host = "h", Name = "PC", Password = "segreta" });
        doc.Settings.LockPin = PinLock.Create("2468", 10_000);
        doc.Settings.LockAfterMinutes = 5;
        data.Store.Save(doc);
        var dialogs = new ScriptedDialogs();
        return (A.Vm(data, dialogs), data, dialogs);
    }

    [Fact]
    public async Task Da_bloccata_i_comandi_e_i_metodi_non_fanno_niente()
    {
        var (vm, data, dialogs) = Locked();
        using var _ = data;
        Assert.True(vm.IsLocked);
        vm.SelectedNode = A.Node(vm, "PC");
        foreach (var c in new[] { vm.NewConnectionCommand, vm.NewFolderCommand, vm.EditCommand, vm.DuplicateCommand, vm.DeleteCommand,
                     vm.ConnectCommand, vm.FavoriteCommand, vm.ProfilesCommand, vm.SettingsCommand, vm.HistoryCommand, vm.QuickTextsCommand,
                     vm.SortByNameCommand, vm.CheckReachabilityCommand, vm.OpenDataFolderCommand, vm.NextTabCommand, vm.SnapshotCommand })
        {
            Assert.False(c.CanExecute(null));
            c.Execute(null); // anche forzato non deve partire
        }
        await vm.ShowSettingsAsync();
        await vm.EditSelectedAsync();
        await vm.DeleteSelectedAsync();
        await vm.NewConnectionAsync();
        vm.DuplicateSelected();
        vm.ToggleFavorite();
        Assert.Single(vm.Document.Connections);
        Assert.False(vm.Document.Connections[0].IsFavorite);
        Assert.Empty(dialogs.Shown);
        Assert.False(vm.TryRunShortcut(KeyCombo.From(Key.D, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.False(vm.RunShortcut(ShortcutAction.ToggleFullScreen));
        Assert.True(vm.Unlock("2468"));
        Assert.True(vm.NewConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Bloccando_si_chiudono_i_dialoghi_aperti()
    {
        var (vm, data, dialogs) = Locked();
        using var _ = data;
        vm.Unlock("2468");
        vm.Lock();
        Assert.Equal(1, dialogs.CloseAllCalls);
    }

    [Fact]
    public void PIN_dimenticato_chiude_tutte_le_schede()
    {
        var (vm, data, _) = Locked();
        using var _ = data;
        vm.Unlock("2468");
        vm.Connect(vm.Document.Connections[0]);
        vm.Lock();
        Assert.Single(vm.Tabs);
        vm.ForgetPinConfirmed();
        Assert.Empty(vm.Tabs);
        Assert.False(vm.IsLocked);
    }

    [Fact]
    public void I_tentativi_falliti_sopravvivono_al_riavvio()
    {
        var (vm, data, _) = Locked();
        using var _ = data;
        for (int i = 0; i < 5; i++)
            Assert.False(vm.Unlock("0000"));
        var restarted = A.Vm(data, new ScriptedDialogs());
        Assert.True(restarted.IsLocked);
        Assert.False(restarted.Unlock("2468")); // PIN giusto, ma l'attesa non si azzera riavviando
        Assert.Contains("Troppi tentativi", restarted.LockError);
    }

    [Fact]
    public void Dopo_uno_sblocco_riuscito_i_tentativi_si_azzerano_anche_su_disco()
    {
        var (vm, data, _) = Locked();
        using var _ = data;
        Assert.False(vm.Unlock("0000"));
        Assert.True(vm.Unlock("2468"));
        vm.Lock();
        var restarted = A.Vm(data, new ScriptedDialogs());
        Assert.True(restarted.Unlock("2468"));
    }

    [Fact]
    public void Il_battito_usa_l_inattivita_del_sistema_e_non_lancia_errori()
    {
        var (vm, data, _) = Locked();
        using var _ = data;
        vm.Unlock("2468");
        vm.Tick(TimeSpan.FromMinutes(1));
        Assert.False(vm.IsLocked);
        // Fermo da 10 minuti per il sistema, ma la finestra ha visto un tasto un istante fa: non si blocca.
        vm.NotifyActivity();
        vm.Tick(TimeSpan.FromMinutes(10));
        Assert.False(vm.IsLocked);
        // Ferme entrambe: si blocca.
        var clock = DateTime.UtcNow;
        vm.UtcNow = () => clock.AddMinutes(10);
        vm.Tick(TimeSpan.FromMinutes(10));
        Assert.True(vm.IsLocked);
    }

    [Fact]
    public void Le_conferme_distruttive_usano_il_pulsante_predefinito_No()
    {
        using var data = new TempData();
        var dialogs = new ScriptedDialogs();
        var vm = A.Vm(data, dialogs, Seed.Conn("PC", "h"));
        vm.SelectedNode = A.Node(vm, "PC");
        vm.DeleteCommand.Execute(null);
        Assert.Equal(1, dialogs.DestructiveCalls);
    }

    [Fact]
    public void Rinominare_una_cartella_col_nome_di_una_sorella_si_rifiuta()
    {
        var doc = new ConnectionsDocument();
        doc.AddFolder("", "Uno");
        doc.AddFolder("", "Due");
        Assert.Null(doc.RenameFolder("Uno", "due"));
        Assert.Contains("Uno", doc.AllGroups());
        Assert.Equal("UNO", doc.RenameFolder("Uno", "UNO"));
    }

    [Fact]
    public void Un_history_json_rovinato_si_tiene_come_bad()
    {
        using var data = new TempData();
        var path = Path.Combine(Path.GetDirectoryName(data.FilePath)!, HistoryStore.FileName);
        File.WriteAllText(path, "{ non e json");
        var history = new HistoryStore(path).Load();
        Assert.Empty(history.Entries);
        Assert.False(File.Exists(path));
        Assert.Equal("{ non e json", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Il_valore_di_gdbus_si_legge_in_secondi()
    {
        Assert.Equal(TimeSpan.FromSeconds(123), SystemIdle.ParseDbusSeconds("(uint32 123,)\n"));
        Assert.Null(SystemIdle.ParseDbusSeconds("Error"));
    }
}

public class FreeRdpInjectionTests
{
    private static readonly FreeRdpClient V3 = new("xfreerdp3", true, false);
    private static readonly FreeRdpClient V2 = new("xfreerdp", false, false);

    [Theory]
    [InlineData("ab\ncd")]
    [InlineData("ab\rcd")]
    [InlineData("ab\0cd")]
    public void Una_password_con_a_capo_o_nul_si_rifiuta(string password)
    {
        var c = new RdpConnection { Host = "pc", Password = password };
        Assert.Throws<ArgumentException>(() => FreeRdpCommandBuilder.Build(c, V3));
        Assert.Throws<ArgumentException>(() => FreeRdpCommandBuilder.Build(c, V2));
    }

    [Fact]
    public void Anche_la_password_del_gateway_si_controlla()
    {
        var c = new RdpConnection { Host = "pc", UseGateway = true, GatewayHost = "gw" };
        var ctx = new FreeRdpLaunchContext { Gateway = new ResolvedCredentials("u", "", "x\n/v:evil") };
        Assert.Throws<ArgumentException>(() => FreeRdpCommandBuilder.Build(c, V3, ctx));
    }

    [Fact]
    public void Con_args_from_stdin_tutti_gli_argomenti_vanno_nello_stdin()
    {
        var c = new RdpConnection { Host = "pc", Name = "Titolo\n/v:evil", Password = "segreta" };
        var cmd = FreeRdpCommandBuilder.Build(c, V3);
        Assert.Equal(["/args-from:stdin"], cmd.CommandLine);
        var lines = cmd.AllStdinLines;
        Assert.Contains("/v:pc", lines);
        Assert.Contains("/p:segreta", lines);
        Assert.DoesNotContain("/v:evil", lines);
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
        Assert.DoesNotContain("segreta", cmd.ToString());
    }

    [Fact]
    public void FreeRDP_2_tiene_gli_argomenti_sulla_riga_e_la_password_nello_stdin()
    {
        var cmd = FreeRdpCommandBuilder.Build(new RdpConnection { Host = "pc", Password = "segreta" }, V2);
        Assert.Contains("/from-stdin", cmd.CommandLine);
        Assert.Equal(["segreta"], cmd.AllStdinLines);
    }

    [Fact]
    public void L_importazione_di_un_rdpm_toglie_gli_a_capo_dalle_password()
    {
        var doc = new ConnectionsDocument();
        doc.Connections.Add(new RdpConnection { Host = "pc", Name = "A", Password = "ab\ncd\0" });
        var json = RdpmFile.Export(doc, "filepw", iterations: 1000);
        var imported = RdpmFile.Import(json, "filepw");
        Assert.Equal("abcd", imported.Connections[0].Password);
    }
}
