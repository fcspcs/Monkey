using System.Text.Json;

namespace Monkey.Core;

public static class RequestType
{
    public const string Status = "status";
    public const string Heartbeat = "heartbeat";

    /// <summary>
    /// Tagesverlauf fuer die Statistikseite. Bewusst ohne Passwort - wie Status
    /// gibt er nur Auskunft und raeumt keine Befugnis ein. Es sind ausserdem die
    /// eigenen Nutzungsdaten dessen, der davorsitzt.
    /// </summary>
    public const string History = "history";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string AddTime = "addtime";
    public const string SetConfig = "setconfig";
    public const string ChangePassword = "changepassword";

    /// <summary>Autorisierter Abbau aller Sperren, damit sich das Tool entfernen laesst.</summary>
    public const string Unlock = "unlock";

    // Optionale Telegram-Anbindung. Die Anfragen laufen wie alles andere ueber die
    // Pipe und verlangen das Master-Passwort - geprueft wird im Dienst.
    public const string TelegramSetup = "telegramsetup";
    public const string TelegramDeploy = "telegramdeploy";
    public const string TelegramWorkerCheck = "telegramworkercheck";
    public const string TelegramWorkerUpdate = "telegramworkerupdate";
    public const string TelegramWorkerRemove = "telegramworkerremove";
    public const string TelegramPair = "telegrampair";
    public const string TelegramOff = "telegramoff";
}

public sealed class Request
{
    public string Type { get; set; } = RequestType.Status;

    /// <summary>Master-Passwort. Wird ausschliesslich im Dienst geprueft.</summary>
    public string? Password { get; set; }

    public string? NewPassword { get; set; }
    public int Minutes { get; set; }

    // Vom Agent gemeldeter Sitzungszustand.
    public int SessionId { get; set; }
    public bool ScreensaverRunning { get; set; }

    /// <summary>Bildschirm ist aus (moderner Ersatz des Bildschirmschoners).</summary>
    public bool DisplayOff { get; set; }

    public GuardConfig? Config { get; set; }

    // Telegram-Einrichtung. Die Bot-Tokens laufen hier nur einmal durch: der Dienst
    // reicht sie an den Worker weiter und behaelt sie selbst nicht.
    public string? WorkerUrl { get; set; }
    public string? SyncSecret { get; set; }
    public string? MonkeyToken { get; set; }
    public string? FriendToken { get; set; }

    // Nur fuer die einmalige automatische Worker-Einrichtung. Das API-Token
    // wird weder vom Agent noch vom Dienst gespeichert.
    public string? CloudflareAccountId { get; set; }
    public string? CloudflareApiToken { get; set; }

    /// <summary>Fuer welchen Bot ein Pairing-Code erzeugt wird: "monkey" oder "friend".</summary>
    public string? PairRole { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, GuardState.JsonOptions);
    public static Request? FromJson(string json) => JsonSerializer.Deserialize<Request>(json, GuardState.JsonOptions);
}

public sealed class StatusDto
{
    public double BalanceSeconds { get; set; }

    /// <summary>
    /// Angerechnete Zeit seit der Anmeldung dieser Sitzung. Fuer die Anzeige im
    /// Hochzaehl-Modus.
    /// </summary>
    public double SessionElapsedSeconds { get; set; }

    public bool Paused { get; set; }
    public DateTimeOffset? PauseUntil { get; set; }

    /// <summary>Laeuft die Uhr gerade, also wird gerade verbraucht?</summary>
    public bool Counting { get; set; }

    /// <summary>Sekunden bis zur Zwangsabmeldung, sonst null.</summary>
    public double? SecondsUntilLogoff { get; set; }

    /// <summary>
    /// Gerade ueberschrittene Warnschwelle in Minuten. Bleibt kurz gesetzt, damit
    /// der Agent sie beim naechsten Abruf zuverlaessig aufgreift.
    /// </summary>
    public int? WarningMinutes { get; set; }

    /// <summary>Wie viel per Master-Passwort pro Vorgang nachgelegt werden darf.</summary>
    public int MaxManualGrantMinutes { get; set; }

    /// <summary>Evolutionsstufe des Affen, 1 bis 5 - allein aus dem Ersparten.</summary>
    public int EvolutionStage { get; set; } = 1;

    public int DailyGrantMinutes { get; set; }
    public int CapMinutes { get; set; }
    public int ClockTamperEvents { get; set; }
    public bool PasswordConfigured { get; set; }
    public GuardConfig? Config { get; set; }

    /// <summary>
    /// Letzter Speicherfehler des Dienstes, null solange alles klappt. Muss dem
    /// Master auffallen: ein Dienst, der nicht speichern kann, verliert beim
    /// naechsten Neustart alles seit dem letzten erfolgreichen Speichern.
    /// </summary>
    public string? PersistenceError { get; set; }

    /// <summary>
    /// Version des laufenden Dienstes. Der Agent vergleicht sie mit seiner
    /// eigenen und startet sich nach einem Update selbst neu.
    /// </summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Enthaelt der Dienst den oeffentlichen Schluessel fuer Release-Signaturen?</summary>
    public bool SignedUpdatesAvailable { get; set; }

    // Zustand der optionalen Telegram-Anbindung, nur fuer die Anzeige.
    public bool TelegramEnabled { get; set; }
    public string? TelegramWorkerHost { get; set; }
    public bool TelegramWorkerManaged { get; set; }
    public int? TelegramWorkerVersion { get; set; }
    public string? TelegramCloudflareAccountId { get; set; }
    public double? TelegramLastSyncSecondsAgo { get; set; }
    public string? TelegramLastError { get; set; }
}

/// <summary>Ein Kalendertag fuer die Statistik, fertig in Minuten.</summary>
public sealed class DayStatDto
{
    public DateOnly Date { get; set; }
    public double UsedMinutes { get; set; }
    public double GrantedMinutes { get; set; }
    public double AddedMinutes { get; set; }
    public double RemovedMinutes { get; set; }
    public double BalanceEndMinutes { get; set; }
    public double EarnedEndMinutes { get; set; }
}

public sealed class Response
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public StatusDto? Status { get; set; }

    /// <summary>Nur bei <see cref="RequestType.History"/> gefuellt, aelteste zuerst.</summary>
    public List<DayStatDto>? History { get; set; }

    public static Response Fail(string message) => new() { Ok = false, Message = message };
    public static Response Success(string? message = null, StatusDto? status = null) =>
        new() { Ok = true, Message = message, Status = status };

    public string ToJson() => JsonSerializer.Serialize(this, GuardState.JsonOptions);
    public static Response? FromJson(string json) => JsonSerializer.Deserialize<Response>(json, GuardState.JsonOptions);
}
