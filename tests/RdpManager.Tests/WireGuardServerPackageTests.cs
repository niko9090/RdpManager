using System.IO;

namespace RdpManager.Tests;

/// <summary>
/// Controlla che il pacchetto server/wireguard-synology esista e sia coerente
/// con le costanti del client (valori letterali: non dipende da VpnDefaults).
/// </summary>
public class WireGuardServerPackageTests
{
    private const string Port = "51820";
    private const string Network = "10.66.0.0/24";
    private const string ServerAddress = "10.66.0.1/24";
    private const string Endpoint = "vpn.example.com";

    private static string PackageDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "server", "wireguard-synology");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("server/wireguard-synology non trovata");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { PackageDir() }.Concat(parts).ToArray()));

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("LEGGIMI.md")]
    [InlineData("config", "wg0.conf.modello")]
    [InlineData("scripts", "prima-generazione.sh")]
    [InlineData("scripts", "aggiungi-pc.sh")]
    [InlineData("scripts", "elenca-pc.sh")]
    [InlineData("scripts", "rimuovi-pc.sh")]
    public void Package_files_exist(params string[] parts)
    {
        Assert.True(File.Exists(Path.Combine(new[] { PackageDir() }.Concat(parts).ToArray())));
    }

    [Fact]
    public void Compose_has_port_capabilities_and_forwarding()
    {
        var text = Read("docker-compose.yml");
        Assert.Contains(Port + ":" + Port + "/udp", text);
        Assert.Contains("NET_ADMIN", text);
        Assert.Contains("net.ipv4.ip_forward=1", text);
        Assert.Contains("restart: unless-stopped", text);
    }

    [Fact]
    public void Server_template_matches_client_constants()
    {
        var text = Read("config", "wg0.conf.modello");
        Assert.Contains("Address = " + ServerAddress, text);
        Assert.Contains("ListenPort = " + Port, text);
        Assert.Contains("PrivateKey", text);
    }

    [Fact]
    public void Guide_mentions_client_constants_and_dlls()
    {
        var text = Read("LEGGIMI.md");
        Assert.Contains(Port + "/UDP", text);
        Assert.Contains(Network, text);
        Assert.Contains(Endpoint, text);
        Assert.Contains("192.0.2.10", text);
        Assert.Contains("wireguard-nt", text);
        Assert.Contains("wireguard.dll", text);
        Assert.Contains("src/RdpManager/Vpn/dll/amd64/", text);
    }

    [Fact]
    public void Add_script_uses_endpoint_syncconf_and_lf_endings()
    {
        var add = Read("scripts", "aggiungi-pc.sh");
        Assert.Contains("WG_ENDPOINT", add);
        Assert.Contains("${WG_ENDPOINT:?", add); // obbligatorio, nessun predefinito
        Assert.Contains("wg syncconf", add);
        Assert.Contains("10.66.0.", add);
        foreach (var name in new[] { "aggiungi-pc.sh", "elenca-pc.sh", "rimuovi-pc.sh", "prima-generazione.sh" })
            Assert.DoesNotContain("\r", Read("scripts", name));
    }
}
