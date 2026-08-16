using Monkey.Service;
using Xunit;

namespace Monkey.Tests;

public sealed class TrustedClockTests
{
    [Fact]
    public void Start_WallClockBehindPersisted_KeepsPersistedAndCountsTamper()
    {
        // Zwischen zwei Dienststarts wurde die Uhr zurueckgestellt: der alte
        // Stand gewinnt, sonst waere der Rueckwaertssprung ein Gratis-Tag.
        var future = DateTimeOffset.Now.AddHours(6);
        var clock = new TrustedClock(future, priorTamperEvents: 2);

        Assert.Equal(3, clock.TamperEvents);
        Assert.True(clock.Now >= future);
    }

    [Fact]
    public void Start_WallClockAhead_UsesWallClock()
    {
        var past = DateTimeOffset.Now.AddHours(-6);
        var clock = new TrustedClock(past, priorTamperEvents: 0);

        Assert.Equal(0, clock.TamperEvents);
        Assert.True((DateTimeOffset.Now - clock.Now).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Advance_MovesForward_AndCapsAwakeTime()
    {
        var clock = new TrustedClock(null, 0);
        var before = clock.Now;

        Thread.Sleep(60);
        var (elapsed, awake) = clock.Advance(awakeCap: TimeSpan.FromMilliseconds(20));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(40), $"elapsed was {elapsed}");
        Assert.True(awake <= TimeSpan.FromMilliseconds(20), $"awake was {awake}");
        Assert.True(clock.Now >= before);
        Assert.Equal(0, clock.TamperEvents);
    }

    [Fact]
    public void Advance_AwakeNeverExceedsElapsedByMuch()
    {
        var clock = new TrustedClock(null, 0);

        Thread.Sleep(30);
        var (_, awake) = clock.Advance(awakeCap: TimeSpan.FromSeconds(10));

        // Wache Zeit kann nicht groesser sein als die Echtzeit seit Start (plus
        // Messrauschen der beiden Zaehler).
        Assert.True(awake < TimeSpan.FromSeconds(5), $"awake was {awake}");
    }
}
