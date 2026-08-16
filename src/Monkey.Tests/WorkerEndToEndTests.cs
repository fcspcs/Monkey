using System.Text.RegularExpressions;
using Monkey.Core;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

/// <summary>
/// Die Konigsdisziplin: der ECHTE Sync-Dienst spricht mit dem ECHTEN worker.js
/// (gehostet von cloud/worker.harness.mjs). Nur Telegram selbst ist Attrappe.
/// Diese Tests fangen, was getrennte Suiten nie sehen: ein Auseinanderlaufen
/// der beiden Seiten des Protokolls oder der doppelt implementierten
/// Gutschrift-Mathematik.
/// </summary>
public sealed class WorkerEndToEndTests
{
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }

        return await condition();
    }

    /// <summary>Nachbau von fmt() aus worker.js - fuer den Minutengenauen Vergleich.</summary>
    private static string WorkerFormat(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        return h >= 1 ? $"{h} h {m:00} min" : $"{m} min";
    }

    [NodeFact]
    public async Task FullChain_FriendCommand_RunsThroughRealWorkerAndBack()
    {
        using var harness = WorkerHarness.Start();
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 0);
        engine.SetTelegram(true, harness.Url, Dpapi.Protect(WorkerHarness.SyncSecret));
        var sync = new TelegramSync(engine);

        // Der Worker richtet seine (Fake-)Telegram-Webhooks ein.
        await harness.PostAsync("/provision", new { });

        // Pairing auf dem normalen Weg: Code vom PC erzeugen lassen ...
        var pair = await sync.HandleAsync(new Request
        {
            Type = RequestType.TelegramPair,
            Password = TestEnv.Password,
            PairRole = "friend",
        });
        Assert.True(pair.Ok, pair.Message);
        var code = Regex.Match(pair.Message!, @"\d{6}").Value;

        // ... und vom Freund per Telegram-Update einloesen.
        await harness.TelegramAsync("friend", 4242, $"/pair {code}");
        Assert.Contains("Paired", await harness.LastReplyAsync());

        // Der echte Sync-Dienst startet und meldet den Stand beim Worker an.
        await sync.StartAsync(CancellationToken.None);
        try
        {
            // Erst wenn der Worker einen Stand hat, nimmt er Befehle an.
            Assert.True(await WaitForAsync(async () =>
            {
                await harness.TelegramAsync("friend", 4242, "/status");
                return (await harness.LastReplyAsync()).Contains("Balance:");
            }, TimeSpan.FromSeconds(15)), "the first sync never reported a state");

            // Der Freund legt 25 Minuten nach - der Worker stellt das in die Queue.
            await harness.TelegramAsync("friend", 4242, "/add 25");

            // Naechster Sync-Takt (im Betrieb spaetestens nach 30 s - hier
            // geweckt wie von der Engine): Befehl abholen, anwenden, quittieren.
            try { engine.TelegramKick.Release(); }
            catch (SemaphoreFullException) { /* Weckruf steht schon an. */ }

            Assert.True(await WaitForAsync(
                () => Task.FromResult(engine.Status().BalanceSeconds >= 1500),
                TimeSpan.FromSeconds(15)), "the queued command never reached the engine");

            // Die Quittung muss als Bestaetigung beim Absender ankommen.
            Assert.True(await WaitForAsync(async () =>
                (await harness.RepliesAsync()).Any(r => r.ChatId == 4242 && r.Text.StartsWith("✅")),
                TimeSpan.FromSeconds(15)), "the confirmation never went back to the friend");
        }
        finally
        {
            await sync.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1500, engine.Status().BalanceSeconds, 1);

        // Und der Worker sieht nach dem letzten Abgleich denselben Stand.
        await harness.TelegramAsync("friend", 4242, "/status");
        Assert.Contains("Balance: 25 min", await harness.LastReplyAsync());

        // Der Pairing-Code war ein Einmalcode.
        await harness.TelegramAsync("friend", 777, $"/pair {code}");
        Assert.Contains("No valid pairing code", await harness.LastReplyAsync());
    }

    [NodeFact]
    public async Task Projection_WorkerAndEngine_AgreeAfterDaysOffline()
    {
        using var harness = WorkerHarness.Start();

        // Ein PC, der seit sechs Tagen aus ist: 10 Minuten Rest, 30 min/Tag.
        var engine = TestEnv.Engine(s =>
        {
            s.LastAccrualDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-6);
            s.BalanceSeconds = 600;
            s.EarnedSeconds = 600;
        });

        await harness.PostAsync("/provision", new { });
        await harness.PostAsync("/pair", new { role = "monkey", code = "123456" });
        await harness.TelegramAsync("monkey", 111, "/pair 123456");

        // Der Worker bekommt den Stand VOR der Gutschrift und muss die sechs
        // Tage selbst hochrechnen.
        await harness.PostAsync("/sync", new { state = engine.BuildTelegramSnapshot() });
        await harness.TelegramAsync("monkey", 111, "/status");
        var projected = await harness.LastReplyAsync();

        // Die Engine holt die Gutschrift jetzt wirklich nach.
        engine.Tick(TimeSpan.FromSeconds(1));
        var engineBalance = engine.Status().BalanceSeconds;

        Assert.Equal(600 + 6 * 1800, engineBalance, 1);
        Assert.Contains($"Balance: {WorkerFormat(engineBalance)}", projected);
    }

    [NodeFact]
    public async Task WrongSyncSecret_SurfacesInStatus_AndNothingIsApplied()
    {
        using var harness = WorkerHarness.Start();
        var engine = TestEnv.Engine();
        engine.SetTelegram(true, harness.Url, Dpapi.Protect("wrong-secret-0123456789abcdef000"));
        var sync = new TelegramSync(engine);

        await sync.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitForAsync(
                () => Task.FromResult(engine.Status().TelegramLastError is not null),
                TimeSpan.FromSeconds(15)), "the sync error never surfaced");
        }
        finally
        {
            await sync.StopAsync(CancellationToken.None);
        }

        Assert.Contains("rejected the sync secret", engine.Status().TelegramLastError);
    }
}
