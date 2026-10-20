using System.Security.Cryptography;
using RdpManager.Core;

namespace RdpManager.Tests;

public class PasswordProtectorTests
{
    [Theory]
    [InlineData("semplice")]
    [InlineData("con spazi e accenti: àèìòù €")]
    [InlineData("")]
    public void ProtectThenUnprotect_ReturnsOriginal(string plain)
    {
        var protectedText = PasswordProtector.Protect(plain);
        Assert.Equal(plain, PasswordProtector.Unprotect(protectedText));
    }

    [Fact]
    public void Protect_DoesNotContainPlainText()
    {
        const string plain = "Segreto123";
        var protectedText = PasswordProtector.Protect(plain);
        Assert.DoesNotContain(plain, protectedText);
        var bytes = Convert.FromBase64String(protectedText);
        Assert.DoesNotContain(plain, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Protect_IsRandomized()
    {
        Assert.NotEqual(PasswordProtector.Protect("x"), PasswordProtector.Protect("x"));
    }

    [Fact]
    public void Unprotect_InvalidData_Throws()
    {
        Assert.ThrowsAny<CryptographicException>(() => PasswordProtector.Unprotect(Convert.ToBase64String(new byte[] { 9, 9, 9 })));
    }

    [Fact]
    public void TryUnprotect_InvalidOrEmpty_ReturnsFalse()
    {
        Assert.False(PasswordProtector.TryUnprotect(null, out _));
        Assert.False(PasswordProtector.TryUnprotect("non-base64!!", out _));
        Assert.False(PasswordProtector.TryUnprotect(Convert.ToBase64String(new byte[] { 1 }), out var plain));
        Assert.Null(plain);
    }

    [Fact]
    public void RdpFileFormat_RoundTrips()
    {
        var hex = PasswordProtector.ProtectForRdpFile("pwd-rdp");
        Assert.Matches("^[0-9A-F]+$", hex);
        Assert.Equal("pwd-rdp", PasswordProtector.TryUnprotectFromRdpFile(hex));
        Assert.Null(PasswordProtector.TryUnprotectFromRdpFile("ZZZZ"));
    }
}
