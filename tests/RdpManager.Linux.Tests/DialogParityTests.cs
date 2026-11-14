using RdpManager.Core;
using RdpManager.Linux.Features;
using RdpManager.Linux.ViewModels;
using RdpManager.Linux.ViewModels.Settings;

namespace RdpManager.Linux.Tests;

/// <summary>Parità con Windows (k-111): le finestre di dialogo e le Impostazioni di Linux hanno gli stessi nomi, ordine e comportamenti.</summary>
[Collection("Globali")]
public class DialogParityTests
{
    private static readonly string[] WindowsPages =
        ["Generale", "Aspetto", "Schermo intero", "Tocco e gesti", "Scorciatoie", "Sicurezza", "Aggiornamenti", "Cronologia e log"];

    [Fact]
    public void Le_pagine_hanno_titolo_e_sottotitolo_come_su_windows()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/c.json");
        Assert.Equal(WindowsPages, vm.Pages.Select(p => p.Title));
        Assert.All(vm.Pages, p => Assert.NotEmpty(p.Subtitle));
        Assert.Equal("Come si aggiorna RdpManager", vm.Pages.Single(p => p.Title == "Aggiornamenti").Subtitle);
    }

    [Fact]
    public void Ogni_modello_di_base_e_applicato_una_volta_sola()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/c.json");
        Assert.Equal(vm.Sections.Count, vm.Sections.Distinct().Count());
        // Le pagine che mostrano più modelli non scrivono da sole.
        Assert.All(vm.Pages.OfType<ComposedSettingsPageViewModel>(), p => Assert.DoesNotContain(p, vm.Sections));
    }

    [Fact]
    public void La_barra_a_schermo_intero_si_salva_con_larghezza_ritardo_stile_e_ordine_delle_sezioni()
    {
        var settings = new AppSettings();
        var page = new MonitorPageViewModel(settings, []);
        page.BarModeChoice = MonitorPageViewModel.BarModeChoices.First(c => c.Value == BarMode.AutoReveal);
        Assert.True(page.BarDelayEnabled);
        page.BarWidth = 333;
        page.BarDelay = 500;
        page.BarStyleChoice = MonitorPageViewModel.BarStyleChoices.First(c => c.Value == SessionListStyle.Thumbnails);
        var first = page.Sections[0];
        page.SelectedSection = first;
        page.MoveSection(+1);
        page.Sections[0].Visible = false;

        page.Apply();

        var bar = settings.Bar;
        Assert.Equal((BarMode.AutoReveal, 333, 500, SessionListStyle.Thumbnails), (bar.Mode, bar.Width, bar.RevealDelayMs, bar.SessionStyle));
        Assert.Equal(first.Kind, bar.Sections[1].Kind);
        Assert.False(bar.Sections[0].Visible);
    }

    [Fact]
    public void Misure_della_barra_fuori_limite_tornano_nei_limiti()
    {
        var settings = new AppSettings();
        var page = new MonitorPageViewModel(settings, []) { BarWidth = 9999, BarDelay = -5 };
        page.Apply();
        Assert.Equal((FullScreenBarSettings.MaxWidth, 0), (settings.Bar.Width, settings.Bar.RevealDelayMs));
    }

    [Fact]
    public void Barra_laterale_e_aggiornamenti_si_scelgono_con_le_opzioni_a_pallino()
    {
        var settings = new AppSettings { SidebarMode = SidebarMode.Pinned, UpdateMode = UpdateMode.AutomaticWithConfirmation };
        var display = new MonitorPageViewModel(settings, []);
        Assert.True(display.SidebarPinned);
        display.SidebarAutoHide = true;
        Assert.False(display.SidebarPinned);
        display.Apply();
        Assert.Equal(SidebarMode.AutoHide, settings.SidebarMode);

        var updates = new AggiornamentiPageViewModel(settings);
        Assert.True(updates.AutoMode);
        updates.OffMode = true;
        Assert.False(updates.AutoMode);
        updates.Apply();
        Assert.Equal(UpdateMode.Off, settings.UpdateMode);
    }

    [Fact]
    public void Ripristina_predefiniti_rimette_i_gesti_di_fabbrica()
    {
        var settings = new AppSettings();
        var page = new ToccoPageViewModel(settings);
        foreach (var g in page.Gestures)
            g.Action = ToccoPageViewModel.ActionChoices.First(c => c.Value is null);
        page.ResetGesturesCommand.Execute(null);
        page.Apply();
        Assert.Equal(TouchGestureMap.Default.ActionFor(TouchGesture.TwoFingerTap), settings.GestureMap.ActionFor(TouchGesture.TwoFingerTap));
    }

    [Fact]
    public void Salvando_con_scorciatoie_doppie_la_pagina_resta_aperta()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/c.json");
        var keys = vm.Sections.OfType<ScorciatoiePageViewModel>().Single();
        keys.Entries[1].Text = keys.Entries[0].Text;
        Assert.False(vm.Validate());
        Assert.Same(keys, vm.SelectedPage);
    }

    [Fact]
    public async Task Spazi_di_lavoro_si_rinominano_e_si_spostano_come_su_windows()
    {
        var doc = new ConnectionsDocument();
        doc.Workspaces.Add(new Workspace { Name = "B" });
        doc.Workspaces.Add(new Workspace { Name = "A" });
        var dialogs = new FakeDialogs { PromptAnswer = "Nuovo nome" };
        var vm = new WorkspacesViewModel(doc, () => true, _ => { }, dialogs);
        // Ordine salvato, non alfabetico: B, A.
        Assert.Equal(["B", "A"], vm.Items.Select(i => i.Name));
        vm.Selected = vm.Items[1];
        vm.MoveSelected(-1);
        Assert.Equal(["A", "B"], doc.Workspaces.Select(w => w.Name));
        Assert.Equal("A", vm.Selected?.Name);
        await vm.RenameSelectedAsync();
        Assert.Equal("Nuovo nome", doc.Workspaces[0].Name);
    }

    [Fact]
    public void Il_dialogo_di_modifica_ha_i_pulsanti_e_le_etichette_di_windows()
    {
        var axaml = File.ReadAllText(SettingsViewModelTests.RepoFile("src/RdpManager.Linux/Dialogs/ConnectionEditWindow.axaml"));
        foreach (var label in new[] { "Host", "Credenziali", "Credenziali gateway", "Risorse", "Riconnessione", "Elenco", "MAC", "Condividi unità locali", "Preferita ★" })
            Assert.Contains($"\"{label}\"", axaml);
        // OK a sinistra e Annulla a destra, come in Windows.
        Assert.True(axaml.IndexOf("Content=\"OK\"", StringComparison.Ordinal) < axaml.IndexOf("Content=\"Annulla\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Il_colore_dell_etichetta_si_toglie_con_nessuno()
    {
        var doc = new ConnectionsDocument();
        var vm = new ConnectionEditViewModel(doc, new RdpConnection { Name = "x", Host = "h", Color = "#112233" }, false);
        Assert.True(vm.ClearColorCommand.CanExecute(null));
        vm.ClearColorCommand.Execute(null);
        Assert.Equal("", vm.Color);
        Assert.False(vm.ClearColorCommand.CanExecute(null));
    }
}
