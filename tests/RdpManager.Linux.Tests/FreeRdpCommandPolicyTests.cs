using RdpManager.Core;
using RdpManager.Platform.Linux;

namespace RdpManager.Linux.Tests;

public class FreeRdpCommandPolicyTests
{
    private static readonly FreeRdpClient V3 = new("xfreerdp3", true, false);
    private static readonly FreeRdpClient V2 = new("xfreerdp", false, false);

    private static RdpConnection Conn() => new() { Host = "pc1", Name = "PC 1" };

    [Fact]
    public void Il_certificato_predefinito_e_fidati_al_primo_uso()
    {
        Assert.Contains("/cert:tofu", FreeRdpCommandBuilder.Build(Conn(), V3).Arguments);
        Assert.Contains("/cert-tofu", FreeRdpCommandBuilder.Build(Conn(), V2).Arguments);
    }

    [Theory]
    [InlineData(CertificatePolicy.Tofu, true, "/cert:tofu")]
    [InlineData(CertificatePolicy.Ignore, true, "/cert:ignore")]
    [InlineData(CertificatePolicy.Deny, true, "/cert:deny")]
    [InlineData(CertificatePolicy.Tofu, false, "/cert-tofu")]
    [InlineData(CertificatePolicy.Ignore, false, "/cert-ignore")]
    [InlineData(CertificatePolicy.Deny, false, "/cert-deny")]
    public void La_politica_del_certificato_arriva_dal_contesto(CertificatePolicy policy, bool v3, string expected)
    {
        var ctx = new FreeRdpLaunchContext { CertificatePolicy = policy };
        var args = FreeRdpCommandBuilder.Build(Conn(), v3 ? V3 : V2, ctx).Arguments;
        Assert.Contains(expected, args);
        Assert.Single(args, a => a.StartsWith("/cert", StringComparison.Ordinal));
    }

    [Fact]
    public void Tieni_sveglia_passa_il_heartbeat_a_xfreerdp()
    {
        var c = Conn();
        Assert.DoesNotContain("+heartbeat", FreeRdpCommandBuilder.Build(c, V3).Arguments);
        c.KeepAlive = true;
        Assert.Contains("+heartbeat", FreeRdpCommandBuilder.Build(c, V3).Arguments);
        Assert.Contains("+heartbeat", FreeRdpCommandBuilder.Build(c, V2).Arguments);
    }

    [Fact]
    public void La_risoluzione_della_cartella_ereditata_arriva_alla_riga_di_comando()
    {
        var doc = new ConnectionsDocument();
        var c = new RdpConnection { Host = "pc1", Group = "Uffici/Roma", InheritFolderResolution = true };
        doc.Connections.Add(c);
        doc.SetFolderSettings("Uffici", null, ResolutionMode.Fixed, 1600, 900);
        var args = FreeRdpCommandBuilder.Build(doc.WithResolvedCredentials(c), V3).Arguments;
        Assert.Contains("/size:1600x900", args);
        Assert.DoesNotContain("/dynamic-resolution", args);
    }
}
