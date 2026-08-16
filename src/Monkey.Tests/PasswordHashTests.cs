using Monkey.Core;
using Xunit;

namespace Monkey.Tests;

public sealed class PasswordHashTests
{
    [Fact]
    public void Create_ThenVerify_Succeeds()
    {
        var (hash, salt, iterations) = PasswordHash.Create("some long password", TestEnv.FastIterations);

        Assert.Equal(TestEnv.FastIterations, iterations);
        Assert.True(PasswordHash.Verify("some long password", hash, salt, iterations));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var (hash, salt, iterations) = PasswordHash.Create("some long password", TestEnv.FastIterations);

        Assert.False(PasswordHash.Verify("some long passwore", hash, salt, iterations));
        Assert.False(PasswordHash.Verify("", hash, salt, iterations));
    }

    [Fact]
    public void Create_UsesFreshSaltEveryTime()
    {
        var first = PasswordHash.Create("same input", TestEnv.FastIterations);
        var second = PasswordHash.Create("same input", TestEnv.FastIterations);

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Theory]
    [InlineData(null, "c2FsdA==", 1000)]
    [InlineData("", "c2FsdA==", 1000)]
    [InlineData("aGFzaA==", null, 1000)]
    [InlineData("aGFzaA==", "", 1000)]
    [InlineData("aGFzaA==", "c2FsdA==", 0)]
    [InlineData("aGFzaA==", "c2FsdA==", -1)]
    [InlineData("kein base64!", "c2FsdA==", 1000)]
    [InlineData("aGFzaA==", "kein base64!", 1000)]
    public void Verify_BrokenStoredValues_FailClosed(string? hash, string? salt, int iterations)
    {
        // Ein manipulierter oder unlesbarer Bestand darf nie als Freifahrtschein wirken.
        Assert.False(PasswordHash.Verify("whatever", hash, salt, iterations));
    }

    [Fact]
    public void DefaultIterations_MeetCurrentGuidance()
    {
        // OWASP-Empfehlung fuer PBKDF2-SHA256 liegt bei 600k. Wer das absenkt,
        // soll es hier bewusst tun muessen.
        Assert.True(PasswordHash.DefaultIterations >= 600_000);
        Assert.True(PasswordHash.MinimumLength >= 10);
    }
}
