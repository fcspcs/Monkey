using TimeGuard.Core;

namespace TimeGuard.Service;

/// <summary>
/// Die gesamte Entscheidungslogik. Der Agent zeigt nur an und fragt an - hier faellt
/// jede Entscheidung, hier wird das Passwort geprueft. Alles unter einem Lock, weil
/// Tick-Schleife und Pipe-Anfragen nebenlaeufig hereinkommen.
/// </summary>
internal sealed class GuardEngine
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Wie lange eine ausgeloeste Warnung im Status stehen bleibt.</summary>
    private static readonly TimeSpan WarningVisibility = TimeSpan.FromSeconds(25);

    private readonly object _gate = new();
    private readonly StateStore _store = new();
    private readonly GuardState _state;
    private readonly TrustedClock _clock;

    private readonly Dictionary<int, AgentReport> _agents = new();

    /// <summary>Angerechnete Zeit je Sitzung, seit diese sich angemeldet hat.</summary>
    private readonly Dictionary<int, double> _sessionElapsed = new();

    private HashSet<int> _knownSessions = [];

    private DateTimeOffset? _zeroSince;
    private TimeSpan _activeGrace;
    private DateTimeOffset _lastSave;
    private double _previousRemainingMinutes = double.MaxValue;

    private int? _activeWarning;
    private DateTimeOffset _warningIssuedAt;

    private int _failedAttempts;
    private DateTimeOffset _lockoutUntil = DateTimeOffset.MinValue;

    private readonly record struct AgentReport(DateTimeOffset LastSeen, bool ScreensaverRunning);

    public GuardEngine()
    {
        _state = _store.Load();
        _clock = new TrustedClock(_state.TrustedNow == default ? null : _state.TrustedNow, _state.ClockTamperEvents);
        _lastSave = _clock.Now;
        _activeGrace = TimeSpan.FromSeconds(_state.Config.GraceSeconds);

        Log.Write($"Dienst gestartet. Guthaben {Format(_state.BalanceSeconds)}, " +
                  $"Tagesbudget {_state.Config.DailyGrantMinutes} min, Deckel {_state.Config.CapMinutes} min, " +
                  $"Passwort {(_state.HasPassword ? "gesetzt" : "FEHLT")}.");
    }

    // ---------------------------------------------------------------- Tick

    public void Tick(TimeSpan interval)
    {
        lock (_gate)
        {
            // Doppelte Tickdauer als Obergrenze: nach einer Verzoegerung soll nicht
            // rueckwirkend ein grosser Block abgezogen werden.
            var (_, awake) = _clock.Advance(interval * 2);

            _state.TrustedNow = _clock.Now;
            _state.ClockTamperEvents = _clock.TamperEvents;

            Accrue();

            // Einmal pro Tick abfragen, das Ergebnis wandert durch alle Schritte.
            var all = Native.EnumerateSessions();
            var active = all.Where(s => s.State == Native.WtsConnectState.Active)
                            .Select(s => s.SessionId).ToHashSet();

            var freshLogin = active.Except(_knownSessions).Any();
            _knownSessions = active;

            foreach (var gone in _sessionElapsed.Keys.Where(id => !active.Contains(id)).ToList())
                _sessionElapsed.Remove(gone);

            var paused = IsPaused();
            var counting = CountingSessions(all);

            if (!paused && counting.Count > 0)
            {
                _state.BalanceSeconds -= awake.TotalSeconds;
                foreach (var session in counting)
                    _sessionElapsed[session] = _sessionElapsed.GetValueOrDefault(session) + awake.TotalSeconds;
            }

            Enforce(paused, counting, freshLogin);

            if (_clock.Now - _lastSave >= SaveInterval)
            {
                _store.Save(_state);
                _lastSave = _clock.Now;
            }
        }
    }

    /// <summary>
    /// Tagesgutschrift mit Uebertrag - das Feature, wegen dem das Ganze existiert.
    /// Nicht verbrauchte Zeit bleibt liegen und wird am naechsten Tag aufgestockt,
    /// bis zum Deckel.
    /// </summary>
    private void Accrue()
    {
        var today = DateOnly.FromDateTime(_clock.Now.LocalDateTime);

        if (_state.LastAccrualDate is not { } last)
        {
            _state.LastAccrualDate = today;
            Grant();
            Log.Write($"Erstgutschrift. Guthaben {Format(_state.BalanceSeconds)}.");
            return;
        }

        if (last >= today) return;

        var days = 0;
        while (last < today && days < 400)
        {
            last = last.AddDays(1);
            Grant();
            days++;
        }

        _state.LastAccrualDate = today;
        Log.Write($"Tageswechsel: {days} Tag(e) gutgeschrieben, Guthaben {Format(_state.BalanceSeconds)}.");
    }

    private void Grant()
    {
        var cap = _state.Config.CapMinutes * 60.0;
        var grant = _state.Config.DailyGrantMinutes * 60.0;

        // Liegt das Guthaben durch eine manuelle Gutschrift bereits ueber dem Deckel,
        // wird es nicht gekuerzt - nur nicht weiter erhoeht.
        if (_state.BalanceSeconds >= cap) return;
        _state.BalanceSeconds = Math.Min(_state.BalanceSeconds + grant, cap);
    }

    private bool IsPaused()
    {
        if (_state.PauseUntil is not { } until) return false;
        if (_clock.Now < until) return true;

        _state.PauseUntil = null;
        Log.Write("Master-Pause abgelaufen, Kontrolle wieder aktiv.");
        return false;
    }

    /// <summary>
    /// Sitzungen, in denen gerade jemand sitzt. Gesperrte Sitzungen und laufender
    /// Bildschirmschoner zaehlen nicht - alles andere schon, auch reines Zuschauen.
    /// </summary>
    private List<int> CountingSessions(List<Native.SessionInfo> sessions)
    {
        var result = new List<int>();

        foreach (var session in sessions)
        {
            if (session.State != Native.WtsConnectState.Active) continue;
            if (session.Locked && _state.Config.PauseOnLock) continue;

            if (_state.Config.PauseOnScreensaver
                && _agents.TryGetValue(session.SessionId, out var report)
                && _clock.Now - report.LastSeen < AgentTimeout
                && report.ScreensaverRunning)
                continue;

            result.Add(session.SessionId);
        }

        return result;
    }

    private void Enforce(bool paused, List<int> sessions, bool freshLogin)
    {
        if (paused || sessions.Count == 0)
        {
            _zeroSince = null;
            _previousRemainingMinutes = double.MaxValue;
            return;
        }

        if (_state.BalanceSeconds > 0)
        {
            _zeroSince = null;
            WarnOnThreshold(sessions);
            return;
        }

        if (_zeroSince is null)
        {
            _zeroSince = _clock.Now;

            // Wer sich mit leerem Konto gerade erst angemeldet hat, bekommt die
            // laengere Frist - das ist das Notfallfenster fuer das Master-Passwort.
            _activeGrace = TimeSpan.FromSeconds(freshLogin
                ? _state.Config.LoginGraceSeconds
                : _state.Config.GraceSeconds);

            Log.Write($"Guthaben leer. Abmeldung in {_activeGrace.TotalSeconds:0} s" +
                      $"{(freshLogin ? " (Anmeldung mit leerem Konto)" : string.Empty)}.");
        }

        _previousRemainingMinutes = double.MaxValue;

        var elapsed = _clock.Now - _zeroSince.Value;
        if (elapsed < _activeGrace)
        {
            var left = (int)(_activeGrace - elapsed).TotalSeconds;
            foreach (var session in sessions.Where(NoLiveAgent))
                Native.SendMessage(session, "TimeGuard",
                    $"Das Zeitkontingent ist aufgebraucht.\n\nAbmeldung in {left} Sekunden.\n\n" +
                    "Bitte jetzt alles speichern.", Math.Max(5, left));
            return;
        }

        _store.Save(_state);
        _lastSave = _clock.Now;

        foreach (var session in sessions)
        {
            if (RunMode.DryRunLogoff)
            {
                Log.Write($"[Trockenlauf] Sitzung {session} wuerde jetzt abgemeldet.");
                continue;
            }

            Log.Write($"Kontingent aufgebraucht - melde Sitzung {session} ab.");
            if (!Native.LogoffSession(session))
                Log.Write($"Abmeldung der Sitzung {session} fehlgeschlagen (Win32 {LastError()}).");
        }

        _zeroSince = null;
    }

    private static int LastError() => System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    private void WarnOnThreshold(List<int> sessions)
    {
        var remaining = _state.BalanceSeconds / 60.0;

        foreach (var threshold in _state.Config.WarnAtMinutes.OrderByDescending(x => x))
        {
            if (_previousRemainingMinutes > threshold && remaining <= threshold)
            {
                _activeWarning = threshold;
                _warningIssuedAt = _clock.Now;

                // Ohne laufenden Agent gibt es kein Warnfenster - dann der
                // Systemdialog als Rueckfallebene.
                foreach (var session in sessions.Where(NoLiveAgent))
                    Native.SendMessage(session, "TimeGuard",
                        $"Noch {threshold} Minute(n) Computerzeit.");

                Log.Write($"Warnschwelle {threshold} min erreicht.");
                break;
            }
        }

        _previousRemainingMinutes = remaining;
    }

    private bool NoLiveAgent(int sessionId) =>
        !_agents.TryGetValue(sessionId, out var report) || _clock.Now - report.LastSeen >= AgentTimeout;

    // ------------------------------------------------------------ Anfragen

    public Response Handle(Request request)
    {
        lock (_gate)
        {
            switch (request.Type)
            {
                case RequestType.Status:
                    return Response.Success(status: BuildStatus(request.SessionId));

                case RequestType.Heartbeat:
                    _agents[request.SessionId] = new AgentReport(_clock.Now, request.ScreensaverRunning);
                    return Response.Success(status: BuildStatus(request.SessionId));

                case RequestType.Pause:
                    return HandlePause(request);

                case RequestType.Resume:
                    return WithPassword(request.Password, () =>
                    {
                        _state.PauseUntil = null;
                        _store.Save(_state);
                        Log.Write("Master-Pause vorzeitig beendet.");
                        return Response.Success("Kontrolle wieder aktiv.", BuildStatus(request.SessionId));
                    });

                case RequestType.AddTime:
                    return HandleAddTime(request);

                case RequestType.SetConfig:
                    return HandleSetConfig(request);

                case RequestType.ChangePassword:
                    return HandleChangePassword(request);

                case RequestType.Unlock:
                    return WithPassword(request.Password, () =>
                    {
                        Log.Write("Autorisierter Teardown angefordert - Sperren werden entfernt.");
                        SelfProtect.Teardown();
                        return Response.Success(
                            "Alle Sperren entfernt. Der Dienst lässt sich jetzt stoppen und entfernen.");
                    });

                default:
                    return Response.Fail($"Unbekannte Anfrage '{request.Type}'.");
            }
        }
    }

    private Response HandlePause(Request request)
    {
        var minutes = Math.Clamp(request.Minutes, 1, _state.Config.MaxPauseMinutes);
        return WithPassword(request.Password, () =>
        {
            _state.PauseUntil = _clock.Now.AddMinutes(minutes);
            _zeroSince = null;
            _store.Save(_state);
            Log.Write($"Master-Pause fuer {minutes} min bis {_state.PauseUntil:HH:mm}.");
            return Response.Success($"Kontrolle pausiert bis {_state.PauseUntil:HH:mm} Uhr.",
                BuildStatus(request.SessionId));
        });
    }

    /// <summary>
    /// Zeit nachlegen. Beliebig oft moeglich, aber je Vorgang hoechstens der in den
    /// Einstellungen gesetzte Betrag. Abziehen geht unbegrenzt.
    /// </summary>
    private Response HandleAddTime(Request request)
    {
        if (request.Minutes == 0) return Response.Fail("Keine Minutenzahl angegeben.");

        return WithPassword(request.Password, () =>
        {
            if (request.Minutes > _state.Config.MaxManualGrantMinutes)
                return Response.Fail(
                    $"Pro Vorgang lassen sich höchstens {FormatMinutes(_state.Config.MaxManualGrantMinutes)} " +
                    "nachlegen. Für mehr den Vorgang einfach wiederholen.");

            _state.BalanceSeconds = Math.Max(0, _state.BalanceSeconds + request.Minutes * 60.0);
            _zeroSince = null;
            _previousRemainingMinutes = double.MaxValue;
            _store.Save(_state);

            Log.Write($"Guthaben manuell um {request.Minutes:+#;-#;0} min geaendert, neu {Format(_state.BalanceSeconds)}.");

            return Response.Success($"Guthaben jetzt {Format(_state.BalanceSeconds)}.",
                BuildStatus(request.SessionId));
        });
    }

    private Response HandleSetConfig(Request request)
    {
        if (request.Config is not { } incoming) return Response.Fail("Keine Einstellungen übergeben.");
        return WithPassword(request.Password, () =>
        {
            var config = _state.Config;
            config.DailyGrantMinutes = Math.Clamp(incoming.DailyGrantMinutes, 0, 24 * 60);
            config.CapMinutes = Math.Clamp(incoming.CapMinutes, config.DailyGrantMinutes, 100 * 24 * 60);
            config.GraceSeconds = Math.Clamp(incoming.GraceSeconds, 10, 3600);
            config.LoginGraceSeconds = Math.Clamp(incoming.LoginGraceSeconds, 10, 3600);
            config.MaxManualGrantMinutes = Math.Clamp(incoming.MaxManualGrantMinutes, 0, 24 * 60);
            config.MaxPauseMinutes = Math.Clamp(incoming.MaxPauseMinutes, 1, 30 * 24 * 60);
            config.PauseOnLock = incoming.PauseOnLock;
            config.PauseOnScreensaver = incoming.PauseOnScreensaver;

            if (incoming.WarnAtMinutes.Length > 0)
                config.WarnAtMinutes = incoming.WarnAtMinutes
                    .Where(x => x is > 0 and <= 24 * 60).Distinct().OrderByDescending(x => x).ToArray();

            _store.Save(_state);
            Log.Write($"Einstellungen geaendert: {config.DailyGrantMinutes} min/Tag, Deckel {config.CapMinutes} min, " +
                      $"Warnung bei {string.Join("/", config.WarnAtMinutes)} min.");
            return Response.Success("Einstellungen gespeichert.", BuildStatus(request.SessionId));
        });
    }

    private Response HandleChangePassword(Request request)
    {
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 4)
            return Response.Fail("Das neue Passwort muss mindestens 4 Zeichen haben.");

        return WithPassword(request.Password, () =>
        {
            var (hash, salt, iterations) = PasswordHash.Create(request.NewPassword);
            _state.PasswordHash = hash;
            _state.PasswordSalt = salt;
            _state.PasswordIterations = iterations;
            _store.Save(_state);
            Log.Write("Master-Passwort geaendert.");
            return Response.Success("Master-Passwort geändert.", BuildStatus(request.SessionId));
        });
    }

    /// <summary>
    /// Passwortpruefung mit Drossel. Ohne hinterlegtes Passwort wird nichts
    /// freigegeben - sonst waere das Loeschen der Zustandsdatei ein Freifahrtschein.
    /// </summary>
    private Response WithPassword(string? password, Func<Response> action)
    {
        if (!_state.HasPassword)
            return Response.Fail("Es ist kein Master-Passwort hinterlegt. " +
                                 "Neu setzen mit 'TimeGuardService.exe init' als Administrator.");

        if (_clock.Now < _lockoutUntil)
        {
            var seconds = (int)(_lockoutUntil - _clock.Now).TotalSeconds;
            return Response.Fail($"Zu viele Fehlversuche. Noch {seconds} Sekunden gesperrt.");
        }

        if (string.IsNullOrEmpty(password) ||
            !PasswordHash.Verify(password, _state.PasswordHash, _state.PasswordSalt, _state.PasswordIterations))
        {
            _failedAttempts++;
            Log.Write($"Falsches Master-Passwort (Versuch {_failedAttempts}).");

            if (_failedAttempts >= MaxFailedAttempts)
            {
                _lockoutUntil = _clock.Now + LockoutDuration;
                _failedAttempts = 0;
                return Response.Fail($"Zu viele Fehlversuche. {LockoutDuration.TotalSeconds:0} Sekunden gesperrt.");
            }

            return Response.Fail("Falsches Master-Passwort.");
        }

        _failedAttempts = 0;
        return action();
    }

    private StatusDto BuildStatus(int sessionId)
    {
        var paused = _state.PauseUntil is { } until && _clock.Now < until;

        double? untilLogoff = null;
        if (_zeroSince is { } since && !paused)
        {
            var left = _activeGrace - (_clock.Now - since);
            untilLogoff = Math.Max(0, left.TotalSeconds);
        }

        return new StatusDto
        {
            BalanceSeconds = Math.Max(0, _state.BalanceSeconds),
            SessionElapsedSeconds = _sessionElapsed.GetValueOrDefault(sessionId),
            Paused = paused,
            PauseUntil = paused ? _state.PauseUntil : null,
            Counting = !paused && CountingSessions(Native.EnumerateSessions()).Count > 0,
            SecondsUntilLogoff = untilLogoff,
            WarningMinutes = _activeWarning is { } warning && _clock.Now - _warningIssuedAt < WarningVisibility
                ? warning
                : null,
            MaxManualGrantMinutes = _state.Config.MaxManualGrantMinutes,
            DailyGrantMinutes = _state.Config.DailyGrantMinutes,
            CapMinutes = _state.Config.CapMinutes,
            ClockTamperEvents = _state.ClockTamperEvents,
            PasswordConfigured = _state.HasPassword,
            Config = _state.Config.Clone(),
        };
    }

    public void Flush()
    {
        lock (_gate)
        {
            _state.TrustedNow = _clock.Now;
            _store.Save(_state);
            Log.Write($"Dienst beendet. Guthaben {Format(_state.BalanceSeconds)}.");
        }
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string FormatMinutes(int minutes) =>
        minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} min";
}
