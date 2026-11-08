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
    [Fact]
    public void Apply_scrive_le_scelte_sulle_impostazioni()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, "/x/connections.json")
        {
            Theme = Choices.Pick(Choices.Theme, AppTheme.Dark),
            Order = Choices.Pick(Choices.Order, ConnectionOrder.Manual),
            Update = Choices.Pick(Choices.Update, UpdateMode.Off),
            HistoryEnabled = false,
            VerboseLog = true,
        };

        vm.Apply();

        Assert.Equal((AppTheme.Dark, ConnectionOrder.Manual, UpdateMode.Off, false, true),
            (settings.Theme, settings.ConnectionOrder, settings.UpdateMode, settings.HistoryEnabled, settings.VerboseLog));
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
