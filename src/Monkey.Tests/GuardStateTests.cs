using Monkey.Core;
using Xunit;

namespace Monkey.Tests;

public sealed class GuardStateTests
{
    [Theory]
    [InlineData(0, 1)]          // nichts erspart -> kleiner Affe
    [InlineData(1_799, 1)]      // knapp unter einem Tagesbudget
    [InlineData(1_800, 1)]      // genau ein Tagesbudget -> Stufe 1
    [InlineData(3_600, 2)]      // zwei Tagesbudgets -> Stufe 2
    [InlineData(9_000, 5)]      // fuenffaches Tagesbudget -> Stufe 5
    [InlineData(1_000_000, 5)]  // mehr geht nicht
    public void EvolutionStage_GrowsWithSavings(double earnedSeconds, int expectedStage)
    {
        var state = new GuardState { EarnedSeconds = earnedSeconds };
        state.Config.DailyGrantMinutes = 30;

        Assert.Equal(expectedStage, state.EvolutionStage);
    }

    [Fact]
    public void EvolutionStage_ZeroDailyGrant_StaysAtOne()
    {
        var state = new GuardState { EarnedSeconds = 100_000 };
        state.Config.DailyGrantMinutes = 0;

        Assert.Equal(1, state.EvolutionStage);
    }

    [Fact]
    public void Json_RoundTrip_KeepsEverything()
    {
        var state = new GuardState
        {
            PasswordHash = "aGFzaA==",
            PasswordSalt = "c2FsdA==",
            PasswordIterations = 600_000,
            BalanceSeconds = 1234.5,
            EarnedSeconds = 600,
            LastAccrualDate = new DateOnly(2026, 8, 16),
            TrustedNow = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.FromHours(2)),
            PauseUntil = new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.FromHours(2)),
            ClockTamperEvents = 3,
            EmptyGraceRuns = 2,
            AppliedRemoteCommandIds = ["a", "b"],
        };
        state.Config.DailyGrantMinutes = 45;
        state.Config.CapMinutes = 300;
        state.Telegram.Enabled = true;
        state.Telegram.WorkerUrl = "https://x.workers.dev";
        state.Telegram.SyncSecretProtected = "chiffrat";
        state.Telegram.Managed = true;
        state.Telegram.CloudflareAccountId = new string('a', 32);
        state.Telegram.ScriptName = "monkey-telegram-abc";
        state.Telegram.KvNamespaceId = "kv-id";
        state.Telegram.WorkerVersion = 2;

        var restored = GuardState.FromJson(state.ToJson())!;

        Assert.Equal(state.PasswordHash, restored.PasswordHash);
        Assert.Equal(state.BalanceSeconds, restored.BalanceSeconds);
        Assert.Equal(state.EarnedSeconds, restored.EarnedSeconds);
        Assert.Equal(state.LastAccrualDate, restored.LastAccrualDate);
        Assert.Equal(state.TrustedNow, restored.TrustedNow);
        Assert.Equal(state.PauseUntil, restored.PauseUntil);
        Assert.Equal(state.ClockTamperEvents, restored.ClockTamperEvents);
        Assert.Equal(state.EmptyGraceRuns, restored.EmptyGraceRuns);
        Assert.Equal(state.AppliedRemoteCommandIds, restored.AppliedRemoteCommandIds);
        Assert.Equal(45, restored.Config.DailyGrantMinutes);
        Assert.Equal(300, restored.Config.CapMinutes);
        Assert.True(restored.Telegram.Enabled);
        Assert.Equal(state.Telegram.WorkerUrl, restored.Telegram.WorkerUrl);
        Assert.Equal(state.Telegram.SyncSecretProtected, restored.Telegram.SyncSecretProtected);
        Assert.True(restored.Telegram.Managed);
        Assert.Equal(state.Telegram.CloudflareAccountId, restored.Telegram.CloudflareAccountId);
        Assert.Equal(state.Telegram.ScriptName, restored.Telegram.ScriptName);
        Assert.Equal(state.Telegram.KvNamespaceId, restored.Telegram.KvNamespaceId);
        Assert.Equal(2, restored.Telegram.WorkerVersion);
    }

    [Fact]
    public void Config_Clone_IsIndependent()
    {
        var config = new GuardConfig { DailyGrantMinutes = 30 };
        var clone = config.Clone();
        clone.DailyGrantMinutes = 99;

        Assert.Equal(30, config.DailyGrantMinutes);
    }
}
