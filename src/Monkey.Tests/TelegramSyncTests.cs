using System.Text.Json;
using Monkey.Core;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

/// <summary>
/// Integrationstests der PC-Seite: eine echte GuardEngine spricht ueber HTTP
/// mit einem Fake-Worker. Geprueft wird der komplette Abgleich - Stand melden,
/// Befehle anwenden, Quittungen nachreichen - sowie die Pipe-Anfragen der
/// Telegram-Einrichtung.
/// </summary>
public sealed class TelegramSyncTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string ValidMonkeyToken = "123456:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ValidFriendToken = "654321:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly string ValidAccountId = new('a', 32);
    private const string ValidApiToken = "cf-api-token-cf-api-token";

    private static GuardEngine ConnectedEngine(FakeWorker worker, Action<GuardState>? mutate = null)
    {
        var engine = TestEnv.Engine(mutate);
        engine.SetTelegram(true, worker.Url, Dpapi.Protect(Secret));
        return engine;
    }

    private static Task<Response> SendAsync(TelegramSync sync, string type, Action<Request>? mutate = null)
    {
        var request = new Request { Type = type, Password = TestEnv.Password };
        mutate?.Invoke(request);
        return sync.HandleAsync(request);
    }

    // ------------------------------------------------------------- Abgleich

    [Fact]
    public async Task SyncLoop_ReportsState_AppliesCommands_SendsReceipts()
    {
        await using var worker = await FakeWorker.StartAsync();
        worker.SyncResponder = call => call == 1
            ? new { commands = new object[] { new { id = "c1", type = "add", minutes = 30 } } }
            : new { commands = Array.Empty<object>() };

        var engine = ConnectedEngine(worker, s => s.BalanceSeconds = 0);

        var sync = new TelegramSync(engine);
        await sync.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await worker.WaitUntilAsync(
                r => r.Count(x => x.Path == "/sync") >= 2, TimeSpan.FromSeconds(15)),
                "the sync loop never reached round two");
        }
        finally
        {
            await sync.StopAsync(CancellationToken.None);
        }

        var syncs = worker.Requests.Where(r => r.Path == "/sync").ToList();

        // Jede Runde traegt das Sync-Secret und den vollstaendigen Stand.
        Assert.All(syncs, r => Assert.Equal($"Bearer {Secret}", r.Authorization));
        var state = syncs[0].Body.GetProperty("state");
        Assert.Equal(0, state.GetProperty("balanceSeconds").GetDouble(), 1);
        Assert.Equal(30, state.GetProperty("dailyGrantMinutes").GetInt32());

        // Runde zwei quittiert den angewendeten Befehl.
        var receipt = syncs[1].Body.GetProperty("results").EnumerateArray().Single();
        Assert.Equal("c1", receipt.GetProperty("id").GetString());
        Assert.True(receipt.GetProperty("ok").GetBoolean());

        // Und die Engine hat die 30 Minuten wirklich gutgeschrieben.
        Assert.Equal(1800, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public async Task SyncLoop_MalformedCommands_AreSkippedOrRejected()
    {
        await using var worker = await FakeWorker.StartAsync();
        worker.SyncResponder = call => call == 1
            ? new
            {
                commands = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = 42, ["type"] = "add", ["minutes"] = 5 },
                    new Dictionary<string, object?> { ["type"] = "add", ["minutes"] = 5 },
                    new Dictionary<string, object?> { ["id"] = "x1", ["minutes"] = 5 },
                    new Dictionary<string, object?> { ["id"] = "x2", ["type"] = "add", ["minutes"] = 0 },
                    new Dictionary<string, object?> { ["id"] = "x3", ["type"] = "bogus", ["minutes"] = 1 },
                },
            }
            : new { commands = Array.Empty<object>() };

        var engine = ConnectedEngine(worker, s => s.BalanceSeconds = 0);

        var sync = new TelegramSync(engine);
        await sync.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await worker.WaitUntilAsync(
                r => r.Count(x => x.Path == "/sync") >= 2, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await sync.StopAsync(CancellationToken.None);
        }

        var syncs = worker.Requests.Where(r => r.Path == "/sync").ToList();
        var receipts = syncs[1].Body.GetProperty("results").EnumerateArray().ToList();

        // Nur die beiden formal gueltigen Befehle bekommen eine (ablehnende)
        // Quittung; der Rest wird kommentarlos verworfen.
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, r => Assert.False(r.GetProperty("ok").GetBoolean()));
        Assert.Equal(0, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public async Task SyncLoop_NotConnected_StaysSilent()
    {
        await using var worker = await FakeWorker.StartAsync();
        var engine = TestEnv.Engine(); // Telegram aus

        var sync = new TelegramSync(engine);
        await sync.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await sync.StopAsync(CancellationToken.None);

        Assert.Empty(worker.Requests);
    }

    // ----------------------------------------------------------- Einrichtung

    [Fact]
    public async Task Setup_RejectsBadInput()
    {
        var engine = TestEnv.Engine();
        var sync = new TelegramSync(engine);

        var wrongPassword = await sync.HandleAsync(new Request
        {
            Type = RequestType.TelegramSetup,
            Password = "voellig-falsch",
        });
        Assert.False(wrongPassword.Ok);
        Assert.Contains("Wrong master password", wrongPassword.Message);

        var httpUrl = await SendAsync(sync, RequestType.TelegramSetup, r =>
        {
            r.WorkerUrl = "http://x.workers.dev";
            r.SyncSecret = Secret;
        });
        Assert.False(httpUrl.Ok);
        Assert.Contains("https://", httpUrl.Message);

        var withQuery = await SendAsync(sync, RequestType.TelegramSetup, r =>
        {
            r.WorkerUrl = "https://x.workers.dev/?a=b";
            r.SyncSecret = Secret;
        });
        Assert.False(withQuery.Ok);

        var shortSecret = await SendAsync(sync, RequestType.TelegramSetup, r =>
        {
            r.WorkerUrl = "https://x.workers.dev";
            r.SyncSecret = "zu-kurz";
        });
        Assert.False(shortSecret.Ok);
        Assert.Contains("sync secret", shortSecret.Message);
    }

    [Fact]
    public async Task Setup_UnreachableWorker_FailsWithFriendlyMessage()
    {
        var engine = TestEnv.Engine();
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramSetup, r =>
        {
            r.WorkerUrl = "https://127.0.0.1:9";
            r.SyncSecret = Secret;
        });

        Assert.False(response.Ok);
        Assert.False(engine.TelegramConfig().Enabled);
    }

    [Fact]
    public async Task Deploy_RejectsBadInput()
    {
        var engine = TestEnv.Engine();
        var sync = new TelegramSync(engine);

        var badAccount = await SendAsync(sync, RequestType.TelegramDeploy, r =>
        {
            r.CloudflareAccountId = "nicht-hex";
            r.CloudflareApiToken = ValidApiToken;
            r.MonkeyToken = ValidMonkeyToken;
            r.FriendToken = ValidFriendToken;
        });
        Assert.False(badAccount.Ok);
        Assert.Contains("32 hexadecimal", badAccount.Message);

        var badApiToken = await SendAsync(sync, RequestType.TelegramDeploy, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = "kurz";
            r.MonkeyToken = ValidMonkeyToken;
            r.FriendToken = ValidFriendToken;
        });
        Assert.False(badApiToken.Ok);

        var badBotToken = await SendAsync(sync, RequestType.TelegramDeploy, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = ValidApiToken;
            r.MonkeyToken = "kein-token";
            r.FriendToken = ValidFriendToken;
        });
        Assert.False(badBotToken.Ok);

        var sameTokens = await SendAsync(sync, RequestType.TelegramDeploy, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = ValidApiToken;
            r.MonkeyToken = ValidMonkeyToken;
            r.FriendToken = ValidMonkeyToken;
        });
        Assert.False(sameTokens.Ok);
        Assert.Contains("different", sameTokens.Message);
    }

    [Fact]
    public async Task Deploy_WhileConnected_IsRejected()
    {
        await using var worker = await FakeWorker.StartAsync();
        var engine = ConnectedEngine(worker);
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramDeploy, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = ValidApiToken;
            r.MonkeyToken = ValidMonkeyToken;
            r.FriendToken = ValidFriendToken;
        });

        Assert.False(response.Ok);
        Assert.Contains("already connected", response.Message);
    }

    [Fact]
    public async Task WorkerMaintenance_RequiresConnection_AndManagedWorker()
    {
        var engine = TestEnv.Engine();
        var sync = new TelegramSync(engine);

        Assert.False((await SendAsync(sync, RequestType.TelegramWorkerCheck)).Ok);
        Assert.False((await SendAsync(sync, RequestType.TelegramWorkerRemove)).Ok);

        // Ein fremder, manuell verbundener Worker wird nie automatisch
        // aktualisiert oder geloescht.
        engine.SetTelegram(true, "https://custom.example.com", Dpapi.Protect(Secret));

        var update = await SendAsync(sync, RequestType.TelegramWorkerUpdate, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = ValidApiToken;
        });
        Assert.False(update.Ok);
        Assert.Contains("deployed by Monkey", update.Message);

        var remove = await SendAsync(sync, RequestType.TelegramWorkerRemove, r =>
        {
            r.CloudflareAccountId = ValidAccountId;
            r.CloudflareApiToken = ValidApiToken;
        });
        Assert.False(remove.Ok);
        Assert.Contains("managed by Monkey", remove.Message);
    }

    // -------------------------------------------------------------- Pairing

    [Fact]
    public async Task Pair_PostsSixDigitCode_AndReturnsIt()
    {
        await using var worker = await FakeWorker.StartAsync();
        var engine = ConnectedEngine(worker);
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramPair, r => r.PairRole = "friend");

        Assert.True(response.Ok);
        var pair = Assert.Single(worker.Requests, r => r.Path == "/pair");
        Assert.Equal($"Bearer {Secret}", pair.Authorization);
        Assert.Equal("friend", pair.Body.GetProperty("role").GetString());
        Assert.Equal(600, pair.Body.GetProperty("ttlSeconds").GetInt32());

        var code = pair.Body.GetProperty("code").GetString()!;
        Assert.Matches(@"^\d{6}$", code);
        Assert.Contains(code, response.Message);
    }

    [Fact]
    public async Task Pair_BadRole_OrNotConnected_Fails()
    {
        await using var worker = await FakeWorker.StartAsync();

        var offline = TestEnv.Engine();
        var offlineSync = new TelegramSync(offline);
        Assert.False((await SendAsync(offlineSync, RequestType.TelegramPair, r => r.PairRole = "friend")).Ok);

        var engine = ConnectedEngine(worker);
        var sync = new TelegramSync(engine);
        Assert.False((await SendAsync(sync, RequestType.TelegramPair, r => r.PairRole = "admin")).Ok);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Pair_WorkerRejectsSecret_GivesClearHint()
    {
        await using var worker = await FakeWorker.StartAsync();
        worker.PairStatus = 401;
        var engine = ConnectedEngine(worker);
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramPair, r => r.PairRole = "monkey");

        Assert.False(response.Ok);
        Assert.Contains("rejected the sync secret", response.Message);
    }

    // -------------------------------------------------------------- Trennen

    [Fact]
    public async Task Disable_WipesWorker_AndDisconnects()
    {
        await using var worker = await FakeWorker.StartAsync();
        var engine = ConnectedEngine(worker);
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramOff);

        Assert.True(response.Ok);
        Assert.Single(worker.Requests, r => r.Path == "/reset");
        Assert.False(engine.TelegramConfig().Enabled);
        Assert.Null(engine.TelegramConfig().WorkerUrl);
    }

    [Fact]
    public async Task Disable_WorkerUnreachable_StillDisconnectsLocally()
    {
        var engine = TestEnv.Engine();
        engine.SetTelegram(true, "http://127.0.0.1:9", Dpapi.Protect(Secret));
        var sync = new TelegramSync(engine);

        var response = await SendAsync(sync, RequestType.TelegramOff);

        Assert.True(response.Ok);
        Assert.Contains("disconnected locally", response.Message);
        Assert.False(engine.TelegramConfig().Enabled);
    }

    [Fact]
    public async Task Disable_NotConnected_Fails()
    {
        var engine = TestEnv.Engine();
        var sync = new TelegramSync(engine);

        Assert.False((await SendAsync(sync, RequestType.TelegramOff)).Ok);
    }

    [Fact]
    public async Task UnknownTelegramRequest_Fails()
    {
        var sync = new TelegramSync(TestEnv.Engine());

        Assert.False((await sync.HandleAsync(new Request { Type = "telegramgibtsnicht" })).Ok);
    }
}
