using Microsoft.Extensions.Hosting;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>
/// Registrazione dall'app senza chiave di amministrazione, con il server vero in-process: il server non ha nemmeno
/// una chiave di amministrazione, quindi l'unica strada è <c>POST /v1/register</c>.
/// </summary>
public class RegistrationWithoutAdminKeyTests : IAsyncLifetime
{
    private const int Iterations = 1000;
    private const string Code = "codice-di-registrazione-di-prova";
    private const string Password = "frase segreta lunga";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-reg-" + Guid.NewGuid().ToString("N"));
    private readonly List<Microsoft.AspNetCore.Builder.WebApplication> _apps = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string StartServer(Action<RdpmSync.SyncOptions> configure)
    {
        var options = new RdpmSync.SyncOptions
        {
            DatabasePath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".sqlite"),
            ServerHashIterations = 1000,
            VpnEndpoint = "vpn.example.com:51820",
        };
        configure(options);
        var app = RdpmSync.SyncApp.Build(options, null, "http://127.0.0.1:0");
        app.Start();
        _apps.Add(app);
        return app.Urls.First();
    }

    private SyncService NewClient() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"), Iterations);

    [Fact]
    public async Task Con_il_codice_giusto_l_account_nasce_e_si_accede()
    {
        var url = StartServer(o => o.RegistrationCode = Code);
        using var sync = NewClient();
        await sync.RegisterAsync(url, "mario", Password, Code);
        await sync.SignInAsync(url, "mario", Password);
        Assert.True(sync.SignedIn);
    }

    [Fact]
    public async Task Con_il_codice_sbagliato_o_mancante_il_server_rifiuta()
    {
        var url = StartServer(o => o.RegistrationCode = Code);
        using var sync = NewClient();
        var wrong = await Assert.ThrowsAsync<SyncServerException>(() => sync.RegisterAsync(url, "mario", Password, "codice-sbagliato"));
        Assert.Equal("invalid_registration_code", wrong.Code);
        var missing = await Assert.ThrowsAsync<SyncServerException>(() => sync.RegisterAsync(url, "mario", Password, null));
        Assert.Equal("invalid_registration_code", missing.Code);
        await Assert.ThrowsAsync<SyncServerException>(() => sync.SignInAsync(url, "mario", Password)); // l'account non è nato
    }

    [Fact]
    public async Task Con_la_registrazione_aperta_non_serve_nessun_codice()
    {
        var url = StartServer(o => o.OpenRegistration = true);
        using var sync = NewClient();
        await sync.RegisterAsync(url, "mario", Password, null);
        await sync.SignInAsync(url, "mario", Password);
        Assert.True(sync.SignedIn);
    }

    [Fact]
    public async Task Senza_codice_e_senza_registrazione_aperta_il_server_dice_che_e_spenta()
    {
        var url = StartServer(_ => { });
        using var sync = NewClient();
        var ex = await Assert.ThrowsAsync<SyncServerException>(() => sync.RegisterAsync(url, "mario", Password, Code));
        Assert.Equal("registration_disabled", ex.Code);
    }

    [Fact]
    public async Task Il_codice_arriva_dal_file_di_configurazione()
    {
        var url = StartServer(o => o.RegistrationCode = Code);
        var cfg = ClientConfig.Parse($$"""{ "server": "{{url}}", "codiceRegistrazione": "{{Code}}" }""");
        using var sync = NewClient();
        await sync.RegisterAsync(cfg.ServerUrl, "anna", Password, cfg.RegistrationCode);
        await sync.SignInAsync(cfg.ServerUrl, "anna", Password);
        Assert.True(sync.SignedIn);
    }
}
