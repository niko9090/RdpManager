using Avalonia.Input;
using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.Platform;
using RdpManager.Linux.Sessions;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Settings;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

internal sealed class RecordingRunner(Func<string, IReadOnlyList<string>, ProcessResult>? answer = null) : IProcessRunner
{
    public List<string> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null, CancellationToken ct = default)
    {
        Calls.Add(fileName + " " + string.Join(' ', arguments));
        return Task.FromResult(answer?.Invoke(fileName, arguments) ?? new ProcessResult(0, "", ""));
    }
}

public class DisplayServerTests
{
    private static Func<string, string?> Env(params (string, string)[] vars) =>
        name => vars.FirstOrDefault(v => v.Item1 == name).Item2;

    [Fact]
    public void Rileva_wayland_x11_e_sconosciuto()
    {
        Assert.Equal(DisplayServerKind.Wayland, DisplayServer.Detect(Env(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0"))));
        Assert.Equal(DisplayServerKind.X11, DisplayServer.Detect(Env(("XDG_SESSION_TYPE", "x11"))));
        Assert.Equal(DisplayServerKind.Wayland, DisplayServer.Detect(Env(("WAYLAND_DISPLAY", "wayland-0"))));
        Assert.Equal(DisplayServerKind.X11, DisplayServer.Detect(Env(("DISPLAY", ":0"))));
        Assert.Equal(DisplayServerKind.Unknown, DisplayServer.Detect(Env()));
    }

    [Fact]
    public void Su_wayland_la_nota_spiega_la_finestra_separata_e_X11()
    {
        var note = DisplayServer.SessionNote(DisplayServerKind.Wayland, embedded: false)!;
        Assert.Contains("Wayland", note);
        Assert.Contains("finestra separata", note);
        Assert.Contains("X11", note);
        Assert.Null(DisplayServer.SessionNote(DisplayServerKind.X11, embedded: true));
        Assert.False(DisplayServer.CanGlobalHotkey(DisplayServerKind.Wayland));
        Assert.True(DisplayServer.CanGlobalHotkey(DisplayServerKind.X11));
    }
}

public class ShortcutMapTests
{
    [Fact]
    public void I_predefiniti_sono_quelli_di_Windows()
    {
        var map = new ShortcutMap();
        Assert.Equal("Ctrl+Alt+Destra", map[ShortcutAction.NextSession].ToString());
        Assert.Equal("Ctrl+Alt+1", map[ShortcutAction.Session1].ToString());
        Assert.Empty(map.Conflicts());
    }

    [Fact]
    public void Le_impostazioni_cambiano_o_disattivano_e_tornano_indietro()
    {
        var map = ShortcutMap.FromSettings(new Dictionary<string, string>
        {
            ["NextSession"] = "Ctrl+Maiusc+F5", ["Overview"] = "", ["Lock"] = "rotto++", ["Inesistente"] = "Ctrl+A",
        });
        Assert.Equal(Key.F5, map[ShortcutAction.NextSession].Key);
        Assert.True(map[ShortcutAction.Overview].IsEmpty);
        Assert.Equal(Key.L, map[ShortcutAction.Lock].Key); // non valida: resta il predefinito
        var saved = map.ToSettings();
        Assert.Equal("Ctrl+Maiusc+F5", saved["NextSession"]);
        Assert.Equal("", saved["Overview"]);
        Assert.False(saved.ContainsKey("Lock"));
    }

    [Fact]
    public void Un_conflitto_si_segnala()
    {
        var map = new ShortcutMap();
        map[ShortcutAction.Overview] = map[ShortcutAction.Lock];
        Assert.Single(map.Conflicts());
    }

    [Fact]
    public void Combinazione_si_legge_e_si_riconosce_dal_tasto_premuto()
    {
        Assert.True(KeyCombo.TryParse("ctrl + alt + right", out var c));
        Assert.Equal(KeyCombo.From(Key.Right, KeyModifiers.Control | KeyModifiers.Alt), c);
        Assert.False(KeyCombo.TryParse("Ctrl+", out _));
        Assert.False(KeyCombo.TryParse("Ctrl+A+B", out _));
        Assert.True(new ShortcutMap().TryGetAction(c, out var action));
        Assert.Equal(ShortcutAction.NextSession, action);
    }

    [Fact]
    public void La_pagina_di_aiuto_elenca_le_scorciatoie_fisse()
    {
        var text = new ShortcutMap().SummaryText();
        Assert.Contains("Ctrl+F4", text);
        Assert.Contains("F5", text);
        Assert.Contains("Sessione successiva", text);
    }
}

public class CertificateTrustTests
{
    [Theory]
    [InlineData(1, "The host key for pc.example.com has changed")]
    [InlineData(1, "Certificate verification failure 'self-signed certificate' at stack position 0")]
    [InlineData(143, "")]
    public void Riconosce_il_problema_di_certificato(int code, string text) =>
        Assert.True(CertificateTrust.IsCertificateProblem(code, text));

    [Fact]
    public void Una_password_sbagliata_non_e_un_problema_di_certificato()
    {
        Assert.False(CertificateTrust.IsCertificateProblem(132, "freerdp_set_last_error_ex ERRCONNECT_AUTHENTICATION_FAILED"));
    }

    [Fact]
    public void Accettare_usa_la_politica_ignore_del_costruttore_dei_comandi()
    {
        Assert.Equal("/cert:ignore", FreeRdpCommandBuilder.CertificateArgument(CertificatePolicy.Ignore, true));
        Assert.Equal("/cert-ignore", FreeRdpCommandBuilder.CertificateArgument(CertificatePolicy.Ignore, false));
        Assert.Equal("/cert:tofu", FreeRdpCommandBuilder.CertificateArgument(CertificatePolicy.Tofu, true));
    }

    [Fact]
    public void Si_ritenta_solo_per_errori_di_rete()
    {
        Assert.True(CertificateTrust.IsRetryableExit(131, "connect failed"));
        Assert.False(CertificateTrust.IsRetryableExit(0, ""));
        Assert.False(CertificateTrust.IsRetryableExit(11, "disconnect by user"));
        Assert.False(CertificateTrust.IsRetryableExit(132, "authentication failure"));
    }
}

public class RemoteInputTests
{
    [Fact]
    public void Comandi_per_X11_e_Wayland()
    {
        Assert.Equal("xdotool key --clearmodifiers ctrl+alt+Delete", RemoteInput.CtrlAltDelCommand("xdotool").ToString());
        Assert.StartsWith("ydotool key 29:1", RemoteInput.CtrlAltDelCommand("ydotool").ToString());
        Assert.Contains("-- ciao", RemoteInput.TypeCommand("xdotool", "ciao").ToString());
        Assert.Equal("xdotool click 3", RemoteInput.KeyComboCommand("click:3").ToString());
    }

    [Fact]
    public async Task Senza_xdotool_il_messaggio_dice_come_installarlo()
    {
        var input = new RemoteInput(new RecordingRunner(), DisplayServerKind.X11, _ => false);
        var error = await input.SendCtrlAltDelAsync();
        Assert.Contains("xdotool", error);
        Assert.Contains("apt", error);
    }

    [Fact]
    public async Task Digita_il_testo_con_xdotool_e_rifiuta_testi_troppo_lunghi()
    {
        var runner = new RecordingRunner();
        var input = new RemoteInput(runner, DisplayServerKind.X11, n => n == "xdotool");
        Assert.Null(await input.TypeAsync("ciao"));
        Assert.Single(runner.Calls);
        Assert.NotNull(await input.TypeAsync(new string('a', RemoteInput.MaxTypedLength + 1)));
        Assert.NotNull(await input.TypeAsync(""));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void La_foto_usa_import_su_X11_e_grim_su_Wayland()
    {
        var x11 = new RemoteInput(new RecordingRunner(), DisplayServerKind.X11, _ => true);
        Assert.Equal("import -window 0x1a2b /tmp/a.png", x11.SnapshotCommand(0x1a2b, "/tmp/a.png", out _)!.ToString());
        var wayland = new RemoteInput(new RecordingRunner(), DisplayServerKind.Wayland, n => n == "grim");
        Assert.Equal("grim /tmp/a.png", wayland.SnapshotCommand(null, "/tmp/a.png", out _)!.ToString());
        var none = new RemoteInput(new RecordingRunner(), DisplayServerKind.Wayland, _ => false);
        Assert.Null(none.SnapshotCommand(null, "/tmp/a.png", out var missing));
        Assert.Contains("Wayland", missing);
    }

    [Fact]
    public void Tastiera_su_schermo_sceglie_il_primo_programma_installato()
    {
        Assert.Equal("florence", new RemoteInput(null, DisplayServerKind.X11, n => n is "florence" or "xvkbd").ScreenKeyboardProgram());
        Assert.Null(new RemoteInput(null, DisplayServerKind.X11, _ => false).ScreenKeyboardProgram());
    }

    [Fact]
    public void Nome_del_file_della_foto_e_sicuro()
    {
        var name = RemoteInput.SnapshotFileName("PC / ufficio: 1", new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal("RdpManager-PC---ufficio--1-20260102-030405.png", name);
    }
}

public class DesktopSupportTests
{
    [Fact]
    public void Rileva_lo_schermo_touch_ma_non_il_touchpad()
    {
        Assert.True(DesktopSupport.HasTouchScreen("I: Bus=0018\nN: Name=\"ELAN Touchscreen\"\nH: Handlers=mouse2\n"));
        Assert.False(DesktopSupport.HasTouchScreen("I: Bus=0018\nN: Name=\"SynPS/2 Synaptics TouchPad\"\nB: ABS=260000\n"));
        Assert.False(DesktopSupport.HasTouchScreen(null));
    }

    [Fact]
    public void Rileva_chi_ospita_le_icone_dell_area_di_notifica()
    {
        Assert.True(DesktopSupport.HasTrayHost("org.kde.StatusNotifierWatcher 1234 plasmashell"));
        Assert.False(DesktopSupport.HasTrayHost("org.freedesktop.portal.Desktop 55 xdg"));
    }

    [Fact]
    public void Nomi_dei_tasti_per_X()
    {
        Assert.Equal("a", X11GlobalHotkey.KeysymName(Key.A));
        Assert.Equal("F5", X11GlobalHotkey.KeysymName(Key.F5));
        Assert.Equal("Right", X11GlobalHotkey.KeysymName(Key.Right));
        Assert.Equal("5", X11GlobalHotkey.KeysymName(Key.D5));
        Assert.Equal(4u | 8u | 1u, X11GlobalHotkey.ModifierMask(new KeyCombo(true, true, true, Key.R)));
        Assert.False(new X11GlobalHotkey(a => a(), () => { }).TryStart("Ctrl+Alt+Maiusc+R", DisplayServerKind.Wayland));
    }
}

public class BlockBViewModelTests
{
    private static (MainWindowViewModel Vm, NullShellUi Ui, FakeDialogs Dialogs, TempData Data) Make(params RdpConnection[] connections)
    {
        var data = new TempData();
        Seed.Write(data.Store, connections);
        var dialogs = new FakeDialogs();
        var vm = Seed.Main(data, dialogs);
        var ui = new NullShellUi();
        vm.Ui = ui;
        vm.VpnProfilePath = Path.Combine(Path.GetTempPath(), "rdpm-vpn-" + Guid.NewGuid().ToString("N") + ".json");
        return (vm, ui, dialogs, data);
    }

    private static RdpConnection[] Three() => [Seed.Conn("Uno", "a.example"), Seed.Conn("Due", "b.example"), Seed.Conn("Tre", "c.example")];

    [Fact]
    public void Scheda_successiva_e_precedente_girano_in_tondo()
    {
        var (vm, _, _, data) = Make(Three());
        using var _ = data;
        foreach (var c in vm.Document.Connections)
            vm.Connect(c);
        Assert.Equal(3, vm.Tabs.Count);
        vm.SelectedTab = vm.Tabs[2];
        vm.NextTabCommand.Execute(null);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        vm.PreviousTabCommand.Execute(null);
        Assert.Same(vm.Tabs[2], vm.SelectedTab);
    }

    [Fact]
    public void Le_scorciatoie_di_Windows_cambiano_scheda_e_schermo_intero()
    {
        var (vm, _, _, data) = Make(Three());
        using var _ = data;
        foreach (var c in vm.Document.Connections)
            vm.Connect(c);
        Assert.True(vm.TryRunShortcut(KeyCombo.From(Key.D2, KeyModifiers.Control | KeyModifiers.Alt)));
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
        bool? fullScreen = null;
        vm.FullScreenChanged += (_, on) => fullScreen = on;
        Assert.True(vm.TryRunShortcut(KeyCombo.From(Key.Enter, KeyModifiers.Control | KeyModifiers.Alt)));
        Assert.True(vm.IsFullScreen);
        Assert.True(fullScreen);
        Assert.False(vm.ChromeVisible);
        Assert.False(vm.TryRunShortcut(KeyCombo.From(Key.Q, KeyModifiers.Control | KeyModifiers.Alt)));
    }

    [Fact]
    public void Vista_affiancata_serve_di_due_sessioni_e_si_toglie_chiudendo_una()
    {
        var (vm, _, _, data) = Make(Three());
        using var _ = data;
        vm.Connect(vm.Document.Connections[0]);
        vm.SplitViewCommand.Execute(null);
        Assert.False(vm.IsSplit);
        Assert.Contains("due sessioni", vm.StatusMessage);
        vm.Connect(vm.Document.Connections[1]);
        vm.SelectedTab = vm.Tabs[0];
        vm.SplitViewCommand.Execute(null);
        Assert.True(vm.IsSplit);
        Assert.Same(vm.Tabs[0], vm.SplitLeft);
        Assert.Same(vm.Tabs[1], vm.SplitRight);
        vm.Tabs[1].CloseCommand.Execute(null);
        Assert.False(vm.IsSplit);
    }

    [Fact]
    public void Barra_laterale_fissa_o_a_comparsa()
    {
        var (vm, _, _, data) = Make();
        using var _ = data;
        vm.StartBlockB(vm.Ui, background: false);
        Assert.True(vm.SidebarShown);
        vm.ToggleSidebarCommand.Execute(null);
        Assert.False(vm.SidebarShown);
        vm.PinSidebarCommand.Execute(null); // non più fissa
        Assert.Equal(SidebarMode.AutoHide, vm.Document.Settings.SidebarMode);
        Assert.False(vm.SidebarShown);
        vm.SidebarHover = true;
        Assert.True(vm.SidebarShown);
    }

    [Fact]
    public async Task Spazio_di_lavoro_si_salva_e_si_riapre()
    {
        var (vm, _, dialogs, data) = Make(Three());
        using var _ = data;
        dialogs.PromptAnswer = "Ufficio";
        vm.Connect(vm.Document.Connections[0]);
        vm.Connect(vm.Document.Connections[1]);
        await vm.SaveWorkspaceAsync();
        var ws = Assert.Single(vm.Document.Workspaces);
        Assert.Equal("Ufficio", ws.Name);
        Assert.Equal(2, ws.Connections.Count);

        vm.Tabs[0].CloseCommand.Execute(null);
        vm.Tabs[0].CloseCommand.Execute(null);
        Assert.Empty(vm.Tabs);
        vm.OpenWorkspace(ws);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Contains("Ufficio", vm.StatusMessage);
    }

    [Fact]
    public async Task Spazio_senza_sessioni_aperte_non_si_salva()
    {
        var (vm, _, dialogs, data) = Make(Three());
        using var _ = data;
        dialogs.PromptAnswer = "Vuoto";
        await vm.SaveWorkspaceAsync();
        Assert.Empty(vm.Document.Workspaces);
    }

    [Fact]
    public async Task La_scorciatoia_di_uno_spazio_lo_apre()
    {
        var (vm, _, dialogs, data) = Make(Three());
        using var _ = data;
        dialogs.PromptAnswer = "A";
        vm.Connect(vm.Document.Connections[0]);
        await vm.SaveWorkspaceAsync();
        vm.Document.Workspaces[0].Shortcut = "Ctrl+Alt+Maiusc+7";
        vm.Tabs[0].CloseCommand.Execute(null);
        Assert.True(vm.TryRunShortcut(KeyCombo.From(Key.D7, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)));
        Assert.Single(vm.Tabs);
    }

    [Fact]
    public void Le_sessioni_aperte_si_ricordano_e_si_riaprono_solo_se_attivo()
    {
        var (vm, _, _, data) = Make(Three());
        using var _ = data;
        vm.Connect(vm.Document.Connections[0]);
        vm.Connect(vm.Document.Connections[2]);
        vm.SaveOpenSessions();
        var ids = vm.Document.Settings.OpenSessions.ToList();
        Assert.Equal(2, ids.Count);

        var again = Seed.Main(data, new FakeDialogs());
        Assert.Equal(0, again.ReopenSessions()); // riapertura spenta
        again.Document.Settings.ReopenSessionsOnStartup = true;
        Assert.Equal(2, again.ReopenSessions());
        Assert.Equal(ids, again.Tabs.Select(t => t.Session.ConnectionId).ToList());
        Assert.Equal(0, again.ReopenSessions()); // non due volte
    }

    [Fact]
    public async Task Aggiornamento_disponibile_offre_il_link_della_release()
    {
        var (vm, ui, _, data) = Make();
        using var _ = data;
        var release = new ReleaseInfo("v99.0.0", new Version(99, 0, 0), "https://github.com/niko9090/RdpManager/releases/tag/v99.0.0", "99");
        vm.UpdateCheck = _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NewerAvailable, release));
        ui.OfferAnswer = true;
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Single(ui.Offers);
        Assert.Equal([release.PageUrl], ui.OpenedUrls);
        Assert.Contains(release.PageUrl, vm.StatusMessage);
    }

    [Fact]
    public async Task Automatico_solo_avviso_non_apre_finestre_e_spento_non_controlla()
    {
        var (vm, ui, _, data) = Make();
        using var _ = data;
        int checks = 0;
        var release = new ReleaseInfo("v99.0.0", new Version(99, 0, 0), "https://example.com/r", null);
        vm.UpdateCheck = _ => { checks++; return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NewerAvailable, release)); };
        vm.Document.Settings.UpdateMode = UpdateMode.NotifyOnly;
        await vm.CheckUpdatesAsync(manual: false);
        Assert.Equal(1, checks);
        Assert.Empty(ui.Offers);
        Assert.Contains("99.0.0", vm.StatusMessage);

        vm.Document.Settings.UpdateMode = UpdateMode.Off;
        await vm.CheckUpdatesAsync(manual: false);
        Assert.Equal(1, checks);
        await vm.CheckUpdatesAsync(manual: true); // a mano si controlla sempre
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task Aggiornato_e_rete_assente_dicono_la_loro_solo_a_mano()
    {
        var (vm, _, _, data) = Make();
        using var _ = data;
        vm.UpdateCheck = _ => Task.FromResult(UpdateCheckResult.Unavailable);
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Contains("Internet", vm.StatusMessage);
        vm.UpdateCheck = _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
        await vm.CheckUpdatesAsync(manual: true);
        Assert.Contains("aggiornato", vm.StatusMessage);
    }

    [Fact]
    public async Task Incolla_come_digitazione_usa_gli_appunti()
    {
        var (vm, ui, _, data) = Make(Three());
        using var _ = data;
        var runner = new RecordingRunner();
        vm.Input = new RemoteInput(runner, DisplayServerKind.X11, n => n == "xdotool");
        ui.Clipboard = "segreto";
        var host = new FreeRdpSessionHost(isInstalled: n => n == "xfreerdp3");
        var withSession = new MainWindowViewModel(data.Store, host, new FakeDialogs()) { Ui = ui, Input = vm.Input };
        withSession.Connect(withSession.Document.Connections[0]);
        await withSession.PasteAsTypingAsync();
        Assert.Contains(runner.Calls, c => c.StartsWith("xdotool type") && c.EndsWith("segreto"));
    }

    [Fact]
    public void Il_menu_mostra_le_scorciatoie_con_F1()
    {
        var (vm, ui, _, data) = Make();
        using var _ = data;
        vm.ShortcutsHelpCommand.Execute(null);
        Assert.Contains("Ctrl+Alt+Destra", Assert.Single(ui.ShownTexts));
    }

    [Fact]
    public void Modalita_tocco_automatica_segue_lo_schermo_e_il_primo_tocco()
    {
        var (vm, _, _, data) = Make();
        using var _ = data;
        Assert.False(vm.TouchActive);
        vm.NoteTouchSeen();
        Assert.True(vm.TouchActive);
        vm.Document.Settings.TouchMode = TouchMode.Off;
        Assert.False(vm.TouchActive);
    }
}

public class AccountAndVpnTests
{
    [Fact]
    public void Gli_errori_del_server_hanno_i_testi_di_Windows()
    {
        var wrong = new SyncServerException(System.Net.HttpStatusCode.Forbidden, "invalid_registration_code", "x");
        Assert.Contains("password del server non è giusta", AccountViewModel.Explain(wrong));
        Assert.Equal("Utente o password sbagliati.",
            AccountViewModel.Explain(new SyncServerException(System.Net.HttpStatusCode.Unauthorized, "invalid_credentials", "x")));
        Assert.Contains("VPN sia accesa", AccountViewModel.Explain(new SyncUnreachableException("x")));
    }

    [Fact]
    public async Task Importa_configurazione_riempie_server_e_password_del_server()
    {
        using var data = new TempData();
        var file = Path.Combine(Path.GetTempPath(), "rdpm-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{\"server\":\"http://syncnetpro.com:8443\",\"codiceRegistrazione\":\"abc\"}");
        try
        {
            var ui = new NullShellUi { FileAnswer = file };
            using var sync = new SyncService(SyncState.PathFor(data.Store));
            var account = new AccountViewModel(sync, () => Task.FromResult(new SyncOutcome(SyncStatus.UpToDate, "")), () => null, ui, new FakeDialogs(),
                defaultConfig: new ClientConfig("", "", ""));
            await account.ImportConfigAsync();
            Assert.Equal("https://syncnetpro.com:8443/", account.ServerUrl);
            Assert.Equal("abc", account.RegistrationCode);
            Assert.Contains("Importata", account.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Configurazione_non_valida_non_viene_importata()
    {
        using var data = new TempData();
        var file = Path.Combine(Path.GetTempPath(), "rdpm-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "non json");
        try
        {
            using var sync = new SyncService(SyncState.PathFor(data.Store));
            var account = new AccountViewModel(sync, () => Task.FromResult(new SyncOutcome(SyncStatus.UpToDate, "")), () => null,
                new NullShellUi { FileAnswer = file }, new FakeDialogs(), defaultConfig: new ClientConfig("", "", ""));
            await account.ImportConfigAsync();
            Assert.Contains("non importata", account.Status);
            Assert.Equal("", account.ServerUrl);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string TempProfile() => Path.Combine(Path.GetTempPath(), "rdpm-vpn-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task Vpn_crea_le_chiavi_e_il_frammento_per_il_server()
    {
        var path = TempProfile();
        try
        {
            var vpn = new VpnViewModel(new LinuxWireGuard(new RecordingRunner(), path + ".conf", LinuxVpnBackend.WgQuick), null,
                new NullShellUi(), new FakeDialogs(), path);
            Assert.Equal("", vpn.PublicKey);
            vpn.PcName = "Portatile";
            vpn.Persist();
            await vpn.GenerateKeysAsync();
            Assert.NotEqual("", vpn.PublicKey);
            Assert.Contains(vpn.PublicKey, vpn.Fragment);
            Assert.Contains("Portatile", vpn.Fragment);
            Assert.False(vpn.CanProvision); // niente account
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Vpn_accende_con_wg_quick_via_pkexec_e_spegne()
    {
        var path = TempProfile();
        try
        {
            var up = false;
            var runner = new RecordingRunner((file, args) => (file, args[0]) switch
            {
                ("pkexec", _) when args.Contains("up") => Up(true),
                ("pkexec", _) when args.Contains("down") => Up(false),
                ("ip", _) => new ProcessResult(up ? 0 : 1, "", ""),
                _ => new ProcessResult(0, "", ""),
            });
            ProcessResult Up(bool value) { up = value; return new ProcessResult(0, "", ""); }
            var vpn = new VpnViewModel(new LinuxWireGuard(runner, path + ".conf", LinuxVpnBackend.WgQuick), null,
                new NullShellUi(), new FakeDialogs(), path);
            await vpn.GenerateKeysAsync();
            vpn.ServerPublicKey = vpn.PublicKey; // una chiave valida qualunque
            vpn.Endpoint = "vpn.example.com:51820";
            await vpn.ToggleAsync();
            Assert.Contains(runner.Calls, c => c.StartsWith("pkexec wg-quick up"));
            Assert.True(vpn.IsConnected);
            Assert.Equal("Spegni la VPN", vpn.ToggleText);
            await vpn.ToggleAsync();
            Assert.Contains(runner.Calls, c => c.StartsWith("pkexec wg-quick down"));
            Assert.False(vpn.IsConnected);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".conf");
        }
    }

    [Fact]
    public async Task Vpn_senza_chiavi_chiede_di_crearle()
    {
        var path = TempProfile();
        var vpn = new VpnViewModel(new LinuxWireGuard(new RecordingRunner((_, _) => new ProcessResult(1, "", "")), path + ".conf", LinuxVpnBackend.WgQuick),
            null, new NullShellUi(), new FakeDialogs(), path);
        await vpn.ToggleAsync();
        Assert.Equal("Prima crea le chiavi.", vpn.Message);
        File.Delete(path);
    }
}

public class BlockBSettingsPageTests
{
    [Fact]
    public void Vassoio_valida_la_scorciatoia_globale()
    {
        var settings = new AppSettings();
        var page = new VassoioPageViewModel(settings) { GlobalHotkey = "R" };
        Assert.NotEqual("", page.HotkeyError);
        page.Apply();
        Assert.Equal(HotkeyRules.DefaultText, settings.GlobalHotkey); // la non valida non sostituisce
        page.GlobalHotkey = "ctrl+alt+g";
        Assert.Equal("", page.HotkeyError);
        page.CloseToTray = true;
        page.Apply();
        Assert.Equal("Ctrl+Alt+G", settings.GlobalHotkey);
        Assert.True(settings.CloseToTray);
        page.ShowTrayIcon = false;
        Assert.False(page.CloseToTray);
        page.GlobalHotkey = "";
        page.Apply();
        Assert.Equal("", settings.GlobalHotkey);
    }

    [Fact]
    public void Scorciatoie_salva_solo_le_differenze_e_segnala_i_conflitti()
    {
        var settings = new AppSettings();
        var page = new ScorciatoiePageViewModel(settings);
        Assert.Equal("", page.Problems);
        page.Entries.First(e => e.Action == ShortcutAction.Overview).Text = "Ctrl+Alt+L"; // come Blocca
        Assert.Contains("Stessa combinazione", page.Problems);
        page.Entries.First(e => e.Action == ShortcutAction.Overview).Text = "Ctrl+Alt+P";
        page.Entries.First(e => e.Action == ShortcutAction.Lock).Text = "";
        page.Entries.First(e => e.Action == ShortcutAction.QuickSearch).Text = "pippo";
        Assert.Contains("non è una combinazione valida", page.Problems);
        page.Apply();
        Assert.Equal("Ctrl+Alt+P", settings.Shortcuts["Overview"]);
        Assert.Equal("", settings.Shortcuts["Lock"]);
        Assert.False(settings.Shortcuts.ContainsKey("QuickSearch"));
        page.ResetCommand.Execute(null);
        page.Apply();
        Assert.Empty(settings.Shortcuts);
    }

    [Fact]
    public void Tocco_salva_modalita_e_gesti()
    {
        var settings = new AppSettings();
        var page = new ToccoPageViewModel(settings);
        page.Mode = ToccoPageViewModel.ModeChoices.First(c => c.Value == TouchMode.On);
        page.SessionMode = ToccoPageViewModel.SessionChoices.First(c => c.Value == SessionTouchMode.Mouse);
        var gesture = page.Gestures.First(g => g.Gesture == TouchGesture.ThreeFingerTap);
        gesture.Action = ToccoPageViewModel.ActionChoices.First(c => c.Value is null);
        page.Apply();
        Assert.Equal(TouchMode.On, settings.TouchMode);
        Assert.Equal(SessionTouchMode.Mouse, settings.SessionTouch);
        Assert.Null(settings.GestureMap.ActionFor(TouchGesture.ThreeFingerTap));
        Assert.Equal(TouchActionKind.RightClick, settings.GestureMap.ActionFor(TouchGesture.TwoFingerTap));
    }

    [Fact]
    public void Monitor_tiene_in_elenco_il_monitor_scollegato()
    {
        var settings = new AppSettings { PreferredMonitor = "HDMI-9" };
        var screens = new[] { new ScreenInfo("DP-1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true) };
        var page = new MonitorPageViewModel(settings, screens);
        Assert.Equal(3, page.MonitorChoices.Count);
        Assert.Contains("non collegato", page.Monitor.Text);
        page.Monitor = page.MonitorChoices[1];
        page.Sidebar = MonitorPageViewModel.SidebarChoices.First(c => c.Value == SidebarMode.AutoHide);
        page.Apply();
        Assert.Equal("DP-1", settings.PreferredMonitor);
        Assert.Equal(SidebarMode.AutoHide, settings.SidebarMode);
    }

    [Fact]
    public void Le_pagine_del_blocco_B_non_sono_piu_segnaposto()
    {
        var pages = new SettingsViewModel(new AppSettings(), "x.json").Pages;
        foreach (var title in new[] { "Area di notifica", "Monitor", "Tocco", "Scorciatoie", "Aggiornamenti" })
            Assert.IsNotAssignableFrom<PlaceholderSettingsPageViewModel>(pages.First(p => p.Title == title));
    }
}
