using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using System.Text;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Derivazione delle chiavi e cifratura del documento: gli stessi vettori del server (docs/sync-server-api.md).</summary>
public class SyncCryptoTests
{
    private const string User = "mario", Password = "correct horse battery staple";
    private const string VectorAuth = "LYH1gkWICwAF8nBgnDp+hpjco7z+BEnEW50myOk2GcA=";
    private const string VectorEnc = "8b1035aaa59eca1969dcc4ff5176b960a45eddcddb2fe37b42b4cb66269aee13";

    [Fact]
    public void Vettori_di_prova_del_server()
    {
        var (auth, key) = SyncCrypto.Derive(User, Password);
        Assert.Equal(VectorAuth, auth);
        Assert.Equal(VectorEnc, Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void Il_nome_utente_non_distingue_maiuscole_e_spazi()
    {
        var a = SyncCrypto.Derive("mario", Password, 1000);
        var b = SyncCrypto.Derive("  Mario ", Password, 1000);
        Assert.Equal(a.AuthSecret, b.AuthSecret);
        Assert.Equal(a.EncryptionKey, b.EncryptionKey);
    }

    [Fact]
    public void Come_il_codice_di_riferimento_del_server()
    {
        var (auth, key) = SyncCrypto.Derive(User, Password, 1000);
        Assert.Equal(RdpmSync.ClientKeys.AuthSecret(User, Password, 1000), auth);
        Assert.Equal(RdpmSync.ClientKeys.EncryptionKey(User, Password, 1000), key);

        var plain = Encoding.UTF8.GetBytes("documento");
        // Un blob cifrato dal cliente si apre con il codice del server e viceversa.
        Assert.Equal(plain, RdpmSync.ClientKeys.Open(key, User, SyncCrypto.Seal(key, User, plain)));
        Assert.Equal(plain, SyncCrypto.Open(key, User, RdpmSync.ClientKeys.Seal(key, User, plain)));
    }

    [Fact]
    public void Cifra_e_decifra_ma_rifiuta_dati_alterati_o_di_un_altro_account()
    {
        var (_, key) = SyncCrypto.Derive(User, Password, 1000);
        var plain = Encoding.UTF8.GetBytes("connessioni");
        var blob = SyncCrypto.Seal(key, User, plain);

        Assert.Equal(1 + 12 + plain.Length + 16, blob.Length);
        Assert.Equal(SyncCrypto.BlobVersion, blob[0]);
        Assert.Equal(plain, SyncCrypto.Open(key, User, blob));
        Assert.NotEqual(blob, SyncCrypto.Seal(key, User, plain)); // nonce nuovo a ogni salvataggio

        Assert.Null(SyncCrypto.Open(key, "luigi", blob)); // AAD: il blob non si sposta su un altro account
        Assert.Null(SyncCrypto.Open(SyncCrypto.Derive(User, "altra password", 1000).EncryptionKey, User, blob));
        var tampered = (byte[])blob.Clone();
        tampered[14] ^= 1;
        Assert.Null(SyncCrypto.Open(key, User, tampered));
        var otherVersion = (byte[])blob.Clone();
        otherVersion[0] = 2;
        Assert.Null(SyncCrypto.Open(key, User, otherVersion));
        Assert.Null(SyncCrypto.Open(key, User, new byte[10]));
    }

    [Theory]
    [InlineData("10.66.0.1:8787", "http://10.66.0.1:8787/")]
    [InlineData(" http://nas.local:8787/ ", "http://nas.local:8787/")]
    [InlineData("https://sync.example.com/base/", "https://sync.example.com/base/")]
    public void Indirizzo_del_server_normalizzato(string input, string expected) =>
        Assert.Equal(expected, SyncHttp.NormalizeUrl(input));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://")]
    public void Indirizzo_del_server_non_valido(string input) =>
        Assert.Throws<ArgumentException>(() => SyncHttp.NormalizeUrl(input));

    [Fact]
    public void Indirizzo_predefinito_VPN_e_porta_del_server()
    {
        Assert.Equal("http://10.66.0.1:8787", SyncHttp.DefaultServerUrl);
        Assert.Equal(8787, new RdpmSync.SyncOptions().Port);
    }
}

/// <summary>Unione a tre vie, senza rete.</summary>
public class SyncMergeTests
{
    private static readonly SyncFingerprints Fp = new(new byte[32]);

    private static RdpConnection Conn(string name, string host = "pc", string? password = null, string group = "") =>
        new() { Name = name, Host = host + name, Username = "u", Password = password, Group = group };

    private static ConnectionsDocument Doc(params RdpConnection[] cs) => new() { Connections = [.. cs] };

    [Fact]
    public void Primo_collegamento_unisce_senza_cancellare()
    {
        var local = Doc(Conn("a"));
        var remote = Doc(Conn("b"));
        Assert.True(SyncMerge.MergeInto(local, remote, new Dictionary<string, string>(), Fp));
        Assert.Equal(["a", "b"], local.Connections.Select(c => c.Name).Order());
    }

    [Fact]
    public void Primo_collegamento_non_duplica_la_stessa_destinazione()
    {
        var a = Conn("a");
        var same = new RdpConnection { Name = "altro nome", Host = a.Host, Username = a.Username };
        var local = Doc(a);
        SyncMerge.MergeInto(local, Doc(same), new Dictionary<string, string>(), Fp);
        Assert.Single(local.Connections);
    }

    [Fact]
    public void Cancellazione_sul_server_cancella_qui_ma_non_se_modificata()
    {
        var keep = Conn("keep");
        var gone = Conn("gone");
        var edited = Conn("edited");
        var local = Doc(keep, gone, edited);
        var baseHashes = Fp.Of(local);
        edited.Host = "nuovo";

        var remote = Doc(keep.Clone());
        Assert.True(SyncMerge.MergeInto(local, remote, baseHashes, Fp));
        Assert.Equal(["edited", "keep"], local.Connections.Select(c => c.Name).Order());
    }

    [Fact]
    public void Cancellazione_locale_non_resuscita_dal_server()
    {
        var a = Conn("a");
        var b = Conn("b");
        var baseHashes = Fp.Of(Doc(a, b));
        var local = Doc(a);
        Assert.False(SyncMerge.MergeInto(local, Doc(a.Clone(), b.Clone()), baseHashes, Fp));
        Assert.Single(local.Connections);
    }

    [Fact]
    public void Modifica_solo_sul_server_si_prende_e_mantiene_lo_stesso_oggetto()
    {
        var a = Conn("a", password: "x");
        var local = Doc(a);
        var baseHashes = Fp.Of(local);
        var remote = Doc(a.Clone());
        remote.Connections[0].Name = "rinominata";
        remote.Connections[0].Password = "y";
        var lastUsed = DateTime.UtcNow;
        a.LastConnectedUtc = lastUsed;

        Assert.True(SyncMerge.MergeInto(local, remote, baseHashes, Fp));
        Assert.Same(a, local.Connections[0]);
        Assert.Equal("rinominata", a.Name);
        Assert.Equal("y", a.Password);
        Assert.Equal(lastUsed, a.LastConnectedUtc); // l'ultimo uso vale solo per questo PC
    }

    [Fact]
    public void Se_cambia_da_entrambe_le_parti_vince_il_locale()
    {
        var a = Conn("a");
        var baseHashes = Fp.Of(Doc(a));
        var remote = Doc(a.Clone());
        remote.Connections[0].Name = "dal server";
        a.Name = "locale";
        var local = Doc(a);
        Assert.False(SyncMerge.MergeInto(local, remote, baseHashes, Fp));
        Assert.Equal("locale", local.Connections[0].Name);
    }

    [Fact]
    public void Cartelle_e_profili_viaggiano_e_si_cancellano()
    {
        var local = new ConnectionsDocument { Groups = ["Casa"] };
        var profile = new CredentialProfile { Name = "Admin", Username = "adm", Password = "pw" };
        var remote = new ConnectionsDocument { Groups = ["Ufficio"], CredentialProfiles = [profile] };

        SyncMerge.MergeInto(local, remote, new Dictionary<string, string>(), Fp);
        Assert.Contains("Ufficio", local.AllGroups());
        Assert.Equal("pw", Assert.Single(local.CredentialProfiles).Password);

        var baseHashes = Fp.Of(local);
        // L'altro PC ha tolto cartella e profilo.
        Assert.True(SyncMerge.MergeInto(local, new ConnectionsDocument { Groups = ["Casa"] }, baseHashes, Fp));
        Assert.DoesNotContain("Ufficio", local.AllGroups());
        Assert.Empty(local.CredentialProfiles);
    }

    [Fact]
    public void Le_impronte_non_cambiano_con_l_ultimo_uso_ma_con_la_password()
    {
        var a = Conn("a", password: "x");
        var before = Fp.Of(a);
        a.LastConnectedUtc = DateTime.UtcNow;
        Assert.Equal(before, Fp.Of(a));
        a.Password = "y";
        Assert.NotEqual(before, Fp.Of(a));
        Assert.NotEqual(before, new SyncFingerprints(new byte[] { 1 }).Of(Conn("a", password: "x")));
    }
}

/// <summary>Il server vero (rdpm-sync) in-process su una porta libera: due «PC» con lo stesso login.</summary>
public sealed class SyncClientServerTests : IAsyncLifetime
{
    private const int Iterations = 1000; // nelle prove il costo vero (600000) non serve
    private const string AdminKey = "chiave-amministrazione-di-prova";

    private sealed class ManualTime : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class DocAccess(ConnectionsDocument doc) : ISyncDocumentAccess
    {
        public ConnectionsDocument Snapshot() => SyncMerge.Snapshot(doc);
        public bool Apply(Func<ConnectionsDocument, bool> change) => change(doc);
    }

    private sealed class Pc : IDisposable
    {
        public ConnectionsDocument Doc { get; } = new();
        public SyncService Service { get; }
        public DocAccess Access { get; }
        public string StatePath => Path.Combine(_dir, "sync.json");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-sync-client-" + Guid.NewGuid().ToString("N"));

        public Pc()
        {
            Service = new SyncService(StatePath, Iterations);
            Access = new DocAccess(Doc);
        }

        public Task<SyncOutcome> SyncAsync() => Service.SyncAsync(Access);

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-sync-server-" + Guid.NewGuid().ToString("N"));
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private string _url = "";
    private readonly List<Pc> _pcs = [];

    public Task InitializeAsync()
    {
        var options = new RdpmSync.SyncOptions
        {
            DatabasePath = Path.Combine(_dir, "db.sqlite"),
            AdminKey = AdminKey,
            ServerHashIterations = 1000,
            MaxDocumentBytes = 1024 * 1024,
        };
        _app = RdpmSync.SyncApp.Build(options, new ManualTime(), "http://127.0.0.1:0");
        _app.Start();
        _url = _app.Urls.First();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var pc in _pcs)
            pc.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Pc NewPc()
    {
        var pc = new Pc();
        _pcs.Add(pc);
        return pc;
    }

    private async Task CreateAccountAsync(string user, string password)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_url) };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/accounts")
        {
            Content = JsonContent.Create(new { username = user, password = SyncCrypto.Derive(user, password, Iterations).AuthSecret }),
        };
        req.Headers.Add("X-Admin-Key", AdminKey);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<Pc> SignedInPcAsync(string user = "mario", string password = "frase segreta lunga")
    {
        var pc = NewPc();
        await pc.Service.SignInAsync(_url, user, password);
        return pc;
    }

    private static string[] Names(ConnectionsDocument d) => d.Connections.Select(c => c.Name).Order().ToArray();

    [Fact]
    public async Task Due_PC_con_lo_stesso_login_vedono_le_stesse_cose()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        var b = await SignedInPcAsync("Mario"); // il nome utente non distingue le maiuscole

        var profile = new CredentialProfile { Name = "Admin", Username = "adm", Domain = "DOM", Password = "pw-profilo" };
        a.Doc.CredentialProfiles.Add(profile);
        a.Doc.Connections.Add(new RdpConnection { Name = "Server", Host = "srv", Username = "u", Password = "pw-conn", Group = "Ufficio/Sale", CredentialProfileId = null });
        a.Doc.Connections.Add(new RdpConnection { Name = "Portatile", Host = "nb", CredentialProfileId = profile.Id, Group = "Casa" });
        a.Doc.AddFolder("", "Vuota");

        var first = await a.SyncAsync();
        Assert.Equal(SyncStatus.Synced, first.Status);
        Assert.True(first.Pushed);

        var second = await b.SyncAsync();
        Assert.Equal(SyncStatus.Synced, second.Status);
        Assert.True(second.LocalChanged);
        Assert.Equal(["Portatile", "Server"], Names(b.Doc));
        Assert.Equal("pw-conn", b.Doc.Connections.Single(c => c.Name == "Server").Password);
        Assert.Equal("Ufficio/Sale", b.Doc.Connections.Single(c => c.Name == "Server").Group);
        var p = Assert.Single(b.Doc.CredentialProfiles);
        Assert.Equal(("DOM", "adm", "pw-profilo"), (p.Domain, p.Username, p.Password));
        Assert.Equal(p.Id, b.Doc.Connections.Single(c => c.Name == "Portatile").CredentialProfileId);
        Assert.Contains("Vuota", b.Doc.AllGroups());

        // Senza modifiche nessuno dei due ha più niente da fare.
        Assert.Equal(SyncStatus.UpToDate, (await a.SyncAsync()).Status);
        Assert.Equal(SyncStatus.UpToDate, (await b.SyncAsync()).Status);
    }

    [Fact]
    public async Task Il_server_conserva_solo_dati_cifrati()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        a.Doc.Connections.Add(new RdpConnection { Name = "SegretissimoNome", Host = "host-riservato", Username = "u", Password = "pw-riservata" });
        await a.SyncAsync();

        // Letto direttamente dal server con il token: il blob non contiene niente in chiaro.
        using var http = new HttpClient { BaseAddress = new Uri(_url) };
        using var client = new SyncHttp(_url);
        var auth = SyncCrypto.Derive("mario", "frase segreta lunga", Iterations).AuthSecret;
        var token = await client.LoginAsync("mario", auth, "prova");
        var doc = await client.GetDocumentAsync(token, null);
        var text = Encoding.UTF8.GetString(doc.Blob!);
        Assert.DoesNotContain("SegretissimoNome", text);
        Assert.DoesNotContain("host-riservato", text);
        Assert.DoesNotContain("pw-riservata", text);
        Assert.Equal(SyncCrypto.BlobVersion, doc.Blob![0]);
    }

    [Fact]
    public async Task Login_sbagliato_rifiutato_e_niente_resta_salvato()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var pc = NewPc();
        var ex = await Assert.ThrowsAsync<SyncServerException>(() => pc.Service.SignInAsync(_url, "mario", "password sbagliata"));
        Assert.Equal("invalid_credentials", ex.Code);
        Assert.False(pc.Service.SignedIn);
        Assert.Equal(SyncStatus.NotSignedIn, (await pc.SyncAsync()).Status);

        await Assert.ThrowsAsync<SyncServerException>(() => pc.Service.SignInAsync(_url, "inesistente", "frase segreta lunga"));
    }

    [Fact]
    public async Task Password_e_token_non_stanno_in_chiaro_su_disco()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var pc = await SignedInPcAsync();
        pc.Doc.Connections.Add(new RdpConnection { Name = "A", Host = "a" });
        await pc.SyncAsync();

        var file = pc.StatePath;
        var json = File.ReadAllText(file);
        Assert.DoesNotContain("frase segreta lunga", json);
        Assert.DoesNotContain("rdpm_", json);
        Assert.Contains("mario", json);

        // Un nuovo servizio sullo stesso file riparte già collegato (all'avvio del programma).
        using var again = new SyncService(file, Iterations);
        Assert.True(again.SignedIn);
        Assert.Equal(SyncStatus.UpToDate, (await again.SyncAsync(pc.Access)).Status);
    }

    [Fact]
    public async Task Cancellazioni_e_modifiche_passano_da_un_PC_all_altro()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        var b = await SignedInPcAsync();
        a.Doc.Connections.Add(new RdpConnection { Name = "Uno", Host = "1" });
        a.Doc.Connections.Add(new RdpConnection { Name = "Due", Host = "2" });
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(["Due", "Uno"], Names(b.Doc));

        a.Doc.Connections.RemoveAll(c => c.Name == "Uno");
        b.Doc.Connections.Single(c => c.Name == "Due").Name = "Due bis";
        await a.SyncAsync();
        var r = await b.SyncAsync();
        Assert.True(r.LocalChanged);
        Assert.Equal(["Due bis"], Names(b.Doc));   // "Uno" cancellata da A
        await a.SyncAsync();
        Assert.Equal(["Due bis"], Names(a.Doc));   // la modifica di B è arrivata ad A
    }

    [Fact]
    public async Task Conflitto_di_versione_si_risolve_rileggendo_unendo_e_rimandando()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        var b = await SignedInPcAsync();
        a.Doc.Connections.Add(new RdpConnection { Name = "Base", Host = "base" });
        await a.SyncAsync();
        await b.SyncAsync();

        a.Doc.Connections.Add(new RdpConnection { Name = "DaA", Host = "a" });
        b.Doc.Connections.Add(new RdpConnection { Name = "DaB", Host = "b" });
        // A ha già letto il server e sta per scrivere: nel frattempo B salva (A prenderà un 409).
        a.Service.BeforePutForTests = async () => Assert.True((await b.SyncAsync()).Pushed);

        var outcome = await a.SyncAsync();
        Assert.Equal(SyncStatus.Synced, outcome.Status);
        Assert.True(outcome.Pushed);
        Assert.Equal(["Base", "DaA", "DaB"], Names(a.Doc));

        await b.SyncAsync();
        Assert.Equal(["Base", "DaA", "DaB"], Names(b.Doc));
    }

    [Fact]
    public async Task Server_non_raggiungibile_da_un_avviso_e_non_perde_le_modifiche()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var pc = await SignedInPcAsync();
        pc.Doc.Connections.Add(new RdpConnection { Name = "Locale", Host = "l" });

        // Stesso account, ma il server «non c'è» (VPN spenta): porta chiusa.
        pc.Service.State.ServerUrl = "http://127.0.0.1:1";
        var offline = await pc.SyncAsync();
        Assert.Equal(SyncStatus.Offline, offline.Status);
        Assert.False(offline.Ok);
        Assert.Equal(["Locale"], Names(pc.Doc));

        // Tornato il server, la modifica locale parte.
        pc.Service.State.ServerUrl = _url;
        Assert.Equal(SyncStatus.Synced, (await pc.SyncAsync()).Status);
        var other = await SignedInPcAsync();
        await other.SyncAsync();
        Assert.Equal(["Locale"], Names(other.Doc));
    }

    [Fact]
    public async Task Documento_con_un_altra_password_non_viene_toccato()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        a.Doc.Connections.Add(new RdpConnection { Name = "A", Host = "a" });
        await a.SyncAsync();

        // Un blob scritto con un'altra chiave (stesso account sul server, ma cifrato diversamente).
        using var client = new SyncHttp(_url);
        var token = await client.LoginAsync("mario", SyncCrypto.Derive("mario", "frase segreta lunga", Iterations).AuthSecret, "prova");
        var current = await client.GetDocumentAsync(token, null);
        var foreign = SyncCrypto.Seal(new byte[32], "mario", Encoding.UTF8.GetBytes("{}"));
        Assert.False((await client.PutDocumentAsync(token, foreign, current.Etag)).Conflict);

        a.Doc.Connections.Add(new RdpConnection { Name = "B", Host = "b" });
        var outcome = await a.SyncAsync();
        Assert.Equal(SyncStatus.Unreadable, outcome.Status);
        Assert.Equal(["A", "B"], Names(a.Doc));
        var after = await client.GetDocumentAsync(token, null);
        Assert.Equal(foreign, after.Blob); // non sovrascritto
    }

    [Fact]
    public async Task Esci_dall_account_revoca_il_token_e_dimentica_tutto()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var pc = await SignedInPcAsync();
        pc.Doc.Connections.Add(new RdpConnection { Name = "A", Host = "a" });
        await pc.SyncAsync();
        Assert.True(pc.Service.SignedIn);

        await pc.Service.SignOutAsync();
        Assert.False(pc.Service.SignedIn);
        Assert.Null(pc.Service.State.ProtectedPassword);
        Assert.Null(pc.Service.State.ProtectedToken);
        Assert.Null(pc.Service.State.Base);
        Assert.Equal(SyncStatus.NotSignedIn, (await pc.SyncAsync()).Status);
        Assert.Equal(["A"], Names(pc.Doc)); // i dati locali restano
    }

    [Fact]
    public async Task Crea_account_dal_client_con_la_chiave_di_amministrazione()
    {
        var pc = NewPc();
        var wrong = await Assert.ThrowsAsync<SyncServerException>(() => pc.Service.CreateAccountAsync(_url, "chiave sbagliata", "nuovo", "una password"));
        Assert.Equal("unauthorized", wrong.Code);

        await pc.Service.CreateAccountAsync(_url, AdminKey, "nuovo", "una password");
        await pc.Service.SignInAsync(_url, "nuovo", "una password");
        Assert.True(pc.Service.SignedIn);
        var again = await Assert.ThrowsAsync<SyncServerException>(() => pc.Service.CreateAccountAsync(_url, AdminKey, "nuovo", "una password"));
        Assert.Equal("account_exists", again.Code);
    }

    [Fact]
    public async Task Token_revocato_si_rifa_il_login_da_solo()
    {
        await CreateAccountAsync("mario", "frase segreta lunga");
        var a = await SignedInPcAsync();
        a.Doc.Connections.Add(new RdpConnection { Name = "A", Host = "a" });
        await a.SyncAsync();

        // Revoco tutti i token dell'account da un altro accesso.
        using var client = new SyncHttp(_url);
        var token = await client.LoginAsync("mario", SyncCrypto.Derive("mario", "frase segreta lunga", Iterations).AuthSecret, "altro");
        using var http = new HttpClient { BaseAddress = new Uri(_url) };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/logout-all");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();

        a.Doc.Connections.Add(new RdpConnection { Name = "B", Host = "b" });
        Assert.Equal(SyncStatus.Synced, (await a.SyncAsync()).Status);
    }
}
