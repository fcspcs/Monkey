using Monkey.Core;
using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

/// <summary>
/// Der Knopf "Check for updates now" im Master-Fenster. Die Pruefung selbst
/// redet mit GitHub und laeuft im Update-Pruefer; hier steht der Teil, den der
/// Dienst entscheidet: wer darf ausloesen, und was sieht das Fenster danach.
/// </summary>
public sealed class UpdateCheckTests
{
    [Fact]
    public void UpdateCheck_WithoutPassword_IsRejected()
    {
        var engine = TestEnv.Engine();

        var response = engine.Handle(RequestType.UpdateCheck);

        Assert.False(response.Ok);
        Assert.Equal(0, engine.UpdateKick.CurrentCount);
        Assert.False(engine.Status().UpdateCheckRunning);
    }

    [Fact]
    public void UpdateCheck_WithWrongPassword_IsRejected()
    {
        var engine = TestEnv.Engine();

        var response = engine.Handle(RequestType.UpdateCheck, "not-the-password");

        Assert.False(response.Ok);
        Assert.Equal(0, engine.UpdateKick.CurrentCount);
    }

    [Fact]
    public void UpdateCheck_WithPassword_WakesTheChecker()
    {
        var engine = TestEnv.Engine();

        var response = engine.Handle(RequestType.UpdateCheck, TestEnv.Password);

        Assert.True(response.Ok);
        Assert.Equal(1, engine.UpdateKick.CurrentCount);
        Assert.True(engine.Status().UpdateCheckRunning);
    }

    [Fact]
    public void UpdateCheck_WhileOneIsRunning_DoesNotQueueASecond()
    {
        // Zwei Weckrufe wuerden denselben Installer zweimal in denselben Ordner
        // laden. Der zweite Klick darf deshalb nichts nachlegen.
        var engine = TestEnv.Engine();

        engine.Handle(RequestType.UpdateCheck, TestEnv.Password);
        var second = engine.Handle(RequestType.UpdateCheck, TestEnv.Password);

        Assert.True(second.Ok);
        Assert.Equal(1, engine.UpdateKick.CurrentCount);
        Assert.True(engine.Status().UpdateCheckRunning);
    }

    [Fact]
    public void ReportUpdateCheck_EndsTheWaitAndShowsTheResult()
    {
        var engine = TestEnv.Engine();
        engine.Handle(RequestType.UpdateCheck, TestEnv.Password);

        engine.ReportUpdateCheck("Already up to date - v9.9.9 is the newest release.");

        var status = engine.Status();
        Assert.False(status.UpdateCheckRunning);
        Assert.Equal("Already up to date - v9.9.9 is the newest release.", status.UpdateLastResult);
        Assert.NotNull(status.UpdateLastCheckSecondsAgo);
    }

    [Fact]
    public void UpdateCheck_AfterAResult_CanRunAgain()
    {
        var engine = TestEnv.Engine();
        engine.Handle(RequestType.UpdateCheck, TestEnv.Password);
        engine.ReportUpdateCheck("The check failed: no network.");

        // Den ersten Weckruf hat der Pruefer im echten Betrieb abgeholt.
        engine.UpdateKick.Wait(0);

        var again = engine.Handle(RequestType.UpdateCheck, TestEnv.Password);

        Assert.True(again.Ok);
        Assert.Equal(1, engine.UpdateKick.CurrentCount);

        // Das alte Ergebnis verschwindet, sonst stuende die Antwort von vorhin
        // neben "wird geprueft".
        var status = engine.Status();
        Assert.True(status.UpdateCheckRunning);
        Assert.Null(status.UpdateLastResult);
    }

    [Fact]
    public void MarkUpdateCheckRunning_ShowsABackgroundCheckToo()
    {
        // Der Sechs-Stunden-Takt weckt den Pruefer ohne Zutun des Fensters.
        var engine = TestEnv.Engine();

        engine.MarkUpdateCheckRunning();

        Assert.True(engine.Status().UpdateCheckRunning);
        Assert.Equal(0, engine.UpdateKick.CurrentCount);
    }

    [Fact]
    public void Status_BeforeAnyCheck_HasNoResult()
    {
        var status = TestEnv.Engine().Status();

        Assert.False(status.UpdateCheckRunning);
        Assert.Null(status.UpdateLastResult);
        Assert.Null(status.UpdateLastCheckSecondsAgo);
    }
}
