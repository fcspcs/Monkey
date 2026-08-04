namespace TimeGuard.Service;

/// <summary>
/// Vom Dienst gefuehrte Uhr, die nicht an der Systemzeit haengt.
///
/// Der Fortschritt kommt aus Environment.TickCount64 (Zeit seit Systemstart,
/// Schlafphasen eingeschlossen). Die Systemzeit wird nur uebernommen, solange sie
/// dazu passt. Springt sie weiter, als der Tickzaehler es hergibt, wurde an der
/// Uhr gedreht: der Sprung wird verworfen und protokolliert.
///
/// Damit laesst sich ein Tagesbudget nicht dadurch erneuern, dass man das Datum
/// vorstellt.
/// </summary>
internal sealed class TrustedClock
{
    private const double ToleranceSeconds = 120;

    private long _lastTick;
    private ulong _lastUnbiased;

    public DateTimeOffset Now { get; private set; }
    public int TamperEvents { get; private set; }

    public TrustedClock(DateTimeOffset? persisted, int priorTamperEvents)
    {
        TamperEvents = priorTamperEvents;

        var wall = DateTimeOffset.Now;
        if (persisted is { } previous && wall < previous)
        {
            // Zwischen zwei Dienststarts ist die Uhr zurueckgestellt worden.
            // Der alte Stand gewinnt, sonst waere ein Rueckwaertssprung gratis.
            Now = previous;
            TamperEvents++;
        }
        else
        {
            Now = wall;
        }

        _lastTick = Environment.TickCount64;
        _lastUnbiased = Native.GetUnbiasedInterruptTime();
    }

    /// <summary>
    /// Uhr weiterstellen.
    /// </summary>
    /// <returns>
    /// Awake: tatsaechlich wache Zeit seit dem letzten Aufruf. Nur diese wird vom
    /// Guthaben abgezogen, damit ein Ruhezustand kein Kontingent verbrennt.
    /// </returns>
    public (TimeSpan Elapsed, TimeSpan Awake) Advance(TimeSpan awakeCap)
    {
        var tick = Environment.TickCount64;
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, tick - _lastTick));
        _lastTick = tick;

        var unbiased = Native.GetUnbiasedInterruptTime();
        var awakeTicks = unbiased > _lastUnbiased ? (long)(unbiased - _lastUnbiased) : 0L;
        _lastUnbiased = unbiased;

        var awake = TimeSpan.FromTicks(awakeTicks);
        if (awake > awakeCap) awake = awakeCap;

        var expected = Now + elapsed;
        var wall = DateTimeOffset.Now;

        if (Math.Abs((wall - expected).TotalSeconds) > ToleranceSeconds)
        {
            TamperEvents++;
            Now = expected;
        }
        else
        {
            Now = wall;
        }

        return (elapsed, awake);
    }
}
