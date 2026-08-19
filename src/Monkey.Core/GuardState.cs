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

    /// <summary>
    /// Cloudflare-API-Token fuer selbsttaetige Worker-Updates, DPAPI-
    /// verschluesselt wie das Sync-Secret: nur das Dienstkonto liest es.
    /// Der Preis fuer Updates ohne Zutun; wer das Token bei Cloudflare
    /// widerruft, ist wieder beim Einfuegen von Hand.
    /// </summary>
    public string? ApiTokenProtected { get; set; }

    /// <summary>
    /// Metadaten des automatisch eingerichteten Workers. Sie sind keine
    /// Geheimnisse.
    /// </summary>
    public bool Managed { get; set; }
    public string? CloudflareAccountId { get; set; }
    public string? ScriptName { get; set; }
    public string? KvNamespaceId { get; set; }
    public int? WorkerVersion { get; set; }
}

/// <summary>
/// Ein Kalendertag in der Rueckschau. Rein zur Anzeige - keine Entscheidung des
/// Dienstes haengt daran. In Sekunden, weil der Dienst so rechnet; auf Minuten
/// rundet erst die Anzeige.
/// </summary>
public sealed class DayStat
{
    public DateOnly Date { get; set; }

    /// <summary>Tatsaechlich am Rechner verbrauchte Zeit.</summary>
    public double UsedSeconds { get; set; }

    /// <summary>Was die Tagesgutschrift beigesteuert hat - nach dem Deckel.</summary>
    public double GrantedSeconds { get; set; }

    /// <summary>Per Master-Passwort oder Telegram nachgelegt.</summary>
    public double AddedSeconds { get; set; }

    /// <summary>Ebenso abgezogen, als positiver Betrag.</summary>
    public double RemovedSeconds { get; set; }

    /// <summary>Stand am Tagesende - beim laufenden Tag der aktuelle.</summary>
    public double BalanceEndSeconds { get; set; }

    /// <summary>Davon erspart, also aus Tagesgutschriften statt nachgelegt.</summary>
    public double EarnedEndSeconds { get; set; }
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

    /// <summary>
    /// Rueckschau je Kalendertag, aelteste zuerst. Auf <see cref="HistoryDays"/>
    /// begrenzt, damit die Zustandsdatei nicht unbegrenzt waechst.
    /// </summary>
    public List<DayStat> History { get; set; } = new();

    /// <summary>Wie viele Tage die Rueckschau behaelt - gut ein Jahr.</summary>
    public const int HistoryDays = 400;

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
