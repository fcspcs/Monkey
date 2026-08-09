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

    /// <summary>
    /// Neue Releases selbststaendig installieren. Braucht kein Passwort, denn es
    /// kann nur in eine Richtung: eine neuere, vom Projektschluessel signierte
    /// Version einspielen - Guthaben und Passwort bleiben dabei unberuehrt.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    // Alle Felder sind Werttypen - eine flache Kopie genuegt.
    public GuardConfig Clone() => (GuardConfig)MemberwiseClone();
}

/// <summary>
/// Einstellungen der optionalen Telegram-Anbindung. Die Bot-Tokens stehen bewusst
/// nicht hier: sie liegen ausschliesslich beim Worker des Nutzers. Auf dem PC bleibt
/// nur, was der Dienst zum Abgleich braucht.
/// </summary>
public sealed class TelegramSettings
{
    public bool Enabled { get; set; }

    /// <summary>Basis-URL des eigenen Cloudflare Workers (https://...).</summary>
    public string? WorkerUrl { get; set; }

    /// <summary>
    /// Sync-Secret fuer die Verbindung zum Worker, DPAPI-verschluesselt (Base64).
    /// Nur das Dienstkonto kann es entschluesseln - Administratoren, die die
    /// Zustandsdatei lesen duerfen, sehen hier nur Chiffrat.
    /// </summary>
    public string? SyncSecretProtected { get; set; }
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

    /// <summary>
    /// Der Teil des Guthabens, der wirklich erspart wurde - also aus den
    /// Tagesgutschriften stammt und nicht per Master-Passwort nachgelegt wurde.
    /// Steuert allein die Evolutionsstufe: Wer sich Zeit dazukauft, faengt beim
    /// kleinen Affen wieder an.
    /// </summary>
    public double EarnedSeconds { get; set; }

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

    /// <summary>
    /// Wie oft in Folge bei leerem Konto eine Schonfrist gewaehrt wurde - egal ob
    /// nach einer Anmeldung (Notfallfenster) oder mitten in der Sitzung. Haelt
    /// das Fenster davon ab, als Gratis-Kontingent gemolken zu werden: staendig
    /// neu anmelden oder sperren/entsperren bringt ab dem vierten Mal nur noch
    /// Sekunden. Sobald wieder Guthaben da ist, faellt der Zaehler auf null.
    /// </summary>
    public int EmptyGraceRuns { get; set; }

    public DateTimeOffset LastSaved { get; set; }

    public TelegramSettings Telegram { get; set; } = new();

    /// <summary>
    /// Bereits ausgefuehrte Fernbefehle (Telegram). Schuetzt vor doppelter
    /// Ausfuehrung, wenn eine Bestaetigung den Worker nicht erreicht hat und er
    /// denselben Befehl noch einmal zustellt.
    /// </summary>
    public List<string> AppliedRemoteCommandIds { get; set; } = new();

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    /// <summary>Anzahl der Evolutionsstufen (1 = kleiner Affe, 5 = Gorilla).</summary>
    public const int EvolutionStages = 5;

    /// <summary>
    /// Stufe aus dem Ersparten, gemessen am Tagesbudget: eine Tagesgutschrift ist
    /// der kleine Affe, zwei angesparte Tagesbudgets die zweite Stufe und so
    /// weiter bis Stufe 5 ab dem Fuenffachen. Der Deckel spielt hier keine Rolle -
    /// er begrenzt nur, wie weit sich ueberhaupt ansparen laesst.
    /// </summary>
    [JsonIgnore]
    public int EvolutionStage
    {
        get
        {
            var daily = Config.DailyGrantMinutes * 60.0;
            if (daily <= 0) return 1;

            var stage = (int)(EarnedSeconds / daily);
            return Math.Clamp(stage, 1, EvolutionStages);
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static GuardState? FromJson(string json) =>
        JsonSerializer.Deserialize<GuardState>(json, JsonOptions);
}
