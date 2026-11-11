using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>Formato .rdpmconfig: lettura, validazione con messaggi in italiano, .conf WireGuard, argomenti di avvio.</summary>
public class RdpmConfigFileTests
{
    internal static readonly string ServerKey = WireGuardKeys.Generate().PublicKey;

    internal static string Sample(string url = "https://vpn.example.com:8443", bool auto = true, string extra = "") => $$"""
        {
          "_commento": "prova",
          "formato": "rdpmconfig",
          "versione": 1,
          "server": { "url": "{{url}}", "urlInterno": "http://10.66.0.1:8787", "codiceRegistrazione": "codice-di-prova" },
          {{extra}}
          "vpn": {
            "endpoint": "vpn.example.com:51820",
            "chiavePubblicaServer": "{{ServerKey}}",
            "rete": "10.66.0.0/24",
            "dns": "10.66.0.1",
            "keepalive": 30,
            "richiediChiaveAlServer": {{(auto ? "true" : "false")}}
          }
        }
        """;

    [Fact]
    public void Un_file_completo_si_legge_in_tutti_i_campi()
    {
        var f = RdpmConfigFile.Parse(Sample());
        Assert.Equal("https://vpn.example.com:8443/", f.ServerUrl);
        Assert.Equal("http://10.66.0.1:8787/", f.ServerUrlInside);
        Assert.Equal("codice-di-prova", f.RegistrationCode);
        Assert.NotNull(f.Vpn);
        Assert.Equal("vpn.example.com:51820", f.Vpn!.Endpoint);
        Assert.Equal(ServerKey, f.Vpn.ServerPublicKey);
        Assert.Equal("10.66.0.0/24", f.Vpn.EffectiveAllowedIPs);
        Assert.Equal("10.66.0.1", f.Vpn.Dns);
        Assert.Equal(30, f.Vpn.Keepalive);
        Assert.True(f.Vpn.RequestKeyFromServer);
        Assert.True(f.HasSecrets);
    }

    [Fact]
    public void Con_un_BOM_e_i_commenti_si_legge_lo_stesso()
    {
        var f = RdpmConfigFile.Parse("﻿" + Sample().Replace("\"versione\": 1,", "// commento\n\"versione\": 1,"));
        Assert.Equal("codice-di-prova", f.RegistrationCode);
    }

    [Fact]
    public void Rete_e_keepalive_hanno_i_valori_predefiniti()
    {
        var f = RdpmConfigFile.Parse($$"""{ "vpn": { "endpoint": "1.2.3.4:51820", "chiavePubblicaServer": "{{ServerKey}}" } }""");
        Assert.Equal(VpnDefaults.Network, f.Vpn!.EffectiveAllowedIPs);
        Assert.Equal(VpnDefaults.Keepalive, f.Vpn.Keepalive);
        Assert.False(f.Vpn.RequestKeyFromServer);
    }

    [Fact]
    public void Il_file_scritto_da_ToJson_si_rilegge_uguale()
    {
        var original = RdpmConfigFile.Parse(Sample(extra: "\"account\": { \"utente\": \"mario\", \"password\": \"frase lunga\" },"));
        var again = RdpmConfigFile.Parse(original.ToJson());
        Assert.Equal(original.ServerUrl, again.ServerUrl);
        Assert.Equal(original.RegistrationCode, again.RegistrationCode);
        Assert.Equal("mario", again.Username);
        Assert.Equal("frase lunga", again.Password);
        Assert.Equal(original.Vpn, again.Vpn);
    }

    [Theory]
    [InlineData("", "vuoto")]
    [InlineData("   ", "vuoto")]
    [InlineData("{ non json", "JSON valido")]
    [InlineData("[1,2]", "non è né un file")]
    [InlineData("{}", "niente di utile")]
    [InlineData("""{ "formato": "altro", "server": { "url": "https://a.example.com" } }""", "formato")]
    [InlineData("""{ "versione": 99, "server": { "url": "https://a.example.com" } }""", "più nuova")]
    [InlineData("""{ "versione": "uno", "server": { "url": "https://a.example.com" } }""", "versione")]
    [InlineData("""{ "server": "https://a.example.com" }""", "oggetto JSON")]
    [InlineData("""{ "server": { "url": "ftp://a" } }""", "server.url")]
    [InlineData("""{ "server": { "url": "https://a.example.com" }, "account": { "utente": "mario" } }""", "utente")]
    [InlineData("""{ "vpn": { "richiediChiaveAlServer": true, "endpoint": "a.example.com:51820", "chiavePubblicaServer": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" } }""", "server.url")]
    public void I_file_non_validi_danno_un_messaggio_chiaro(string text, string expected)
    {
        var ex = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse(text));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<FormatException>(ex);
    }

    [Fact]
    public void Gli_errori_della_sezione_vpn_si_elencano_tutti()
    {
        var ex = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse("""
            { "server": { "url": "https://a.example.com" },
              "vpn": { "endpoint": "senza-porta", "chiavePubblicaServer": "corta", "rete": "10.66.0.0",
                       "dns": "non-un-ip", "keepalive": -5, "indirizzo": "10.66.0.2" } }
            """));
        Assert.True(ex.Errors.Count >= 6, string.Join(" | ", ex.Errors));
        Assert.Contains(ex.Errors, e => e.Contains("vpn.endpoint"));
        Assert.Contains(ex.Errors, e => e.Contains("chiavePubblicaServer"));
        Assert.Contains(ex.Errors, e => e.Contains("vpn.rete"));
        Assert.Contains(ex.Errors, e => e.Contains("vpn.dns"));
        Assert.Contains(ex.Errors, e => e.Contains("keepalive"));
        Assert.Contains(ex.Errors, e => e.Contains("indirizzo"));
    }

    [Fact]
    public void Manca_endpoint_e_chiave_del_server()
    {
        var ex = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse("""{ "vpn": { "keepalive": 25 } }"""));
        Assert.Contains(ex.Errors, e => e.Contains("Manca «vpn.endpoint»"));
        Assert.Contains(ex.Errors, e => e.Contains("Manca «vpn.chiavePubblicaServer»"));
    }

    [Fact]
    public void Un_conf_WireGuard_normale_e_accettato()
    {
        var priv = WireGuardKeys.Generate().PrivateKey;
        var f = RdpmConfigFile.Parse(new WireGuardConfig
        {
            PrivateKey = priv, Address = "10.66.0.7/32", PeerPublicKey = ServerKey, Endpoint = "vpn.example.com:51820", Dns = "10.66.0.1",
        }.ToConf());
        Assert.True(f.FromWireGuardConf);
        Assert.Equal(priv, f.Vpn!.PrivateKey);
        Assert.Equal("10.66.0.7/32", f.Vpn.Address);
        Assert.False(f.Vpn.RequestKeyFromServer);
        Assert.True(f.HasSecrets);
    }

    [Fact]
    public void Un_conf_rotto_o_un_testo_qualsiasi_sono_rifiutati_con_un_motivo()
    {
        var ex = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse("[Interface]\nPrivateKey = no\n"));
        Assert.Contains("Peer", ex.Message);
        var other = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse("ciao, non sono una configurazione"));
        Assert.Contains(".conf", other.Message);
        var bad = Assert.Throws<ConfigFileException>(() => RdpmConfigFile.Parse(
            "[Interface]\nPrivateKey = no\nAddress = x\n[Peer]\nPublicKey = y\nEndpoint = z\nAllowedIPs = w\n"));
        Assert.True(bad.Errors.Count >= 3);
    }

    [Fact]
    public void Load_dice_se_il_file_non_c_e()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => RdpmConfigFile.Load(Path.Combine(Path.GetTempPath(), "non-esiste-" + Guid.NewGuid() + ".rdpmconfig")));
        Assert.Contains("non trovato", ex.Message);
    }

    [Fact]
    public void L_argomento_di_avvio_si_riconosce_solo_se_e_un_file_di_configurazione_esistente()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-cfgarg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = Path.Combine(dir, "a.RDPMCONFIG");
            var other = Path.Combine(dir, "a.txt");
            File.WriteAllText(cfg, Sample());
            File.WriteAllText(other, "x");
            Assert.True(RdpmConfigFile.LooksLikeConfigArgument(cfg));
            Assert.False(RdpmConfigFile.LooksLikeConfigArgument(other));
            Assert.False(RdpmConfigFile.LooksLikeConfigArgument(Path.Combine(dir, "manca.rdpmconfig")));
            Assert.False(RdpmConfigFile.LooksLikeConfigArgument("--dati"));
            Assert.Equal(cfg, RdpmConfigFile.FindArgument(["--dati", other, cfg]));
            Assert.Null(RdpmConfigFile.FindArgument([]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Il_comando_del_server_produce_un_file_che_l_app_legge()
    {
        var options = new RdpmSync.SyncOptions
        {
            VpnEndpoint = "vpn.example.com:51820", VpnServerPublicKey = ServerKey, RegistrationCode = "codice-vero", VpnDns = "10.66.0.1",
        };
        var (json, errors) = RdpmSync.ClientConfigExport.Build(options, "https://vpn.example.com:8443", null, null, null);
        Assert.Empty(errors);
        var f = RdpmConfigFile.Parse(json!);
        Assert.Equal("https://vpn.example.com:8443/", f.ServerUrl);
        Assert.Equal("http://10.66.0.1:8787/", f.ServerUrlInside);
        Assert.Equal("codice-vero", f.RegistrationCode);
        Assert.Equal(ServerKey, f.Vpn!.ServerPublicKey);
        Assert.True(f.Vpn.RequestKeyFromServer);

        var (none, why) = RdpmSync.ClientConfigExport.Build(new RdpmSync.SyncOptions(), "https://x.example.com", null, null, null);
        Assert.Null(none);
        Assert.True(why.Count >= 2);
        // la riga di comando del server stampa lo stesso file
        var output = new StringWriter();
        Assert.Equal(0, RdpmSync.Cli.Run(["export-client-config", "--url", "https://vpn.example.com:8443"], options, null, output));
        Assert.Equal(json, output.ToString());
    }
}
