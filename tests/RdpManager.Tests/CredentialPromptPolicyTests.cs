using RdpManager.Core;

namespace RdpManager.Tests;

public class CredentialPromptPolicyTests
{
    [Fact]
    public void ConPasswordSalvataLaRichiestaDiWindowsEVietataAOgniConnessione()
    {
        var doc = new ConnectionsDocument();
        var c = new RdpConnection { Host = "pc", Username = "u", Password = "segreto" };
        doc.Connections.Add(c);
        // Prima connessione e riconnessione dopo una caduta: stesso risultato.
        for (int i = 0; i < 2; i++)
        {
            var effective = doc.WithResolvedCredentials(c);
            var policy = CredentialPromptPolicy.For(!string.IsNullOrEmpty(effective.Password), allowPromptWhenNoPassword: true);
            Assert.False(policy.PromptForCredentials);
            Assert.False(policy.AllowPrompting);
            Assert.Equal("segreto", CredentialPromptPolicy.PasswordFor(effective));
        }
    }

    [Fact]
    public void ConPasswordDelProfiloLaRichiestaEVietataAncheDopoBloccoESblocco()
    {
        var doc = new ConnectionsDocument();
        var p = new CredentialProfile { Name = "p", Username = "u", Password = "pw" };
        doc.CredentialProfiles.Add(p);
        var c = new RdpConnection { Host = "pc", CredentialProfileId = p.Id };
        doc.Connections.Add(c);
        doc.SealPasswords();
        doc.UnsealPasswords();
        var effective = doc.WithResolvedCredentials(c);
        Assert.Equal("pw", CredentialPromptPolicy.PasswordFor(effective));
        Assert.False(CredentialPromptPolicy.For(true, true).AllowPrompting);
    }

    [Fact]
    public void SenzaPasswordWindowsPuoChiedere()
    {
        var policy = CredentialPromptPolicy.For(false, true);
        Assert.True(policy.PromptForCredentials);
        Assert.True(policy.AllowPrompting);
        Assert.False(policy.Strict);
    }

    [Fact]
    public void ConDivietoEsplicitoNonSiChiedeMai()
    {
        var policy = CredentialPromptPolicy.For(false, false);
        Assert.False(policy.AllowPrompting);
        Assert.True(policy.Strict);
    }
}
