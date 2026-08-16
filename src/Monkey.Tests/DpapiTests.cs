using System.Security.Cryptography;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

public sealed class DpapiTests
{
    [Fact]
    public void ProtectThenUnprotect_RoundTrips()
    {
        const string secret = "sync-secret-0123456789abcdef0123456789";

        var protectedValue = Dpapi.Protect(secret);

        Assert.NotEqual(secret, protectedValue);
        Assert.DoesNotContain(secret, protectedValue);
        Assert.Equal(secret, Dpapi.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var protectedValue = Dpapi.Protect("secret value");
        var bytes = Convert.FromBase64String(protectedValue);
        bytes[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Unprotect_Garbage_Throws()
    {
        Assert.ThrowsAny<Exception>(() => Dpapi.Unprotect("kein base64!"));
    }
}
