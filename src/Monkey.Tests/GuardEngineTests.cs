using Monkey.Core;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

public sealed class GuardEngineTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    // ------------------------------------------------------- Tagesgutschrift

    [Fact]
    public void FirstTick_GrantsFirstTopUp()
    {
        var engine = TestEnv.Engine(s => s.LastAccrualDate = null);

        engine.Tick(Tick);

        Assert.Equal(30 * 60, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void Tick_CreditsMissedDays_UpToCap()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.LastAccrualDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-5);
            s.Config.DailyGrantMinutes = 30;
            s.Config.CapMinutes = 60;
        });

        engine.Tick(Tick);

        // Fuenf verpasste Tage, aber der Deckel liegt bei 60 Minuten.
        Assert.Equal(3600, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void Tick_SameDay_GrantsNothingTwice()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 100);

        engine.Tick(Tick);
        engine.Tick(Tick);

        Assert.Equal(100, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void Tick_BalanceAboveCap_IsNotTrimmed()
    {
        // Eine manuelle Gutschrift ueber dem Deckel bleibt stehen - sie waechst
        // nur nicht weiter.
        var engine = TestEnv.Engine(s =>
        {
            s.LastAccrualDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
            s.BalanceSeconds = 240 * 60 + 600;
        });

        engine.Tick(Tick);

        Assert.Equal(240 * 60 + 600, engine.Status().BalanceSeconds, 1);
    }

    // ------------------------------------------------------------ Verbrauch

    [Fact]
    public void Tick_ActiveSession_ConsumesBalance()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, TestEnv.User());

        Thread.Sleep(150);
        engine.Tick(Tick);

        var balance = engine.Status(7).BalanceSeconds;
        Assert.InRange(balance, 290, 299.99);
        Assert.True(engine.Status(7).SessionElapsedSeconds > 0);
    }

    [Fact]
    public void Tick_LockedSession_DoesNotConsume()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, TestEnv.User(locked: true));

        Thread.Sleep(100);
        engine.Tick(Tick);

        Assert.Equal(300, engine.Status().BalanceSeconds, 3);
    }

    [Fact]
    public void Tick_ScreensaverReported_DoesNotConsume()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => { r.SessionId = 7; r.ScreensaverRunning = true; });

        Thread.Sleep(100);
        engine.Tick(Tick);

        Assert.Equal(300, engine.Status().BalanceSeconds, 3);
    }

    [Fact]
    public void Tick_DisplayOffReported_DoesNotConsume()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => { r.SessionId = 7; r.DisplayOff = true; });

        Thread.Sleep(100);
        engine.Tick(Tick);

        Assert.Equal(300, engine.Status().BalanceSeconds, 3);
    }

    [Fact]
    public void Tick_LogonScreenOnly_DoesNotConsume()
    {
        // Der Anmeldebildschirm ist eine aktive Sitzung ohne Benutzer - er darf
        // weder zaehlen noch als Anmeldung gelten.
        var logonScreen = new Native.SessionInfo(2, Native.WtsConnectState.Active, false, "");
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, logonScreen);

        Thread.Sleep(100);
        engine.Tick(Tick);

        Assert.Equal(300, engine.Status().BalanceSeconds, 3);
        Assert.Null(engine.Status().SecondsUntilLogoff);
    }

    // ------------------------------------------------------------ Schonfrist

    [Fact]
    public void EmptyBalance_FreshLogin_GetsLoginGrace()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 0, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => r.SessionId = 7);

        engine.Tick(Tick);

        var status = engine.Status(7);
        Assert.NotNull(status.SecondsUntilLogoff);
        Assert.InRange(status.SecondsUntilLogoff!.Value, 110, 120);
        Assert.Equal(1, TestEnv.PersistedState().EmptyGraceRuns);
    }

    [Fact]
    public void EmptyBalance_GraceUsedUp_OnlySecondsRemain()
    {
        // Ab dem vierten Mal in Folge gibt es nur noch die Kurzfrist - das
        // Notfallfenster laesst sich nicht als Gratis-Kontingent melken.
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 0;
            s.EmptyGraceRuns = 3;
        }, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => r.SessionId = 7);

        engine.Tick(Tick);

        var status = engine.Status(7);
        Assert.NotNull(status.SecondsUntilLogoff);
        Assert.InRange(status.SecondsUntilLogoff!.Value, 0, 10);
    }

    [Fact]
    public void EmptyGraceRuns_ResetOnceBalanceReturns()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 0;
            s.EmptyGraceRuns = 3;
        }, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => r.SessionId = 7);
        engine.Tick(Tick);

        // Nachlegen beendet die Schonfrist, und mit Guthaben faellt der Zaehler
        // beim naechsten Tick auf null.
        engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 30);
        engine.Tick(Tick);
        Assert.Null(engine.Status(7).SecondsUntilLogoff);

        // Wieder leer: Die naechste Schonfrist ist die erste einer neuen Serie -
        // volle Laenge statt Kurzfrist, und genau so steht es im Bestand.
        engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = -31);
        engine.Tick(Tick);

        var status = engine.Status(7);
        Assert.NotNull(status.SecondsUntilLogoff);
        Assert.InRange(status.SecondsUntilLogoff!.Value, 60, 90);
        Assert.Equal(1, TestEnv.PersistedState().EmptyGraceRuns);
    }

    // -------------------------------------------------------------- Warnung

    [Fact]
    public void Warning_FiresWhenBalanceCrossesThreshold()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 5 * 60;
            s.Config.WarnMinutes = 10;
        }, TestEnv.User());
        engine.Handle(RequestType.Heartbeat, mutate: r => r.SessionId = 7);

        engine.Tick(Tick);

        Assert.Equal(10, engine.Status(7).WarningMinutes);
    }

    [Fact]
    public void Warning_NoSessions_DoesNotFire()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 5 * 60;
            s.Config.WarnMinutes = 10;
        });

        engine.Tick(Tick);

        Assert.Null(engine.Status().WarningMinutes);
    }

    // ------------------------------------------------------------- Passwort

    [Fact]
    public void WrongPassword_FiveTimes_LocksOut()
    {
        var engine = TestEnv.Engine();

        Response last = Response.Success();
        for (var i = 0; i < 5; i++)
            last = engine.Handle(RequestType.AddTime, "falsch-falsch", r => r.Minutes = 30);

        Assert.False(last.Ok);
        Assert.Contains("Locked", last.Message);

        // Waehrend der Sperre hilft auch das richtige Passwort nicht.
        var during = engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 30);
        Assert.False(during.Ok);
        Assert.Contains("Locked", during.Message);
    }

    [Fact]
    public void CorrectPassword_ResetsFailureCounter()
    {
        var engine = TestEnv.Engine();

        for (var i = 0; i < 4; i++)
            engine.Handle(RequestType.AddTime, "falsch-falsch", r => r.Minutes = 30);
        Assert.True(engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 1).Ok);

        // Der Zaehler beginnt von vorn - vier weitere Fehlversuche sperren nicht.
        for (var i = 0; i < 4; i++)
            engine.Handle(RequestType.AddTime, "falsch-falsch", r => r.Minutes = 30);
        Assert.True(engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 1).Ok);
    }

    [Fact]
    public void NoPasswordStored_NothingIsAuthorized()
    {
        // Wer die Zustandsdatei loescht, bekommt dadurch kein offenes Tor.
        var engine = TestEnv.Engine(s =>
        {
            s.PasswordHash = null;
            s.PasswordSalt = null;
            s.PasswordIterations = 0;
        });

        var response = engine.Handle(RequestType.AddTime, "irgendwas", r => r.Minutes = 30);

        Assert.False(response.Ok);
        Assert.Contains("No master password", response.Message);
    }

    // ------------------------------------------------------------ Nachlegen

    [Fact]
    public void AddTime_OverPerGoLimit_IsRejected()
    {
        var engine = TestEnv.Engine(s => s.Config.MaxManualGrantMinutes = 240);

        var response = engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 241);

        Assert.False(response.Ok);
        Assert.Contains("per go", response.Message);
    }

    [Fact]
    public void AddTime_ResetsEvolutionToStageOne()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 4 * 1800;
            s.EarnedSeconds = 4 * 1800; // Stufe 4 bei 30 min Tagesbudget
        });
        Assert.Equal(4, engine.Status().EvolutionStage);

        engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 10);

        // Dazugekauft ist nicht gespart.
        Assert.Equal(1, engine.Status().EvolutionStage);
    }

    [Fact]
    public void AddTime_NegativeMinutes_FloorsAtZero()
    {
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300);

        var response = engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = -30);

        Assert.True(response.Ok);
        Assert.Equal(0, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void AddTime_ZeroMinutes_IsRejectedWithoutPasswordCheck()
    {
        var engine = TestEnv.Engine();

        var response = engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 0);

        Assert.False(response.Ok);
    }

    // ---------------------------------------------------------------- Pause

    [Fact]
    public void Pause_IsGone_TheRequestIsUnknown()
    {
        // Die Pause wurde entfernt - ein Agent alter Fassung, der sie noch
        // anfragt, bekommt eine Absage statt einer stillen Wirkung.
        var engine = TestEnv.Engine();

        var response = engine.Handle("pause", TestEnv.Password, r => r.Minutes = 60);

        Assert.False(response.Ok);
        Assert.Contains("Unknown request", response.Message);
    }

    // -------------------------------------------------------- Einstellungen

    [Fact]
    public void SetConfig_ClampsValues_AndKeepsMaxManualGrant()
    {
        var engine = TestEnv.Engine(s => s.Config.MaxManualGrantMinutes = 120);

        var response = engine.Handle(RequestType.SetConfig, TestEnv.Password, r => r.Config = new GuardConfig
        {
            DailyGrantMinutes = 100_000,   // -> 1440
            CapMinutes = 1,                // -> mindestens Tagesbudget
            WarnMinutes = 0,               // -> 1
            GraceSeconds = 1,              // -> 10
            LoginGraceSeconds = 100_000,   // -> 3600
            MaxManualGrantMinutes = 100_000,
        });

        Assert.True(response.Ok);
        var config = engine.Status().Config!;
        Assert.Equal(1440, config.DailyGrantMinutes);
        Assert.Equal(1440, config.CapMinutes);
        Assert.Equal(1, config.WarnMinutes);
        Assert.Equal(10, config.GraceSeconds);
        Assert.Equal(3600, config.LoginGraceSeconds);

        // Das Pro-Vorgang-Limit wird bei der Installation festgelegt und laesst
        // sich hier nicht aufweichen.
        Assert.Equal(120, config.MaxManualGrantMinutes);
    }

    [Fact]
    public void ChangePassword_EnforcesMinimumLength()
    {
        var engine = TestEnv.Engine();

        var tooShort = engine.Handle(RequestType.ChangePassword, TestEnv.Password,
            r => r.NewPassword = new string('x', PasswordHash.MinimumLength - 1));

        Assert.False(tooShort.Ok);
        Assert.Contains($"{PasswordHash.MinimumLength}", tooShort.Message);
    }

    [Fact]
    public void ChangePassword_OldStopsWorking_NewWorks()
    {
        var engine = TestEnv.Engine();
        const string newPassword = "ein-ganz-neues-passwort";

        Assert.True(engine.Handle(RequestType.ChangePassword, TestEnv.Password,
            r => r.NewPassword = newPassword).Ok);

        Assert.False(engine.Handle(RequestType.AddTime, TestEnv.Password, r => r.Minutes = 1).Ok);
        Assert.True(engine.Handle(RequestType.AddTime, newPassword, r => r.Minutes = 1).Ok);
    }

    // ----------------------------------------------------------- Fernzugriff

    [Fact]
    public void RemoteAdd_CreditsBalance_AndResetsEvolution()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 3600;
            s.EarnedSeconds = 3600;
        });

        var results = engine.ApplyRemoteCommands([new RemoteCommand("c1", "add", 30)]);

        Assert.True(Assert.Single(results).Ok);
        Assert.Equal(3600 + 1800, engine.Status().BalanceSeconds, 1);
        Assert.Equal(1, engine.Status().EvolutionStage);
    }

    [Fact]
    public void RemoteAdd_KnowsNoPerGoLimit()
    {
        // Der Deckel des Passwort-Nachlegens gilt fuer den Freund nicht:
        // er ist eine gekoppelte Vertrauensrolle und gibt, was er will.
        var engine = TestEnv.Engine(s => s.Config.MaxManualGrantMinutes = 30);

        var results = engine.ApplyRemoteCommands([new RemoteCommand("c1", "add", 100_000)]);

        Assert.True(Assert.Single(results).Ok);
        Assert.Equal(100_000 * 60.0, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void RemoteCommand_SameIdTwice_RunsOnlyOnce()
    {
        var engine = TestEnv.Engine();

        var first = engine.ApplyRemoteCommands([new RemoteCommand("c1", "add", 30)]);
        var second = engine.ApplyRemoteCommands([new RemoteCommand("c1", "add", 30)]);

        Assert.True(Assert.Single(first).Ok);
        Assert.True(Assert.Single(second).Ok);
        Assert.Equal("Already done.", second[0].Message);
        Assert.Equal(1800, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void RemotePauseAndResume_AreRefusedWithAnExplanation()
    {
        // Ein noch nicht aktualisierter Worker kann sie weiterhin zustellen.
        var engine = TestEnv.Engine(s => s.BalanceSeconds = 300, TestEnv.User());

        var results = engine.ApplyRemoteCommands(
        [
            new RemoteCommand("p1", "pause", 60),
            new RemoteCommand("r1", "resume", 0),
        ]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Ok));
        Assert.All(results, r => Assert.Contains("removed", r.Message));

        // Und die Uhr laeuft unbeirrt weiter.
        Thread.Sleep(100);
        engine.Tick(Tick);
        Assert.True(engine.Status().BalanceSeconds < 300);
    }

    [Fact]
    public void RemoteCommand_UnknownOrInvalid_IsRejected()
    {
        var engine = TestEnv.Engine();

        var results = engine.ApplyRemoteCommands(
        [
            new RemoteCommand("u1", "unlock", 0),
            new RemoteCommand("u2", "add", 0),
            new RemoteCommand("u3", "add", -5),
            new RemoteCommand("", "add", 30),
            new RemoteCommand(new string('x', 65), "add", 30),
        ]);

        // Leere und ueberlange IDs werden still uebersprungen, der Rest quittiert.
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(r.Ok));
        Assert.Equal(0, engine.Status().BalanceSeconds, 1);
    }

    [Fact]
    public void RemoteCommandIds_EvictOldest_AfterSixtyFour()
    {
        var engine = TestEnv.Engine(s => s.Config.MaxManualGrantMinutes = 240);

        engine.ApplyRemoteCommands([new RemoteCommand("first", "add", 1)]);
        for (var i = 0; i < 64; i++)
            engine.ApplyRemoteCommands([new RemoteCommand($"filler-{i}", "add", 1)]);

        // "first" ist aus dem Gedaechtnis gefallen - der Schutz ist ein Ring,
        // kein ewiges Register. Der Worker loescht quittierte Befehle ohnehin.
        var again = engine.ApplyRemoteCommands([new RemoteCommand("first", "add", 1)]);
        Assert.NotEqual("Already done.", again[0].Message);
    }

    // ------------------------------------------------------------- Snapshot

    [Fact]
    public void Snapshot_CarriesEverythingTheWorkerNeeds()
    {
        var engine = TestEnv.Engine(s =>
        {
            s.BalanceSeconds = 1234;
            s.EarnedSeconds = 600;
            s.LastAccrualDate = new DateOnly(2026, 8, 10);
            s.Config.DailyGrantMinutes = 30;
            s.Config.CapMinutes = 240;
            s.Config.MaxManualGrantMinutes = 120;
        });

        var snapshot = engine.BuildTelegramSnapshot();

        Assert.Equal(1234, snapshot.BalanceSeconds, 1);
        Assert.Equal(600, snapshot.EarnedSeconds, 1);
        Assert.Equal(30, snapshot.DailyGrantMinutes);
        Assert.Equal(240, snapshot.CapMinutes);
        Assert.Equal("2026-08-10", snapshot.LastAccrualDate);
        Assert.Equal((int)DateTimeOffset.Now.Offset.TotalMinutes, snapshot.TzOffsetMinutes);
        Assert.False(snapshot.Counting);
        Assert.True(Math.Abs(snapshot.SavedAtUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 5_000);
    }

    [Fact]
    public void Status_ReflectsTelegramLink()
    {
        var engine = TestEnv.Engine();
        var accountId = new string('a', 32);

        engine.SetTelegram(true, "https://monkey-telegram-x.example.workers.dev", "chiffrat",
            managed: true, cloudflareAccountId: accountId,
            scriptName: "monkey-telegram-x", kvNamespaceId: "kv1", workerVersion: 2);
        engine.ReportTelegramSync(false, "boom");

        var status = engine.Status();
        Assert.True(status.TelegramEnabled);
        Assert.Equal("monkey-telegram-x.example.workers.dev", status.TelegramWorkerHost);
        Assert.True(status.TelegramWorkerManaged);
        Assert.Equal(2, status.TelegramWorkerVersion);
        Assert.Equal(accountId, status.TelegramCloudflareAccountId);
        Assert.Equal("boom", status.TelegramLastError);
        Assert.Null(status.TelegramLastSyncSecondsAgo);

        engine.ReportTelegramSync(true, null);
        status = engine.Status();
        Assert.Null(status.TelegramLastError);
        Assert.NotNull(status.TelegramLastSyncSecondsAgo);

        // Trennen raeumt alle Metadaten weg.
        engine.SetTelegram(false, null, null);
        status = engine.Status();
        Assert.False(status.TelegramEnabled);
        Assert.Null(status.TelegramWorkerHost);
        Assert.False(status.TelegramWorkerManaged);
        Assert.Null(status.TelegramWorkerVersion);
    }

    [Fact]
    public void UnknownRequestType_Fails()
    {
        var engine = TestEnv.Engine();

        Assert.False(engine.Handle("gibtsnicht").Ok);
    }
}
