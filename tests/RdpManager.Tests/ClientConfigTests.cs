using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>File di configurazione del client (rdpmanager-config.json): lettura, errori, ricerca automatica.</summary>
public class ClientConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdpm-clientcfg-" + Guid.NewGuid().ToString("N"));

    public ClientConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string folder, string text)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ClientConfig.FileName);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Valid = """
        {
          "_commento": "ignorato",
          "server": "vpn.example.com:8443",
          "codiceRegistrazione": "  codice-di-prova  ",
          "vpnEndpoint": "vpn.example.com:51820"
        }
        """;

    [Fact]
    public void File_valido_si_importa()
    {
        var cfg = ClientConfig.Load(Write(_dir, Valid));
        Assert.Equal("https://vpn.example.com:8443/", cfg.ServerUrl);
        Assert.Equal("codice-di-prova", cfg.RegistrationCode);
        Assert.Equal("vpn.example.com:51820", cfg.VpnEndpoint);
    }

    [Fact]
    public void Bastano_i_campi_presenti()
    {
        var cfg = ClientConfig.Parse("""{ "codiceRegistrazione": "abc" }""");
        Assert.Equal("", cfg.ServerUrl);
        Assert.Equal("abc", cfg.RegistrationCode);
    }

    [Fact]
    public void File_mancante_da_errore_chiaro()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => ClientConfig.Load(Path.Combine(_dir, "non-c-e.json")));
        Assert.Contains("non trovato", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ non e' json")]
    [InlineData("[1, 2]")]
    [InlineData("{}")]
    [InlineData("""{ "server": "ftp://vpn.example.com" }""")]
    public void File_malformato_o_vuoto_da_errore(string text)
    {
        Assert.Throws<FormatException>(() => ClientConfig.Parse(text));
        Assert.Throws<FormatException>(() => ClientConfig.Load(Write(_dir, text)));
    }

    [Fact]
    public void Ricerca_automatica_accanto_all_eseguibile_poi_nella_cartella_dei_dati()
    {
        var exe = Path.Combine(_dir, "exe");
        var data = Path.Combine(_dir, "dati");
        Directory.CreateDirectory(exe);
        Assert.Null(ClientConfig.FindDefault(exe, data));

        Write(data, """{ "server": "https://dati.example.com" }""");
        Assert.Equal("https://dati.example.com/", ClientConfig.FindDefault(exe, data)!.ServerUrl);

        Write(exe, """{ "server": "https://exe.example.com" }""");
        Assert.Equal("https://exe.example.com/", ClientConfig.FindDefault(exe, data)!.ServerUrl);
    }

    [Fact]
    public void Ricerca_automatica_ignora_un_file_rotto_e_prova_l_altro()
    {
        var exe = Path.Combine(_dir, "exe");
        var data = Path.Combine(_dir, "dati");
        Write(exe, "rotto");
        Write(data, """{ "server": "https://dati.example.com" }""");
        Assert.Equal("https://dati.example.com/", ClientConfig.FindDefault(exe, data)!.ServerUrl);
    }
}
