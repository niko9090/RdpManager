using RdpManager.Linux.ViewModels.Settings;
using System.Security.Cryptography;
using RdpManager.Core;
using RdpManager.Linux.Platform;
using RdpManager.Linux.ViewModels;

namespace RdpManager.Linux.Tests;

public class ConnectionEditViewModelTests
{
    [Theory]
    [InlineData("", "3389", "computer")]
    [InlineData("pc", "0", "porta")]
    [InlineData("pc", "70000", "porta")]
    [InlineData("pc", "abc", "porta")]
    public void Valori_non_validi_vengono_rifiutati_senza_toccare_la_connessione(string host, string port, string expected)
    {
        var doc = new ConnectionsDocument();
        var c = new RdpConnection { Host = "originale" };
        var vm = new ConnectionEditViewModel(doc, c, isNew: true) { Host = host, Port = port };

        Assert.False(vm.TryCommit());
        Assert.Contains(expected, vm.Error);
        Assert.Equal("originale", c.Host);
        Assert.Empty(doc.Connections);
    }

    [Fact]
    public void Mac_e_gateway_vengono_controllati()
    {
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), new RdpConnection { Host = "pc" }, true) { MacAddress = "xx" };
        Assert.False(vm.TryCommit());
        vm.MacAddress = "AA:BB:CC:DD:EE:FF";
        vm.UseGateway = true;
        Assert.False(vm.TryCommit());
        vm.GatewayHost = "gw.example.com";
        Assert.True(vm.TryCommit());
        Assert.Null(vm.Error);
    }

    [Fact]
    public void Risoluzione_fissa_e_profilo_si_applicano()
    {
        var doc = new ConnectionsDocument();
        var profile = new CredentialProfile { Name = "P", Username = "u" };
        doc.CredentialProfiles.Add(profile);
        var c = new RdpConnection { Host = "pc" };
        var vm = new ConnectionEditViewModel(doc, c, true)
        {
            Resolution = Choices.Pick(Choices.Resolution, ResolutionMode.Fixed),
            DesktopWidth = "1280",
            DesktopHeight = "720",
            Audio = Choices.Pick(Choices.Audio, AudioMode.None),
            Profile = vm0(doc, profile.Id),
        };

        Assert.True(vm.FixedResolution);
        Assert.True(vm.TryCommit());
        Assert.Equal((ResolutionMode.Fixed, 1280, 720, AudioMode.None, profile.Id),
            (c.ResolutionMode, c.DesktopWidth, c.DesktopHeight, c.AudioMode, c.CredentialProfileId));
        Assert.Same(c, doc.Connections.Single());

        static ChoiceItem<Guid?> vm0(ConnectionsDocument d, Guid id) =>
            new ConnectionEditViewModel(d, new RdpConnection(), true).Profiles.First(p => p.Value == id);
    }

    [Fact]
    public void Password_vuota_non_cancella_quella_esistente()
    {
        var c = new RdpConnection { Host = "pc", Password = "vecchia" };
        var vm = new ConnectionEditViewModel(new ConnectionsDocument(), c, false);
        Assert.True(vm.TryCommit());
        Assert.Equal("vecchia", c.Password);

        vm.Password = "nuova";
        Assert.True(vm.TryCommit());
        Assert.Equal("nuova", c.Password);
    }
}

public class ProfilesViewModelTests
{
    [Fact]
    public void Crea_modifica_ed_elimina_profili_salvando_nel_file_dei_dati()
    {
        using var data = new TempData();
        var doc = new ConnectionsDocument();
        var dialogs = new FakeDialogs();
        var store = data.Store;
        var vm = new ProfilesViewModel(doc, () => { store.Save(doc); return true; }, dialogs);

        Assert.True(vm.IsNew);
        vm.Name = "Admin";
        vm.Username = "root";
        vm.Domain = "LAN";
        vm.Password = "pw";
        Assert.True(vm.Commit());

        var saved = store.Load().CredentialProfiles.Single();
        Assert.Equal(("Admin", "root", "LAN", "pw"), (saved.Name, saved.Username, saved.Domain, saved.Password));
        Assert.False(vm.IsNew);

        vm.Name = "Admin 2";
        vm.Password = "";
        Assert.True(vm.Commit());
        saved = store.Load().CredentialProfiles.Single();
        Assert.Equal(("Admin 2", "pw"), (saved.Name, saved.Password));

        doc.Connections.Add(new RdpConnection { Host = "pc", CredentialProfileId = saved.Id });
        vm.DeleteAsync().Wait();
        Assert.Contains("usato da 1 connessioni", dialogs.ConfirmMessages.Single());
        Assert.Empty(store.Load().CredentialProfiles);
        Assert.Null(doc.Connections.Single().CredentialProfileId);
    }

    [Fact]
    public void Profilo_vuoto_viene_rifiutato()
    {
        var vm = new ProfilesViewModel(new ConnectionsDocument(), () => true, new FakeDialogs());
        Assert.False(vm.Commit());
        Assert.NotNull(vm.Error);
    }
}

public class SettingsViewModelTests
{
    private static T Page<T>(SettingsViewModel vm) where T : SettingsPageViewModel => vm.Pages.OfType<T>().Single();

    [Fact]
    public void Apply_scrive_le_scelte_di_ogni_pagina_sulle_impostazioni()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, "/x/connections.json");
        Page<AspettoPageViewModel>(vm).Theme = Choices.Pick(Choices.Theme, AppTheme.Dark);
        Page<ElencoPageViewModel>(vm).ManualOrder = true;
        Page<AggiornamentiPageViewModel>(vm).Update = Choices.Pick(Choices.Update, UpdateMode.Off);
        Page<AvvioPageViewModel>(vm).HistoryEnabled = false;
        Page<AvvioPageViewModel>(vm).VerboseLog = true;

        vm.Apply();

        Assert.Equal((AppTheme.Dark, ConnectionOrder.Manual, UpdateMode.Off, false, true),
            (settings.Theme, settings.ConnectionOrder, settings.UpdateMode, settings.HistoryEnabled, settings.VerboseLog));
    }

    [Fact]
    public void Le_pagine_sono_quelle_del_documento_di_parita()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/connections.json");
        Assert.Equal(["Aspetto", "Avvio", "Elenco", "PIN", "Area di notifica", "Monitor", "Tocco", "Scorciatoie", "Aggiornamenti"],
            vm.Pages.Select(p => p.Title));
        Assert.Same(vm.Pages[0], vm.SelectedPage);
    }

    [Fact]
    public void Ogni_pagina_ha_la_sua_vista()
    {
        var vm = new SettingsViewModel(new AppSettings(), "/x/connections.json");
        foreach (var page in vm.Pages)
            Assert.NotNull(RdpManager.Linux.Dialogs.Settings.SettingsPageTemplate.ViewTypeFor(page.GetType()));
    }

    [Fact]
    public void La_casella_della_cronologia_ha_l_etichetta_giusta_e_la_pagina_non_tocca_altro()
    {
        var settings = new AppSettings { HistoryEnabled = true };
        var page = Page<AvvioPageViewModel>(new SettingsViewModel(settings, "/x/c.json"));
        Assert.True(page.HistoryEnabled);
        var axaml = File.ReadAllText(RepoFile("src/RdpManager.Linux/Dialogs/Settings/PaginaAvvio.axaml"));
        Assert.Contains("Registra la cronologia delle connessioni", axaml);
        Assert.DoesNotContain("Ricorda le ultime connessioni", axaml);
    }

    internal static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}

public class FileKeySecretStoreTests
{
    [Fact]
    public void Cifra_e_decifra_e_rifiuta_entropia_diversa()
    {
        var key = Path.Combine(Path.GetTempPath(), "rdpm-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileKeySecretStore(key);
            byte[] data = [1, 2, 3, 4];
            var cipher = store.Protect(data, [9]);

            Assert.Equal(data, new FileKeySecretStore(key).Unprotect(cipher, [9]));
            Assert.ThrowsAny<CryptographicException>(() => store.Unprotect(cipher, [8]));
            Assert.ThrowsAny<CryptographicException>(() => store.Unprotect([1, 2], null));
        }
        finally
        {
            File.Delete(key);
        }
    }
}
