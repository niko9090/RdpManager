using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RdpmSync;

namespace RdpmSync.Tests;

/// <summary>WireGuard finto: tiene l'elenco dei peer che il server ha chiesto di applicare (nessun comando vero).</summary>
public sealed class FakeWireGuard : IWireGuardPeers
{
    public List<WgPeer> Peers { get; private set; } = [];
    public int Applications { get; private set; }
    public bool Fail { get; set; }
    /// <summary>Simula wg0.conf / socket illeggibili per permessi (UnauthorizedAccessException).</summary>
    public bool Denied { get; set; }
    public string[] Reserved { get; set; } = [];

    public IReadOnlyCollection<string> ReservedAddresses() =>
        Denied ? throw new UnauthorizedAccessException("permesso negato") : Reserved;

    public Task ApplyAsync(IReadOnlyCollection<WgPeer> desired, CancellationToken ct = default)
    {
        if (Denied)
            throw new UnauthorizedAccessException("permesso negato");
        if (Fail)
            throw new IOException("wg non risponde");
        Peers = desired.ToList();
        Applications++;
        return Task.CompletedTask;
    }
}

public class VpnTests
{
    private const string ServerKey = "c2VydmVyLXB1YmxpYy1rZXktMzItYnl0ZXMtLi4uLi4=";

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static TestServer Server(FakeWireGuard wg, Action<SyncOptions>? configure = null) =>
        new(o =>
        {
            o.VpnServerPublicKey = ServerKey;
            o.VpnEndpoint = "vpn.example.com:51820";
            configure?.Invoke(o);
        }, wireGuard: wg);

    private static async Task<string> UserAsync(TestServer s, string user)
    {
        await s.CreateAccountAsync(user, "frase segreta lunga");
        return await s.LoginAsync(user, "frase segreta lunga");
    }

    private static Task<HttpResponseMessage> Request(TestServer s, string token, string name, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/vpn/devices") { Content = JsonContent.Create(new { name, publicKey = key }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return s.Http.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Primo_pc_prende_il_primo_ip_libero_e_i_dati_del_profilo()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg, o => o.VpnDns = "10.66.0.1");
        var token = await UserAsync(s, "mario");
        var key = NewKey();

        var r = await Request(s, token, "Ufficio", key);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("10.66.0.2/32", j.GetProperty("address").GetString());
        Assert.Equal(ServerKey, j.GetProperty("serverPublicKey").GetString());
        Assert.Equal("vpn.example.com:51820", j.GetProperty("endpoint").GetString());
        Assert.Equal("10.66.0.0/24", j.GetProperty("allowedIPs").GetString());
        Assert.Equal("10.66.0.1", j.GetProperty("dns").GetString());
        Assert.Equal(25, j.GetProperty("persistentKeepalive").GetInt32());

        var peer = Assert.Single(wg.Peers);
        Assert.Equal(key, peer.PublicKey);
        Assert.Equal("10.66.0.2/32", peer.Address);
    }

    [Fact]
    public async Task Gli_indirizzi_sono_progressivi_e_saltano_quelli_riservati()
    {
        var wg = new FakeWireGuard { Reserved = ["10.66.0.3"] };
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        var a = await Json(await Request(s, token, "A", NewKey()));
        var b = await Json(await Request(s, token, "B", NewKey()));
        var c = await Json(await Request(s, token, "C", NewKey()));
        Assert.Equal("10.66.0.2/32", a.GetProperty("address").GetString());
        Assert.Equal("10.66.0.4/32", b.GetProperty("address").GetString()); // .3 e' di un peer messo a mano
        Assert.Equal("10.66.0.5/32", c.GetProperty("address").GetString());
        Assert.Equal(3, wg.Peers.Count);
    }

    [Fact]
    public async Task Stesso_pc_rifatto_e_lo_stesso_peer()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        var key = NewKey();
        var first = await Json(await Request(s, token, "Ufficio", key));

        var again = await Request(s, token, "Ufficio", key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.GetProperty("address").GetString(), (await Json(again)).GetProperty("address").GetString());
        Assert.Single(wg.Peers);
    }

    [Fact]
    public async Task Stesso_nome_con_chiave_nuova_tiene_l_indirizzo_e_cambia_la_chiave()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        var oldKey = NewKey();
        var newKey = NewKey();
        var first = await Json(await Request(s, token, "Ufficio", oldKey));
        var second = await Json(await Request(s, token, "Ufficio", newKey));

        Assert.Equal(first.GetProperty("address").GetString(), second.GetProperty("address").GetString());
        var peer = Assert.Single(wg.Peers);
        Assert.Equal(newKey, peer.PublicKey);
    }

    [Fact]
    public async Task La_rimozione_toglie_il_peer_e_libera_l_indirizzo()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        await Request(s, token, "A", NewKey());
        await Request(s, token, "B", NewKey());

        var del = await s.SendAsync(token, HttpMethod.Delete, "/v1/vpn/devices/A");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var left = Assert.Single(wg.Peers);
        Assert.Equal("10.66.0.3/32", left.Address);

        var again = await s.SendAsync(token, HttpMethod.Delete, "/v1/vpn/devices/A");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var c = await Json(await Request(s, token, "C", NewKey()));
        Assert.Equal("10.66.0.2/32", c.GetProperty("address").GetString()); // l'indirizzo liberato si riusa
    }

    [Fact]
    public async Task Un_utente_non_vede_ne_toglie_i_dispositivi_di_un_altro()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var mario = await UserAsync(s, "mario");
        var luisa = await UserAsync(s, "luisa");
        var marioKey = NewKey();
        await Request(s, mario, "Casa", marioKey);

        var list = await Json(await s.SendAsync(luisa, HttpMethod.Get, "/v1/vpn/devices"));
        Assert.Equal(0, list.GetArrayLength());

        var del = await s.SendAsync(luisa, HttpMethod.Delete, "/v1/vpn/devices/Casa");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
        Assert.Single(wg.Peers);

        // la chiave di Mario non si può registrare anche da Luisa
        var steal = await Request(s, luisa, "Mia", marioKey);
        Assert.Equal(HttpStatusCode.Conflict, steal.StatusCode);
        Assert.Equal("key_in_use", (await Json(steal)).GetProperty("error").GetString());

        // lo stesso nome in due account sono due PC diversi
        var luisaPc = await Json(await Request(s, luisa, "Casa", NewKey()));
        Assert.Equal("10.66.0.3/32", luisaPc.GetProperty("address").GetString());
        var marioList = await Json(await s.SendAsync(mario, HttpMethod.Get, "/v1/vpn/devices"));
        Assert.Equal(1, marioList.GetArrayLength());
    }

    [Fact]
    public async Task Senza_token_o_con_token_falso_401()
    {
        await using var s = Server(new FakeWireGuard());
        var r = await s.Http.PostAsJsonAsync("/v1/vpn/devices", new { name = "A", publicKey = NewKey() });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Request(s, "rdpm_falso", "A", NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Http.GetAsync("/v1/vpn/devices")).StatusCode);
    }

    [Theory]
    [InlineData("A", "non-e-una-chiave")]
    [InlineData("A", "")]
    [InlineData("", "chiave")]
    [InlineData("nome/strano", "chiave")]
    public async Task Dati_non_validi_400(string name, string key)
    {
        await using var s = Server(new FakeWireGuard());
        var token = await UserAsync(s, "mario");
        var r = await Request(s, token, name, key == "chiave" ? NewKey() : key);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Rete_piena_e_limite_di_dispositivi()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg, o => o.MaxDevicesPerAccount = 2);
        var token = await UserAsync(s, "mario");
        await Request(s, token, "A", NewKey());
        await Request(s, token, "B", NewKey());
        var third = await Request(s, token, "C", NewKey());
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("too_many_devices", (await Json(third)).GetProperty("error").GetString());

        Assert.Null(VpnEndpoints.NextFree(Enumerable.Range(2, 253).Select(n => $"10.66.0.{n}")));
        Assert.Equal("10.66.0.254", VpnEndpoints.NextFree(Enumerable.Range(2, 252).Select(n => $"10.66.0.{n}")));
    }

    [Fact]
    public async Task Se_wireguard_non_risponde_il_peer_non_resta_nel_database()
    {
        var wg = new FakeWireGuard { Fail = true };
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        var r = await Request(s, token, "A", NewKey());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);

        wg.Fail = false;
        var ok = await Request(s, token, "A", NewKey());
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("10.66.0.2/32", (await Json(ok)).GetProperty("address").GetString());
    }

    [Fact]
    public async Task Se_wg0_conf_non_e_leggibile_per_permessi_503_e_nessun_peer_resta()
    {
        var wg = new FakeWireGuard { Denied = true };
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        var r = await Request(s, token, "A", NewKey());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);

        wg.Denied = false;
        var ok = await Request(s, token, "A", NewKey());
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Account_cancellabile_anche_se_la_vpn_nega_i_permessi()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        await Request(s, token, "A", NewKey());

        wg.Denied = true;
        var req = new HttpRequestMessage(HttpMethod.Delete, "/v1/admin/accounts/mario");
        req.Headers.Add("X-Admin-Key", TestServer.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Eliminando_l_account_spariscono_i_suoi_peer()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        var token = await UserAsync(s, "mario");
        await Request(s, token, "A", NewKey());
        Assert.Single(wg.Peers);

        var req = new HttpRequestMessage(HttpMethod.Delete, "/v1/admin/accounts/mario");
        req.Headers.Add("X-Admin-Key", TestServer.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.SendAsync(req)).StatusCode);
        Assert.Empty(wg.Peers);
    }

    [Fact]
    public async Task All_avvio_il_tunnel_si_riallinea_al_database()
    {
        var wg = new FakeWireGuard();
        await using var s = Server(wg);
        // l'avvio chiama ReconcileAsync in background: basta aspettarlo
        for (int i = 0; i < 100 && wg.Applications == 0; i++)
            await Task.Delay(20);
        Assert.True(wg.Applications >= 1);
    }

    [Fact]
    public async Task Senza_configurazione_vpn_503()
    {
        await using var s = new TestServer();
        var token = await UserAsync(s, "mario");
        var r = await Request(s, token, "A", NewKey());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("vpn_disabled", (await Json(r)).GetProperty("error").GetString());
    }
}

public class WgConfFileTests
{
    private const string Base = """
        [Interface]
        Address = 10.66.0.1/24
        ListenPort = 51820
        PrivateKey = SEGRETA

        # PC: manuale
        [Peer]
        PublicKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
        AllowedIPs = 10.66.0.7/32
        """;

    private static readonly WgPeer P1 = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=", "10.66.0.2/32", "1-Ufficio");
    private static readonly WgPeer P2 = new("ccccccccccccccccccccccccccccccccccccccccccc=", "10.66.0.3/32", "1-Casa");

    [Fact]
    public void Il_blocco_gestito_si_riscrive_senza_toccare_il_resto()
    {
        var one = WgConfFile.Rewrite(Base, [P1, P2]);
        Assert.Contains("PrivateKey = SEGRETA", one);
        Assert.Contains("# PC: 1-Ufficio", one);
        Assert.Equal([P1.PublicKey, P2.PublicKey], WgConfFile.ManagedKeys(one));

        var two = WgConfFile.Rewrite(one, [P2]);
        Assert.Equal([P2.PublicKey], WgConfFile.ManagedKeys(two));
        Assert.DoesNotContain("1-Ufficio", two);
        Assert.Contains("# PC: manuale", two);
        Assert.Equal(1, two.Split(WgConfFile.BlockBegin).Length - 1);

        Assert.Equal(two, WgConfFile.Rewrite(two, [P2])); // idempotente
    }

    [Fact]
    public void I_peer_a_mano_restano_riservati_e_quelli_gestiti_no()
    {
        var conf = WgConfFile.Rewrite(Base, [P1]);
        Assert.Equal(["10.66.0.7"], WgConfFile.UnmanagedAddresses(conf));
    }

    [Fact]
    public async Task WgCliPeers_scrive_il_file_e_chiama_wg_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-wg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var conf = Path.Combine(dir, "wg0.conf");
            File.WriteAllText(conf, Base);
            var log = Path.Combine(dir, "log.txt");
            // «wg» finto: uno script che annota gli argomenti (cmd su Windows, sh altrove)
            string wg;
            if (OperatingSystem.IsWindows())
            {
                wg = Path.Combine(dir, "wg.cmd");
                File.WriteAllText(wg, $"@echo off\r\necho %* >> \"{log}\"\r\n");
            }
            else
            {
                wg = Path.Combine(dir, "wg.sh");
                File.WriteAllText(wg, $"#!/bin/sh\necho \"$@\" >> \"{log}\"\n");
                File.SetUnixFileMode(wg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var peers = new WgCliPeers(conf, "wg0", wg);
            await peers.ApplyAsync([P1, P2]);
            await peers.ApplyAsync([P2]);

            var calls = File.ReadAllLines(log).Select(l => l.Trim()).ToList();
            Assert.Contains($"set wg0 peer {P1.PublicKey} allowed-ips 10.66.0.2/32", calls);
            Assert.Contains($"set wg0 peer {P1.PublicKey} remove", calls);
            Assert.Equal([P2.PublicKey], WgConfFile.ManagedKeys(File.ReadAllText(conf)));
            Assert.Equal(["10.66.0.7"], peers.ReservedAddresses());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class RegistrationTests
{
    private static Task<HttpResponseMessage> Register(TestServer s, string user, string password = "frase segreta lunga", string? code = null) =>
        s.Http.PostAsJsonAsync("/v1/register", new { username = user, password = TestServer.Secret(user, password), registrationCode = code });

    [Fact]
    public async Task Spenta_di_default_403()
    {
        await using var s = new TestServer();
        var r = await Register(s, "mario");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("registration_disabled", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Aperta_crea_l_account_e_poi_si_accede()
    {
        await using var s = new TestServer(o => o.OpenRegistration = true);
        Assert.Equal(HttpStatusCode.Created, (await Register(s, "mario")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Register(s, "mario")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.LoginRawAsync("mario", "frase segreta lunga")).StatusCode);
    }

    [Fact]
    public async Task Con_codice_serve_il_codice_giusto()
    {
        await using var s = new TestServer(o => o.RegistrationCode = "codice-di-casa");
        Assert.Equal(HttpStatusCode.Forbidden, (await Register(s, "mario")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Register(s, "mario", code: "sbagliato")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(s, "mario", code: "codice-di-casa")).StatusCode);
    }

    [Fact]
    public async Task Troppi_tentativi_dallo_stesso_indirizzo_429()
    {
        await using var s = new TestServer(o => o.OpenRegistration = true);
        for (int i = 0; i < 10; i++)
            await Register(s, "utente" + i);
        Assert.Equal((HttpStatusCode)429, (await Register(s, "utente-ancora")).StatusCode);
    }
}
