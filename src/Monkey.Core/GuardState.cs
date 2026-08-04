using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monkey.Core;

public sealed class GuardConfig
{
    /// <summary>Minuten, die pro Kalendertag gutgeschrieben werden.</summary>
    public int DailyGrantMinutes { get; set; } = 30;

    /// <summary>
    /// Obergrenze des Guthabens. Ohne Deckel sammelt ein zweiwoechiger Urlaub
    /// ein Kontingent an, das das ganze System entwertet.
    /// </summary>
    public int CapMinutes { get; set; } = 240;

    /// <summary>Restminuten, bei denen das Warnfenster erscheint.</summary>
    public int WarnMinutes { get; set; } = 10;

    /// <summary>
    /// Schonfrist ab Erreichen von 0 waehrend einer laufenden Sitzung, bevor
    /// abgemeldet wird. Zeit zum Speichern.
    /// </summary>
    public int GraceSeconds { get; set; } = 90;

    /// <summary>
    /// Schonfrist nach einer Anmeldung mit bereits leerem Konto. Bewusst laenger:
    /// das ist das Zeitfenster, um im Notfall per Master-Passwort Zeit nachzulegen.
    /// </summary>
    public int LoginGraceSeconds { get; set; } = 120;

    /// <summary>
    /// Wie viel Zeit sich pro Vorgang per Master-Passwort nachlegen laesst.
    /// Beliebig oft nutzbar, aber je Nutzung hoechstens dieser Betrag.
    /// </summary>
    public int MaxManualGrantMinutes { get; set; } = 240;

    /// <summary>Bildschirmschoner haelt die Uhr an.</summary>
    public bool PauseOnScreensaver { get; set; } = true;

    /// <summary>Gesperrte Sitzung haelt die Uhr an.</summary>
    public bool PauseOnLock { get; set; } = true;

    /// <summary>Laengste am Stueck erlaubte Master-Pause.</summary>
    public int MaxPauseMinutes { get; set; } = 480;

    // Alle Felder sind Werttypen - eine flache Kopie genuegt.
    public GuardConfig Clone() => (GuardConfig)MemberwiseClone();
}

public sealed class GuardState
{
    public int Version { get; set; } = 1;

    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public int PasswordIterations { get; set; }

    public GuardConfig Config { get; set; } = new();

    /// <summary>Verbleibendes Guthaben in Sekunden.</summary>
    public double BalanceSeconds { get; set; }

    /// <summary>Letzter Kalendertag (lokal), fuer den gutgeschrieben wurde.</summary>
    public DateOnly? LastAccrualDate { get; set; }

    /// <summary>
    /// Vom Dienst gefuehrte Uhr. Sie laeuft ueber die Zeit seit Systemstart und ist
    /// damit unabhaengig davon, ob jemand an der Systemzeit dreht.
    /// </summary>
    public DateTimeOffset TrustedNow { get; set; }

    /// <summary>Master-Pause laeuft bis hierhin (TrustedNow-Zeitbasis).</summary>
    public DateTimeOffset? PauseUntil { get; set; }

    /// <summary>Zaehler fuer erkannte Systemzeit-Manipulationen.</summary>
    public int ClockTamperEvents { get; set; }

    public DateTimeOffset LastSaved { get; set; }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static GuardState? FromJson(string json) =>
        JsonSerializer.Deserialize<GuardState>(json, JsonOptions);
}
