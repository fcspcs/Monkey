using System.Globalization;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>Lesekopie der Telegram-Einstellungen fuer den Sync-Dienst.</summary>
public sealed record TelegramConfigView(bool Enabled, string? WorkerUrl, string? SyncSecretProtected);

/// <summary>
/// Die gesamte Entscheidungslogik. Der Agent zeigt nur an und fragt an - hier faellt
/// jede Entscheidung, hier wird das Passwort geprueft. Alles unter einem Lock, weil
/// Tick-Schleife und Pipe-Anfragen nebenlaeufig hereinkommen.
/// </summary>
internal sealed class GuardEngine
{
    private const int MaxFailedAttempts = 5;

    /// <summary>
    /// So viele Schonfristen in Folge gibt es bei leerem Konto in voller Laenge.
    /// Danach greift die Kurzfrist - genug, um die Abmeldung kommen zu sehen,
    /// zu knapp, um damit zu arbeiten.
    /// </summary>
    private const int MaxEmptyGraceRuns = 3;
    private const int ShortEmergencyGraceSeconds = 10;

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

    /// <summary>
    /// Sitzungen, die in der laufenden Schonfrist bereits den Systemdialog bekommen
    /// haben. Ohne das schickt jeder Tick einen neuen: bei zwei Minuten Frist waeren
    /// das zwei Dutzend Fenster, die sich stapeln und einzeln weggeklickt werden
    /// muessen. Mit dem Ende der Schonfrist faellt der Vermerk wieder weg.
    /// </summary>
    private readonly HashSet<int> _graceNotified = [];
    private DateTimeOffset _lastSave;
    private double _previousRemainingMinutes = double.MaxValue;

    private int? _activeWarning;
    private DateTimeOffset _warningIssuedAt;

    private int _failedAttempts;
    private DateTimeOffset _lockoutUntil = DateTimeOffset.MinValue;

    private DateTimeOffset? _lastTelegramSync;
    private string? _telegramError;

    /// <summary>
    /// Weckt den Telegram-Abgleich, sobald sich am Konto etwas geaendert hat -
    /// dann steht der neue Stand binnen Sekunden beim Worker statt erst im
    /// naechsten Takt.
    /// </summary>
    public SemaphoreSlim TelegramKick { get; } = new(0, 1);

    private readonly record struct AgentReport(DateTimeOffset LastSeen, bool ScreensaverRunning, bool DisplayOff);

    public GuardEngine()
    {
        _state = _store.Load();
        _clock = new TrustedClock(_state.TrustedNow == default ? null : _state.TrustedNow, _state.ClockTamperEvents);
        _lastSave = _clock.Now;
        _activeGrace = TimeSpan.FromSeconds(_state.Config.GraceSeconds);

        Log.Write($"Service started. Balance {Format(_state.BalanceSeconds)}, " +
                  $"per day {_state.Config.DailyGrantMinutes} min, cap {_state.Config.CapMinutes} min, " +
                  $"password {(_state.HasPassword ? "set" : "MISSING")}.");
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
            // Nur Sitzungen mit angemeldetem Benutzer. Der Anmeldebildschirm ist
            // ebenfalls eine aktive Sitzung - wuerde er mitzaehlen, gaelte jede
            // Abmeldung als Anmeldung und verbrauchte danach munter Guthaben.
            var active = all.Where(s => s.State == Native.WtsConnectState.Active && s.HasUser)
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

                // Verbrauchte Zeit zehrt auch am Ersparten - der Affe schrumpft
                // wieder, wenn das Gesparte aufgebraucht wird.
                _state.EarnedSeconds -= awake.TotalSeconds;

                foreach (var session in counting)
                    _sessionElapsed[session] = _sessionElapsed.GetValueOrDefault(session) + awake.TotalSeconds;
            }

            ClampEarned();

            // Wieder Guthaben da (Tagesgutschrift oder Nachlegen): die Zaehlung
            // der Leerlauf-Schonfristen beginnt von vorn.
            if (_state.BalanceSeconds > 0) _state.EmptyGraceRuns = 0;

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
            Log.Write($"First top-up. Balance {Format(_state.BalanceSeconds)}.");
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
        Log.Write($"New day: {days} day(s) credited, balance {Format(_state.BalanceSeconds)}.");
        KickTelegram();
    }

    private void Grant()
    {
        var cap = _state.Config.CapMinutes * 60.0;
        var grant = _state.Config.DailyGrantMinutes * 60.0;

        // Liegt das Guthaben durch eine manuelle Gutschrift bereits ueber dem Deckel,
        // wird es nicht gekuerzt - nur nicht weiter erhoeht.
        if (_state.BalanceSeconds >= cap) return;

        var before = _state.BalanceSeconds;
        _state.BalanceSeconds = Math.Min(_state.BalanceSeconds + grant, cap);

        // Nur der tatsaechlich gutgeschriebene Teil zaehlt als erspart.
        _state.EarnedSeconds += _state.BalanceSeconds - before;
        ClampEarned();
    }

    /// <summary>
    /// Das Ersparte kann nie groesser sein als das Guthaben, aus dem es stammt -
    /// und nie groesser als der Deckel.
    /// </summary>
    private void ClampEarned()
    {
        var cap = _state.Config.CapMinutes * 60.0;
        _state.EarnedSeconds = Math.Clamp(_state.EarnedSeconds, 0, Math.Max(0, Math.Min(_state.BalanceSeconds, cap)));
    }

    private bool IsPaused()
    {
        if (_state.PauseUntil is not { } until) return false;
        if (_clock.Now < until) return true;

        _state.PauseUntil = null;
        Log.Write("Pause expired, the limit is active again.");
        return false;
    }

    /// <summary>
    /// Sitzungen, in denen gerade jemand sitzt. Gesperrte Sitzungen, laufender
    /// Bildschirmschoner und ausgeschalteter Bildschirm zaehlen nicht - alles
    /// andere schon, auch reines Zuschauen. Der abgeschaltete Monitor steht dem
    /// Schoner gleich, weil modernes Windows meist gar keinen Schoner mehr
    /// startet, sondern die Anzeige einfach ausknipst.
    /// </summary>
    private List<int> CountingSessions(List<Native.SessionInfo> sessions)
    {
        var result = new List<int>();

        foreach (var session in sessions)
        {
            if (session.State != Native.WtsConnectState.Active) continue;

            // Ohne angemeldeten Benutzer sitzt dort niemand - das ist der
            // Anmeldebildschirm nach dem Abmelden. Er gilt nicht als gesperrt,
            // und ein Agent, der einen Bildschirmschoner melden koennte, laeuft
            // dort auch nicht. Ohne diese Pruefung liefe das Guthaben weiter.
            if (!session.HasUser) continue;

            if (session.Locked && _state.Config.PauseOnLock) continue;

            if (_state.Config.PauseOnScreensaver
                && _agents.TryGetValue(session.SessionId, out var report)
                && _clock.Now - report.LastSeen < AgentTimeout
                && (report.ScreensaverRunning || report.DisplayOff))
                continue;

            result.Add(session.SessionId);
        }

        return result;
    }

    private void Enforce(bool paused, List<int> sessions, bool freshLogin)
    {
        if (paused || sessions.Count == 0)
        {
            ResetGrace();
            _previousRemainingMinutes = double.MaxValue;
            return;
        }

        if (_state.BalanceSeconds > 0)
        {
            ResetGrace();
            WarnOnThreshold(sessions);
            return;
        }

        if (_zeroSince is null)
        {
            ResetGrace();
            _zeroSince = _clock.Now;

            // Jede gewaehrte Schonfrist bei leerem Konto zaehlt - sofort
            // gespeichert, damit auch ein harter Neustart sie nicht vergisst.
            _state.EmptyGraceRuns++;
            _store.Save(_state);
            _lastSave = _clock.Now;

            // Wer sich mit leerem Konto gerade erst angemeldet hat, bekommt die
            // laengere Frist - das ist das Notfallfenster fuer das Master-Passwort.
            // Aber nur ein paar Mal in Folge: sonst wird aus dem Notfallfenster per
            // Dauer-Anmelden (oder Sperren und Entsperren) ein Gratis-Kontingent.
            var exhausted = _state.EmptyGraceRuns > MaxEmptyGraceRuns;

            _activeGrace = TimeSpan.FromSeconds(
                exhausted ? ShortEmergencyGraceSeconds
                : freshLogin ? _state.Config.LoginGraceSeconds
                : _state.Config.GraceSeconds);

            Log.Write($"Balance empty. Signing out in {_activeGrace.TotalSeconds:0} s " +
                      $"(grace {_state.EmptyGraceRuns} in a row" +
                      $"{(freshLogin ? ", fresh sign-in" : string.Empty)}" +
                      $"{(exhausted ? " - emergency window used up" : string.Empty)}).");
        }

        _previousRemainingMinutes = double.MaxValue;

        var elapsed = _clock.Now - _zeroSince.Value;
        if (elapsed < _activeGrace)
        {
            var left = (int)(_activeGrace - elapsed).TotalSeconds;

            // Je Sitzung genau einmal pro Schonfrist. Der Dialog steht ohnehin bis
            // zur Abmeldung; ein zweiter sagt nichts Neues, sondern legt sich nur
            // obendrauf. Wer den Agent laufen hat, sieht den Countdown im Overlay
            // und bekommt gar keinen.
            foreach (var session in sessions)
            {
                if (!NoLiveAgent(session) || !_graceNotified.Add(session)) continue;

                Native.SendMessage(session, "Monkey",
                    $"Your screen time is used up.\n\nSigning out in {left} seconds.\n\n" +
                    "Please save your work now.", Math.Max(5, left));
            }

            return;
        }

        _store.Save(_state);
        _lastSave = _clock.Now;

        foreach (var session in sessions)
        {
            if (RunMode.DryRunLogoff)
            {
                Log.Write($"[Dry run] Session {session} would be signed out now.");
                continue;
            }

            Log.Write($"Balance used up - signing out session {session}.");
            if (!Native.LogoffSession(session))
                Log.Write($"Signing out session {session} failed (Win32 {LastError()}).");
        }

        ResetGrace();
    }

    /// <summary>
    /// Schonfrist beenden: Uhr zurueck auf null und die Vermerke fuer den
    /// Systemdialog loeschen, damit die naechste Frist wieder einmal warnen darf.
    /// </summary>
    private void ResetGrace()
    {
        _zeroSince = null;
        _graceNotified.Clear();
    }

    private static int LastError() => System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    private void WarnOnThreshold(List<int> sessions)
    {
        var remaining = _state.BalanceSeconds / 60.0;
        var threshold = _state.Config.WarnMinutes;

        if (threshold > 0 && _previousRemainingMinutes > threshold && remaining <= threshold)
        {
            _activeWarning = threshold;
            _warningIssuedAt = _clock.Now;

            // Ohne laufenden Agent gibt es kein Warnfenster - dann der
            // Systemdialog als Rueckfallebene.
            foreach (var session in sessions.Where(NoLiveAgent))
                Native.SendMessage(session, "Monkey",
                    $"{threshold} minute(s) of screen time left.");

            Log.Write($"Warning threshold {threshold} min reached.");
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
                    _agents[request.SessionId] = new AgentReport(_clock.Now, request.ScreensaverRunning, request.DisplayOff);
                    return Response.Success(status: BuildStatus(request.SessionId));

                case RequestType.Pause:
                    return HandlePause(request);

                case RequestType.Resume:
                    return WithPassword(request.Password, () =>
                    {
                        _state.PauseUntil = null;
                        _store.Save(_state);
                        KickTelegram();
                        Log.Write("Pause ended early.");
                        return Response.Success("The limit is active again.", BuildStatus(request.SessionId));
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
                        Log.Write("Authorised teardown requested - releasing locks.");
                        SelfProtect.Teardown();
                        return Response.Success(
                            "All locks released. The service can now be stopped and removed.");
                    });

                default:
                    return Response.Fail($"Unknown request '{request.Type}'.");
            }
        }
    }

    private Response HandlePause(Request request)
    {
        var minutes = Math.Clamp(request.Minutes, 1, _state.Config.MaxPauseMinutes);
        return WithPassword(request.Password, () =>
        {
            _state.PauseUntil = _clock.Now.AddMinutes(minutes);
            ResetGrace();
            _store.Save(_state);
            KickTelegram();
            Log.Write($"Paused for {minutes} min until {_state.PauseUntil:HH:mm}.");
            return Response.Success($"Paused until {_state.PauseUntil:HH:mm}.",
                BuildStatus(request.SessionId));
        });
    }

    /// <summary>
    /// Zeit nachlegen. Beliebig oft moeglich, aber je Vorgang hoechstens der in den
    /// Einstellungen gesetzte Betrag. Abziehen geht unbegrenzt.
    /// </summary>
    private Response HandleAddTime(Request request)
    {
        if (request.Minutes == 0) return Response.Fail("No number of minutes given.");

        return WithPassword(request.Password, () =>
        {
            if (request.Minutes > _state.Config.MaxManualGrantMinutes)
                return Response.Fail(
                    $"At most {FormatMinutes(_state.Config.MaxManualGrantMinutes)} can be added per go. " +
                    "Need more? Just do it again.");

            _state.BalanceSeconds = Math.Max(0, _state.BalanceSeconds + request.Minutes * 60.0);

            // Zeit dazukaufen setzt die Evolution ganz auf Stufe 1 zurueck - gespart
            // ist nur, was aus den Tagesgutschriften stammt. Zeit abziehen setzt
            // nicht zurueck, senkt das Ersparte aber ueber ClampEarned entsprechend
            // mit: was man wegwirft, hat man eben auch nicht mehr gespart.
            if (request.Minutes > 0) _state.EarnedSeconds = 0;
            ClampEarned();

            ResetGrace();
            _previousRemainingMinutes = double.MaxValue;
            _store.Save(_state);
            KickTelegram();

            Log.Write($"Balance changed manually by {request.Minutes:+#;-#;0} min, now {Format(_state.BalanceSeconds)}" +
                      $"{(request.Minutes > 0 ? " - evolution reset to stage 1" : string.Empty)}.");

            return Response.Success($"Balance is now {Format(_state.BalanceSeconds)}.",
                BuildStatus(request.SessionId));
        });
    }

    private Response HandleSetConfig(Request request)
    {
        if (request.Config is not { } incoming) return Response.Fail("No settings were sent.");
        return WithPassword(request.Password, () =>
        {
            var config = _state.Config;
            config.DailyGrantMinutes = Math.Clamp(incoming.DailyGrantMinutes, 0, 24 * 60);
            config.CapMinutes = Math.Clamp(incoming.CapMinutes, config.DailyGrantMinutes, 100 * 24 * 60);
            config.GraceSeconds = Math.Clamp(incoming.GraceSeconds, 10, 3600);
            config.LoginGraceSeconds = Math.Clamp(incoming.LoginGraceSeconds, 10, 3600);
            config.MaxPauseMinutes = Math.Clamp(incoming.MaxPauseMinutes, 1, 30 * 24 * 60);
            config.PauseOnLock = incoming.PauseOnLock;
            config.PauseOnScreensaver = incoming.PauseOnScreensaver;
            config.WarnMinutes = Math.Clamp(incoming.WarnMinutes, 1, 24 * 60);
            config.AutoUpdate = incoming.AutoUpdate;

            // MaxManualGrantMinutes ist bewusst NICHT enthalten - es wird nur beim
            // Installieren festgelegt und laesst sich hier nicht aendern.

            _store.Save(_state);
            KickTelegram();
            Log.Write($"Settings changed: {config.DailyGrantMinutes} min/day, cap {config.CapMinutes} min, " +
                      $"warning at {config.WarnMinutes} min.");
            return Response.Success("Settings saved.", BuildStatus(request.SessionId));
        });
    }

    private Response HandleChangePassword(Request request)
    {
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 4)
            return Response.Fail("The new password needs at least 4 characters.");

        return WithPassword(request.Password, () =>
        {
            var (hash, salt, iterations) = PasswordHash.Create(request.NewPassword);
            _state.PasswordHash = hash;
            _state.PasswordSalt = salt;
            _state.PasswordIterations = iterations;
            _store.Save(_state);
            Log.Write("Master password changed.");
            return Response.Success("Master password changed.", BuildStatus(request.SessionId));
        });
    }

    /// <summary>Fuer den Update-Pruefer: laufend abgefragt, deshalb eigener kurzer Weg.</summary>
    public bool AutoUpdateEnabled
    {
        get { lock (_gate) return _state.Config.AutoUpdate; }
    }

    // ------------------------------------------------------------- Telegram

    /// <summary>
    /// Passwortpruefung fuer Anfragen, die ausserhalb der Engine weiterlaufen
    /// (Telegram-Einrichtung). Dieselbe Drossel wie ueberall sonst.
    /// </summary>
    public Response Authorize(string? password)
    {
        lock (_gate) return WithPassword(password, () => Response.Success());
    }

    public TelegramConfigView TelegramConfig()
    {
        lock (_gate)
            return new TelegramConfigView(
                _state.Telegram.Enabled, _state.Telegram.WorkerUrl, _state.Telegram.SyncSecretProtected);
    }

    public void SetTelegram(bool enabled, string? workerUrl, string? syncSecretProtected)
    {
        lock (_gate)
        {
            _state.Telegram.Enabled = enabled;
            _state.Telegram.WorkerUrl = workerUrl;
            _state.Telegram.SyncSecretProtected = syncSecretProtected;

            if (!enabled)
            {
                _lastTelegramSync = null;
                _telegramError = null;
            }

            _store.Save(_state);
            _lastSave = _clock.Now;

            Log.Write(enabled && workerUrl is not null
                ? $"Telegram link enabled (worker {new Uri(workerUrl).Host})."
                : "Telegram link disabled.");

            if (enabled) KickTelegram();
        }
    }

    public void ReportTelegramSync(bool ok, string? error)
    {
        lock (_gate)
        {
            if (ok)
            {
                _lastTelegramSync = _clock.Now;
                _telegramError = null;
            }
            else
            {
                _telegramError = error;
            }
        }
    }

    /// <summary>
    /// Momentaufnahme fuer den Worker. Enthaelt neben dem Stand alles, was er
    /// braucht, um bei ausgeschaltetem PC selbst weiterzurechnen: Tagesbudget,
    /// Deckel, letzter Gutschriftstag und Zeitzone.
    /// </summary>
    public TelegramSnapshot BuildTelegramSnapshot()
    {
        lock (_gate)
        {
            var paused = _state.PauseUntil is { } until && _clock.Now < until;

            return new TelegramSnapshot
            {
                BalanceSeconds = Math.Max(0, _state.BalanceSeconds),
                EarnedSeconds = Math.Max(0, _state.EarnedSeconds),
                DailyGrantMinutes = _state.Config.DailyGrantMinutes,
                CapMinutes = _state.Config.CapMinutes,
                MaxManualGrantMinutes = _state.Config.MaxManualGrantMinutes,
                MaxPauseMinutes = _state.Config.MaxPauseMinutes,
                EvolutionStage = _state.EvolutionStage,
                Counting = !paused && CountingSessions(Native.EnumerateSessions()).Count > 0,
                PauseRemainingSeconds = paused ? (_state.PauseUntil!.Value - _clock.Now).TotalSeconds : 0,
                LastAccrualDate = _state.LastAccrualDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TzOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
                SavedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
    }

    /// <summary>
    /// Fernbefehle des Freundes anwenden. Bewusst enger als die Pipe: nur
    /// nachlegen, pausieren, fortsetzen - keine Einstellungen, kein Passwort,
    /// kein Teardown. Jeder Befehl wird genau einmal ausgefuehrt, auch wenn der
    /// Worker ihn nach einer verlorenen Quittung erneut zustellt.
    /// </summary>
    public List<RemoteResult> ApplyRemoteCommands(IReadOnlyList<RemoteCommand> commands)
    {
        var results = new List<RemoteResult>();
        if (commands.Count == 0) return results;

        lock (_gate)
        {
            var changed = false;

            foreach (var command in commands)
            {
                if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 64) continue;

                if (_state.AppliedRemoteCommandIds.Contains(command.Id))
                {
                    results.Add(new RemoteResult(command.Id, true, "Already done."));
                    continue;
                }

                var (ok, message) = ApplyRemote(command);
                results.Add(new RemoteResult(command.Id, ok, message));

                _state.AppliedRemoteCommandIds.Add(command.Id);
                while (_state.AppliedRemoteCommandIds.Count > 64)
                    _state.AppliedRemoteCommandIds.RemoveAt(0);
                changed = true;
            }

            if (changed)
            {
                _store.Save(_state);
                _lastSave = _clock.Now;
            }
        }

        return results;
    }

    private (bool Ok, string Message) ApplyRemote(RemoteCommand command)
    {
        switch (command.Type)
        {
            case "add":
            {
                var minutes = command.Minutes;
                if (minutes < 1)
                    return (false, "The number of minutes must be positive.");
                if (minutes > _state.Config.MaxManualGrantMinutes)
                    return (false, $"At most {FormatMinutes(_state.Config.MaxManualGrantMinutes)} can be added per go.");

                _state.BalanceSeconds += minutes * 60.0;

                // Wie beim Nachlegen per Passwort: dazugegeben ist nicht gespart.
                _state.EarnedSeconds = 0;
                ClampEarned();
                ResetGrace();
                _previousRemainingMinutes = double.MaxValue;

                Log.Write($"Telegram: {minutes} min added remotely, balance now {Format(_state.BalanceSeconds)}.");
                return (true, $"Added {minutes} min. The balance is now {Format(_state.BalanceSeconds)}.");
            }

            case "pause":
            {
                var minutes = Math.Clamp(command.Minutes, 1, _state.Config.MaxPauseMinutes);
                _state.PauseUntil = _clock.Now.AddMinutes(minutes);
                ResetGrace();

                Log.Write($"Telegram: paused remotely for {minutes} min.");
                return (true, $"Paused for {minutes} min.");
            }

            case "resume":
                _state.PauseUntil = null;
                Log.Write("Telegram: pause ended remotely.");
                return (true, "The limit is active again.");

            default:
                return (false, "Unknown command.");
        }
    }

    /// <summary>
    /// Weckt den Telegram-Abgleich, ohne zu blockieren. Ausserhalb der Anbindung
    /// ein No-op - der Abgleich prueft selbst, ob er eingerichtet ist.
    /// </summary>
    private void KickTelegram()
    {
        if (!_state.Telegram.Enabled || TelegramKick.CurrentCount > 0) return;
        try { TelegramKick.Release(); }
        catch (SemaphoreFullException) { /* Weckruf steht schon an. */ }
    }

    /// <summary>
    /// Passwortpruefung mit Drossel. Ohne hinterlegtes Passwort wird nichts
    /// freigegeben - sonst waere das Loeschen der Zustandsdatei ein Freifahrtschein.
    /// </summary>
    private Response WithPassword(string? password, Func<Response> action)
    {
        if (!_state.HasPassword)
            return Response.Fail("No master password is stored. Please reinstall with MonkeySetup.exe.");

        if (_clock.Now < _lockoutUntil)
        {
            var seconds = (int)(_lockoutUntil - _clock.Now).TotalSeconds;
            return Response.Fail($"Too many wrong tries. Locked for another {seconds} seconds.");
        }

        if (string.IsNullOrEmpty(password) ||
            !PasswordHash.Verify(password, _state.PasswordHash, _state.PasswordSalt, _state.PasswordIterations))
        {
            _failedAttempts++;
            Log.Write($"Wrong master password (attempt {_failedAttempts}).");

            if (_failedAttempts >= MaxFailedAttempts)
            {
                _lockoutUntil = _clock.Now + LockoutDuration;
                _failedAttempts = 0;
                return Response.Fail($"Too many wrong tries. Locked for {LockoutDuration.TotalSeconds:0} seconds.");
            }

            return Response.Fail("Wrong master password.");
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
            EvolutionStage = _state.EvolutionStage,
            DailyGrantMinutes = _state.Config.DailyGrantMinutes,
            CapMinutes = _state.Config.CapMinutes,
            ClockTamperEvents = _state.ClockTamperEvents,
            PasswordConfigured = _state.HasPassword,
            Config = _state.Config.Clone(),
            ServiceVersion = UpdateWorker.CurrentVersionText,
            TelegramEnabled = _state.Telegram.Enabled,
            TelegramWorkerHost = _state.Telegram.WorkerUrl is { } workerUrl
                && Uri.TryCreate(workerUrl, UriKind.Absolute, out var workerUri) ? workerUri.Host : null,
            TelegramLastSyncSecondsAgo = _lastTelegramSync is { } sync
                ? Math.Max(0, (_clock.Now - sync).TotalSeconds) : null,
            TelegramLastError = _telegramError,
        };
    }

    public void Flush()
    {
        lock (_gate)
        {
            _state.TrustedNow = _clock.Now;
            _store.Save(_state);
            Log.Write($"Service stopped. Balance {Format(_state.BalanceSeconds)}.");
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
