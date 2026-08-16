using Monkey.Core;
using Xunit;

namespace Monkey.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Request_RoundTrip_KeepsAllFields()
    {
        var request = new Request
        {
            Type = RequestType.TelegramDeploy,
            Password = "geheim",
            NewPassword = "neu",
            Minutes = 42,
            SessionId = 7,
            ScreensaverRunning = true,
            DisplayOff = true,
            Config = new GuardConfig { DailyGrantMinutes = 45 },
            WorkerUrl = "https://x.workers.dev",
            SyncSecret = "secret",
            MonkeyToken = "123456:token",
            FriendToken = "654321:token",
            CloudflareAccountId = new string('a', 32),
            CloudflareApiToken = "api-token-api-token-api",
            PairRole = "friend",
        };

        var restored = Request.FromJson(request.ToJson())!;

        Assert.Equal(request.Type, restored.Type);
        Assert.Equal(request.Password, restored.Password);
        Assert.Equal(request.NewPassword, restored.NewPassword);
        Assert.Equal(request.Minutes, restored.Minutes);
        Assert.Equal(request.SessionId, restored.SessionId);
        Assert.True(restored.ScreensaverRunning);
        Assert.True(restored.DisplayOff);
        Assert.Equal(45, restored.Config!.DailyGrantMinutes);
        Assert.Equal(request.WorkerUrl, restored.WorkerUrl);
        Assert.Equal(request.SyncSecret, restored.SyncSecret);
        Assert.Equal(request.MonkeyToken, restored.MonkeyToken);
        Assert.Equal(request.FriendToken, restored.FriendToken);
        Assert.Equal(request.CloudflareAccountId, restored.CloudflareAccountId);
        Assert.Equal(request.CloudflareApiToken, restored.CloudflareApiToken);
        Assert.Equal(request.PairRole, restored.PairRole);
    }

    [Fact]
    public void Response_RoundTrip_KeepsStatus()
    {
        var response = Response.Success("alles gut", new StatusDto
        {
            BalanceSeconds = 100,
            Paused = true,
            EvolutionStage = 3,
            PersistenceError = "Platte voll",
            TelegramEnabled = true,
            TelegramWorkerHost = "x.workers.dev",
            TelegramWorkerManaged = true,
            TelegramWorkerVersion = 2,
            SignedUpdatesAvailable = true,
        });

        var restored = Response.FromJson(response.ToJson())!;

        Assert.True(restored.Ok);
        Assert.Equal("alles gut", restored.Message);
        Assert.Equal(100, restored.Status!.BalanceSeconds);
        Assert.True(restored.Status.Paused);
        Assert.Equal(3, restored.Status.EvolutionStage);
        Assert.Equal("Platte voll", restored.Status.PersistenceError);
        Assert.True(restored.Status.TelegramEnabled);
        Assert.Equal("x.workers.dev", restored.Status.TelegramWorkerHost);
        Assert.True(restored.Status.TelegramWorkerManaged);
        Assert.Equal(2, restored.Status.TelegramWorkerVersion);
        Assert.True(restored.Status.SignedUpdatesAvailable);
    }

    [Fact]
    public void Fail_CarriesMessageAndNoStatus()
    {
        var restored = Response.FromJson(Response.Fail("nope").ToJson())!;

        Assert.False(restored.Ok);
        Assert.Equal("nope", restored.Message);
        Assert.Null(restored.Status);
    }
}
