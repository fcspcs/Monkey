using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>Vom Worker zugestellter Fernbefehl des Freundes.</summary>
public sealed record RemoteCommand(string Id, string Type, int Minutes);

/// <summary>Ergebnis eines Fernbefehls - geht als Quittung zurueck an den Worker.</summary>
public sealed record RemoteResult(string Id, bool Ok, string Message);

/// <summary>Momentaufnahme fuer den Worker, damit er auch bei ausgeschaltetem PC antworten kann.</summary>
public sealed class TelegramSnapshot
{
    public double BalanceSeconds { get; init; }
    public double EarnedSeconds { get; init; }
    public int DailyGrantMinutes { get; init; }
    public int CapMinutes { get; init; }
    public int EvolutionStage { get; init; }
    public bool Counting { get; init; }
    public string? LastAccrualDate { get; init; }
    public int TzOffsetMinutes { get; init; }
    public long SavedAtUtcMs { get; init; }
}

/// <summary>
/// Abgleich mit dem eigenen Cloudflare Worker des Nutzers - der optionale
/// Telegram-Draht. Der Dienst meldet regelmaessig den Stand (damit der Worker auch
/// bei ausgeschaltetem PC antworten kann) und holt dabei wartende Befehle des
/// Freundes ab. Entschieden wird alles in der GuardEngine; der Worker kann nur,
/// was <see cref="GuardEngine.ApplyRemoteCommands"/> zulaesst - das Master-Passwort
/// kennt er nicht und braucht er nicht.
/// </summary>
internal sealed class TelegramSync(
    GuardEngine engine,
    TimeSpan? syncInterval = null,
    TimeSpan? stateHeartbeat = null) : BackgroundService
{
    /// <summary>
    /// Takt des Abgleichs. Kurz gehalten, damit Befehle des Freundes schnell
    /// ankommen - eine Runde ohne Neuigkeiten kostet den Worker nur einen
    /// Lesevorgang und ist damit praktisch umsonst.
    /// </summary>
    private static readonly TimeSpan DefaultSyncInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// So oft wird der Stand spaetestens zum Worker geschoben. Der Takt oben
    /// bleibt bei einer halben Minute - er haelt die Reaktionszeit auf Befehle
    /// des Freundes kurz und kostet den Worker nur Lesevorgaenge. Nur das
    /// Schreiben ist knapp: Cloudflares kostenloses KV-Kontingent erlaubt 1000
    /// Schreibvorgaenge pro Tag, ein Stand je Takt braeuchte 2880 und wuerde
    /// nach gut acht Stunden Laufzeit abgewiesen.
    /// </summary>
    private static readonly TimeSpan DefaultStateHeartbeat = TimeSpan.FromMinutes(5);

    // Beide Takte sind nur fuer Tests einstellbar; im Dienst gelten die Vorgaben.
    private readonly TimeSpan _syncInterval = syncInterval ?? DefaultSyncInterval;
    private readonly TimeSpan _stateHeartbeat = stateHeartbeat ?? DefaultStateHeartbeat;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient CloudflareHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex TokenPattern = new(@"^\d{5,12}:[A-Za-z0-9_\-]{30,64}$", RegexOptions.Compiled);
    private static readonly Regex WebhookSecretPattern = new(@"^[A-Za-z0-9_\-]{16,128}$", RegexOptions.Compiled);
    private static readonly Regex AccountIdPattern = new(@"^[a-fA-F0-9]{32}$", RegexOptions.Compiled);
    private const string WorkerCompatibilityDate = "2024-11-01";
    private const int CurrentWorkerVersion = 4;

    /// <summary>
    /// Quittungen, die den Worker noch nicht erreicht haben. Bleiben liegen, bis
    /// ein Abgleich klappt - erst mit der Quittung loescht der Worker den Befehl
    /// aus seiner Warteschlange und benachrichtigt den Absender.
    /// </summary>
    private readonly List<RemoteResult> _unsentResults = [];

    private int _failures;

    /// <summary>Wann der Stand zuletzt beim Worker ankam - Grundlage des Herzschlags.</summary>
    private DateTimeOffset _lastStatePush = DateTimeOffset.MinValue;

    /// <summary>
    /// Fingerabdruck des zuletzt gemeldeten Standes. Weicht der aktuelle davon
    /// ab, hat sich etwas geaendert, das der Worker nicht selbst ausrechnen kann.
    /// </summary>
    private string? _lastPushedFingerprint;

    /// <summary>Zuletzt gemeldeter Stand - Ausgangspunkt der Vorhersagepruefung.</summary>
    private double _lastPushedBalance;
    private double _lastPushedEarned;

    /// <summary>
    /// So weit darf die Vorhersage des Workers danebenliegen, ohne dass
    /// geschrieben wird. Grosszuegig gegenueber Taktjitter, aber weit unter
    /// jedem echten Sprung - die kleinste Gabe ist eine Minute.
    /// </summary>
    private const double DriftToleranceSeconds = 30;

    /// <summary>Wann der Worker das naechste Mal auf eine alte Fassung geprueft wird.</summary>
    private DateTimeOffset _nextWorkerVersionCheck = DateTimeOffset.MinValue;

    /// <summary>
    /// Hoechste Fassung, deren Selbst-Update schon einmal fehlschlug. Der Takt
    /// versucht sie nicht endlos weiter - ein Dienstneustart schon.
    /// </summary>
    private int _workerUpdateFailedFor;

    private static readonly TimeSpan WorkerVersionCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Erst kurz nach Dienststart pruefen, nicht sofort: direkt nach einem
    /// Monkey-Update ist der Sync noch gar nicht gelaufen, und der Worker soll
    /// zuerst den frischen Stand bekommen.
    /// </summary>
    private static readonly TimeSpan FirstWorkerVersionCheckDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Weckruf durch die Engine (Guthaben geaendert) oder regulaerer Takt.
                // Wer geweckt wurde, meldet den Stand ungefragt - der Weckruf kommt
                // ja gerade deshalb, weil sich etwas geaendert hat.
                bool kicked;
                try { kicked = await engine.TelegramKick.WaitAsync(_syncInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }

                try { await SyncOnceAsync(kicked, stoppingToken); }
                catch (OperationCanceledException) { break; }

                try { await MaybeUpdateWorkerAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // Letzter Stand beim Herunterfahren - genau davon lebt die Abfrage bei
            // ausgeschaltetem PC. Nur ein Versuch, mit knapper Frist, und der
            // Stand geht in jedem Fall mit: danach kommt keiner mehr.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await SyncOnceAsync(true, timeout.Token); }
            catch (Exception) { /* Best effort - das Herunterfahren wartet nicht. */ }
        }
    }

    private async Task SyncOnceAsync(bool force, CancellationToken token)
    {
        var settings = engine.TelegramConfig();
        if (!settings.Enabled ||
            string.IsNullOrEmpty(settings.WorkerUrl) ||
            string.IsNullOrEmpty(settings.SyncSecretProtected))
            return;

        string secret;
        try
        {
            secret = Dpapi.Unprotect(settings.SyncSecretProtected);
        }
        catch (Exception)
        {
            engine.ReportTelegramSync(false, "stored sync secret is unreadable");
            return;
        }

        // Ein aelterer Worker rechnet den Ablauf noch nicht selbst mit und haelt
        // schon nach 90 Sekunden Stille den PC fuer aus. Ihm gegenueber bleibt es
        // beim alten Verhalten, bis die Selbstaktualisierung durch ist - sonst
        // behauptete /status stundenlang, der PC sei aus. Unbekannte Fassung zaehlt
        // als alt.
        var sparse = settings.WorkerVersion >= CurrentWorkerVersion;

        try
        {
            // Bis zu drei Runden: Stand melden und Befehle holen, Befehle anwenden,
            // Quittungen sofort nachreichen (statt erst im naechsten Takt).
            for (var round = 0; round < 3; round++)
            {
                // Ab Runde eins hat gerade ein Fernbefehl gewirkt - der Sprung im
                // Guthaben muss sofort hoch, sonst zeigt /status ihn erst beim
                // naechsten Herzschlag.
                var snapshot = engine.BuildTelegramSnapshot();
                var now = DateTimeOffset.UtcNow;
                var withState = force || round > 0 || !sparse || StateIsStale(snapshot, now);

                var body = new
                {
                    state = withState ? snapshot : null,
                    results = _unsentResults.ToArray(),
                };
                using var doc = await PostAsync($"{settings.WorkerUrl}/sync", secret, body, token);
                _unsentResults.Clear();

                // Erst nach der geglueckten Antwort: ein fehlgeschlagener Versuch
                // darf den Herzschlag nicht als erledigt abhaken.
                if (withState)
                {
                    _lastStatePush = now;
                    _lastPushedFingerprint = Fingerprint(snapshot);
                    _lastPushedBalance = snapshot.BalanceSeconds;
                    _lastPushedEarned = snapshot.EarnedSeconds;
                }

                var commands = ParseCommands(doc);
                if (commands.Count == 0) break;

                _unsentResults.AddRange(engine.ApplyRemoteCommands(commands));
            }

            engine.ReportTelegramSync(true, null);
            _failures = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures++;
            engine.ReportTelegramSync(false, Shorten(ex.Message, 120));

            // Kein Netz ist bei einem Heim-PC Alltag - nicht jede halbe Minute eine
            // Logzeile, aber der erste Fehler und dann alle zehn Minuten einer.
            if (_failures == 1 || _failures % 20 == 0)
                Log.Write($"Telegram sync failing ({_failures}x): {Shorten(ex.Message, 200)}");
        }
    }

    /// <summary>
    /// Muss der Stand hoch? Nur wenn der Worker von sich aus etwas Falsches
    /// anzeigen wuerde. Massstab ist also nicht "hat sich etwas geaendert",
    /// sondern "liegt project() in worker.js daneben" - denn den Ablauf bei
    /// laufender Uhr rechnet der Worker selbst mit.
    /// </summary>
    private bool StateIsStale(TelegramSnapshot snapshot, DateTimeOffset now)
    {
        if (_lastPushedFingerprint is null) return true;
        if (Fingerprint(snapshot) != _lastPushedFingerprint) return true;

        // Der Herzschlag haelt ausserdem die Online-Erkennung des Workers frisch.
        if (now - _lastStatePush >= _stateHeartbeat) return true;

        // Was der Worker gerade rechnen wuerde. Bei stehender Uhr ist das der
        // zuletzt gemeldete Stand, bei laufender der um die verstrichene Zeit
        // verminderte - genau wie in project(). Alles, was daneben liegt, ist ein
        // Sprung, den niemand vorhersagen konnte: Nachlegen, Banane, Ruhezustand.
        var elapsed = snapshot.Counting ? (now - _lastStatePush).TotalSeconds : 0;
        return Drifted(snapshot.BalanceSeconds, _lastPushedBalance, elapsed) ||
               Drifted(snapshot.EarnedSeconds, _lastPushedEarned, elapsed);

        static bool Drifted(double actual, double lastPushed, double elapsed) =>
            Math.Abs(actual - Math.Max(0, lastPushed - elapsed)) > DriftToleranceSeconds;
    }

    /// <summary>
    /// Der Teil des Standes, den der Worker nicht herleiten kann. Guthaben und
    /// Ersparnis fehlen bewusst - die prueft <see cref="StateIsStale"/> gegen die
    /// Vorhersage, statt jede ablaufende Sekunde als Aenderung zu werten. Die
    /// Evolutionsstufe fehlt ebenso: sie folgt aus Ersparnis und Tagesbudget.
    /// </summary>
    internal static string Fingerprint(TelegramSnapshot snapshot) => string.Join('|',
        snapshot.DailyGrantMinutes.ToString(CultureInfo.InvariantCulture),
        snapshot.CapMinutes.ToString(CultureInfo.InvariantCulture),
        snapshot.Counting ? "1" : "0",
        snapshot.LastAccrualDate ?? "-",
        snapshot.TzOffsetMinutes.ToString(CultureInfo.InvariantCulture));

    // ------------------------------------------------- Anfragen aus dem Agent

    public async Task<Response> HandleAsync(Request request)
    {
        try
        {
            return request.Type switch
            {
                RequestType.TelegramSetup => await SetupAsync(request),
                RequestType.TelegramDeploy => await DeployAsync(request),
                RequestType.TelegramWorkerCheck => await CheckWorkerAsync(request),
                RequestType.TelegramWorkerUpdate => await UpdateWorkerAsync(request),
                RequestType.TelegramWorkerRemove => await RemoveWorkerAsync(request),
                RequestType.TelegramPair => await PairAsync(request),
                RequestType.TelegramOff => await DisableAsync(request),
                _ => Response.Fail($"Unknown request '{request.Type}'."),
            };
        }
        catch (InvalidOperationException ex)
        {
            return Response.Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Response.Fail($"The worker could not be reached: {Shorten(ex.Message, 160)}");
        }
        catch (TaskCanceledException)
        {
            return Response.Fail("The worker did not answer in time.");
        }
        catch (Exception ex)
        {
            Log.Write($"Telegram request failed: {ex.Message}");
            return Response.Fail("Internal error while talking to the worker.");
        }
    }

    /// <summary>
    /// Gefuehrte Ein-Klick-Einrichtung: legt einen eigenen KV-Speicher an, laedt
    /// den eingebetteten Worker hoch, bindet das Sync-Secret und verbindet danach
    /// beide Telegram-Bots. Cloudflare-Token und Bot-Tokens werden nicht gespeichert;
    /// die Tokens landen als nicht auslesbare Cloudflare-Secret-Bindings.
    /// </summary>
    private async Task<Response> DeployAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        if (engine.TelegramConfig().Enabled)
            return Response.Fail("Telegram is already connected. Disconnect it before deploying a replacement Worker.");

        var accountId = request.CloudflareAccountId?.Trim();
        if (accountId is null || !AccountIdPattern.IsMatch(accountId))
            return Response.Fail("The Cloudflare Account ID must contain exactly 32 hexadecimal characters.");

        var apiToken = request.CloudflareApiToken?.Trim();
        if (apiToken is null || apiToken.Length is < 20 or > 512)
            return Response.Fail("The Cloudflare API token looks incomplete.");

        var tokens = ValidateBotTokens(request);
        if (!tokens.Ok) return Response.Fail(tokens.Error!);

        var (scriptName, namespaceTitle) = ManagedNames();
        var secret = NewSyncSecret();

        if (await WorkerExistsAsync(accountId, apiToken, scriptName))
            return Response.Fail(
                $"A Worker named '{scriptName}' already exists in this account. Connect that Worker manually or remove the orphan in Cloudflare before deploying.");

        var kv = await EnsureKvNamespaceAsync(accountId, apiToken, namespaceTitle);
        if (!kv.Created)
            return Response.Fail(
                $"A KV store named '{namespaceTitle}' already exists without a connected Worker. Remove that orphan in Cloudflare before deploying.");
        var namespaceId = kv.Id;
        var workerSecrets = new WorkerSecrets(
            secret,
            tokens.MonkeyToken!,
            tokens.FriendToken!,
            NewWebhookSecret(),
            NewWebhookSecret());
        string workerUrl;
        try
        {
            await UploadWorkerAsync(accountId, apiToken, scriptName, namespaceId, workerSecrets);
            await EnableWorkerSubdomainAsync(accountId, apiToken, scriptName);
            var accountSubdomain = await GetAccountSubdomainAsync(accountId, apiToken);
            workerUrl = $"https://{scriptName}.{accountSubdomain}.workers.dev";

            await WaitForWorkerAsync(workerUrl);
            await ProvisionBotsAsync(workerUrl, secret);
            engine.SetTelegram(
                true, workerUrl, Dpapi.Protect(secret), true,
                accountId, scriptName, namespaceId, CurrentWorkerVersion);
            engine.StoreTelegramApiToken(Dpapi.Protect(apiToken));
        }
        catch
        {
            // Ohne lokale Verbindung waere ein teilweise eingerichteter Worker
            // samt Secrets sonst verwaist. Nur Ressourcen mit exakt unserem
            // Namen bzw. in diesem Versuch neu erzeugte KV-Daten entfernen.
            await TryDeleteCloudflareAsync(
                $"accounts/{accountId}/workers/scripts/{scriptName}", apiToken);
            if (kv.Created)
                await TryDeleteCloudflareAsync(
                    $"accounts/{accountId}/storage/kv/namespaces/{namespaceId}", apiToken);
            throw;
        }

        return Response.Success(
            $"Cloudflare Worker v{CurrentWorkerVersion} deployed and Telegram connected. Bot and webhook tokens are encrypted Cloudflare secrets.\n" +
            "The API token stays on this PC, encrypted and readable only by the service, so future Worker updates install themselves. " +
            "Next: create one pairing code for each bot below.");
    }

    /// <summary>
    /// Einrichtung: Tokens und frische Webhook-Geheimnisse zum Worker bringen, der
    /// registriert damit beide Webhooks bei Telegram. Die Tokens werden auf dem PC
    /// nicht gespeichert - nach diesem Aufruf kennt nur noch der Worker sie.
    /// </summary>
    private async Task<Response> SetupAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var rawUrl = request.WorkerUrl?.Trim().TrimEnd('/');
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return Response.Fail("The worker address must be a plain https:// URL.");

        var secret = request.SyncSecret?.Trim();
        if (secret is null || secret.Length < 32 || secret.Length > 128)
            return Response.Fail("The sync secret looks wrong - generate it with the button and copy it to Cloudflare unchanged.");

        var url = rawUrl!;
        var workerVersion = await GetWorkerVersionAsync(url, secret);
        if (workerVersion < CurrentWorkerVersion)
        {
            var tokens = ValidateBotTokens(request);
            if (!tokens.Ok)
                return Response.Fail(
                    "This is a legacy Worker. Enter both bot tokens to connect it, or use the automatic deployment above.");

            await ProvisionLegacyBotsAsync(url, secret, tokens.MonkeyToken!, tokens.FriendToken!);
        }
        else
        {
            await ProvisionBotsAsync(url, secret);
        }

        engine.SetTelegram(true, url, Dpapi.Protect(secret), workerVersion: workerVersion);
        return Response.Success(
            $"Telegram is connected to Worker v{workerVersion} - both bots are registered.\n" +
            "Next step: create a pairing code for each bot below.");
    }

    private static (bool Ok, string? MonkeyToken, string? FriendToken, string? Error)
        ValidateBotTokens(Request request)
    {
        var monkeyToken = request.MonkeyToken?.Trim();
        var friendToken = request.FriendToken?.Trim();
        if (monkeyToken is null || friendToken is null ||
            !TokenPattern.IsMatch(monkeyToken) || !TokenPattern.IsMatch(friendToken))
            return (false, null, null, "One of the bot tokens doesn't look like a token from @BotFather.");
        if (monkeyToken == friendToken)
            return (false, null, null, "The two bots need different tokens - the same one was entered twice.");

        return (true, monkeyToken, friendToken, null);
    }

    private static async Task ProvisionBotsAsync(string url, string secret)
    {
        using var _ = await PostAsync(
            $"{url}/provision", secret, new { }, CancellationToken.None);
    }

    /// <summary>Kompatibilitaet mit dem bisherigen Worker, der Tokens in KV ablegte.</summary>
    private static async Task ProvisionLegacyBotsAsync(
        string url, string secret, string monkeyToken, string friendToken)
    {
        var body = new
        {
            monkeyToken,
            friendToken,
            monkeyWebhookSecret = NewWebhookSecret(),
            friendWebhookSecret = NewWebhookSecret(),
        };

        using var _ = await PostAsync($"{url}/provision", secret, body, CancellationToken.None);
    }

    /// <summary>
    /// Selbsttaetiges Worker-Update, ohne Zutun und ohne Passwort: es kann nur
    /// die Fassung installieren, die in diesem Dienst eingebettet ist - dieselbe
    /// Schranke, die auch der Knopf hat. Laeuft nur, wenn bei Einrichtung oder
    /// letztem Hand-Update ein Cloudflare-Token hinterlegt wurde.
    /// </summary>
    private async Task MaybeUpdateWorkerAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (_nextWorkerVersionCheck == DateTimeOffset.MinValue)
        {
            _nextWorkerVersionCheck = now + FirstWorkerVersionCheckDelay;
            return;
        }
        if (now < _nextWorkerVersionCheck) return;
        _nextWorkerVersionCheck = now + WorkerVersionCheckInterval;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled || !settings.Managed ||
            settings.WorkerUrl is null || settings.SyncSecretProtected is null ||
            settings.ApiTokenProtected is null || settings.CloudflareAccountId is null)
            return;

        // Schon auf Stand? Die /info-Abfrage ist billig und entscheidet alles Weitere.
        if (settings.WorkerVersion >= CurrentWorkerVersion) return;
        if (_workerUpdateFailedFor >= CurrentWorkerVersion) return;

        try
        {
            var apiToken = Dpapi.Unprotect(settings.ApiTokenProtected);

            Log.Write($"Worker v{settings.WorkerVersion} is older than the embedded v{CurrentWorkerVersion} - updating in the background.");
            var result = await UpdateWorkerCoreAsync(settings, settings.CloudflareAccountId, apiToken);

            if (result.Ok)
            {
                Log.Write($"Background worker update done: {result.Message}");
            }
            else
            {
                _workerUpdateFailedFor = CurrentWorkerVersion;
                Log.Write($"Background worker update failed: {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _workerUpdateFailedFor = CurrentWorkerVersion;
            Log.Write($"Background worker update failed: {Shorten(ex.Message, 200)}");
        }
    }

    private async Task<Response> CheckWorkerAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled || settings.WorkerUrl is null || settings.SyncSecretProtected is null)
            return Response.Fail("Connect Telegram first.");

        var secret = Dpapi.Unprotect(settings.SyncSecretProtected);
        var version = await GetWorkerVersionAsync(settings.WorkerUrl, secret);
        var maintenance = version switch
        {
            CurrentWorkerVersion => "The Worker is up to date.",
            < CurrentWorkerVersion when settings.ApiTokenProtected is not null =>
                "An update is available and installs itself within a few hours. The button below does the same thing right now.",
            < CurrentWorkerVersion =>
                "An update is available. Paste a Cloudflare token below once - Monkey keeps it encrypted, and every later update installs itself.",
            _ => "The Worker is newer than this Monkey installation. Update Monkey before changing it.",
        };

        return Response.Success($"Worker v{version}; this app provides v{CurrentWorkerVersion}. {maintenance}");
    }

    private async Task<Response> UpdateWorkerAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled || settings.WorkerUrl is null || settings.SyncSecretProtected is null)
            return Response.Fail("Connect Telegram first.");

        // Ohne eingegebenes Token tut es das gespeicherte - so laesst sich der
        // Knopf auch druecken, wenn die Selbstaktualisierung nur noch nicht
        // dran war.
        string apiToken;
        if (!string.IsNullOrWhiteSpace(request.CloudflareApiToken))
            apiToken = ValidateApiToken(request.CloudflareApiToken);
        else if (settings.ApiTokenProtected is { } storedToken)
            apiToken = Dpapi.Unprotect(storedToken);
        else
            return Response.Fail(
                "No Cloudflare token is stored yet. Paste one once - Monkey keeps it encrypted, and every later update installs itself.");

        var accountId = string.IsNullOrWhiteSpace(request.CloudflareAccountId) && settings.CloudflareAccountId is { } known
            ? known
            : ValidateAccountId(request.CloudflareAccountId);
        if (settings.CloudflareAccountId is { } storedAccount &&
            !string.Equals(storedAccount, accountId, StringComparison.OrdinalIgnoreCase))
            return Response.Fail("This Account ID does not match the account used to deploy the Worker.");

        var result = await UpdateWorkerCoreAsync(settings, accountId, apiToken);
        if (result.Ok) engine.StoreTelegramApiToken(Dpapi.Protect(apiToken));
        return result;
    }

    /// <summary>
    /// Der eigentliche Update-Weg, geteilt zwischen Knopf und Hintergrund. Wer
    /// hierher kommt, ist schon autorisiert und hat Konto und Token in der Hand.
    /// </summary>
    private async Task<Response> UpdateWorkerCoreAsync(TelegramConfigView settings, string accountId, string apiToken)
    {
        var (expectedScript, namespaceTitle) = ManagedNames();
        var scriptName = settings.ScriptName ?? expectedScript;
        if (!WorkerUrlMatchesScript(settings.WorkerUrl!, scriptName) ||
            (!settings.Managed && !string.Equals(scriptName, expectedScript, StringComparison.Ordinal)))
            return Response.Fail(
                "Automatic updates are only available for Workers deployed by Monkey. Update this custom Worker in Cloudflare.");

        var secret = Dpapi.Unprotect(settings.SyncSecretProtected!);
        var version = await GetWorkerVersionAsync(settings.WorkerUrl!, secret);
        if (version > CurrentWorkerVersion)
            return Response.Fail("The Worker is newer than this Monkey installation. Update Monkey first.");
        if (version == CurrentWorkerVersion)
            return Response.Success($"Worker v{version} is already up to date.");

        var namespaceId = settings.KvNamespaceId ??
                          (await EnsureKvNamespaceAsync(accountId, apiToken, namespaceTitle)).Id;

        if (version < 2)
        {
            // Worker v1 hielt Bot- und Webhook-Tokens noch in KV. Sie werden nur
            // im Arbeitsspeicher gelesen, als Secret-Bindings hochgeladen und
            // anschliessend durch /provision aus KV entfernt.
            var legacy = await ReadLegacyWorkerSecretsAsync(accountId, apiToken, namespaceId, secret);
            await UploadWorkerAsync(accountId, apiToken, scriptName, namespaceId, legacy);
            await WaitForWorkerAsync(settings.WorkerUrl!);
            await ProvisionBotsAsync(settings.WorkerUrl!, secret);
        }
        else
        {
            // Ab v2 bleiben alle Bindings unangetastet; nur der eingebettete Code
            // wird ersetzt. So muessen Secret-Werte nie wieder ausgelesen werden.
            await UploadWorkerContentAsync(accountId, apiToken, scriptName);
            await WaitForWorkerAsync(settings.WorkerUrl!);
        }

        var installedVersion = await GetWorkerVersionAsync(settings.WorkerUrl!, secret);
        if (installedVersion != CurrentWorkerVersion)
            return Response.Fail("Cloudflare accepted the upload, but the expected Worker version is not active yet.");

        engine.SetTelegram(
            true, settings.WorkerUrl, settings.SyncSecretProtected, true,
            accountId, scriptName, namespaceId, installedVersion);
        return Response.Success(
            $"Worker updated to v{installedVersion}. Pairings, status and queued commands were preserved.");
    }

    private async Task<Response> RemoveWorkerAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled || !settings.Managed || settings.WorkerUrl is null ||
            settings.SyncSecretProtected is null || settings.ScriptName is null ||
            settings.KvNamespaceId is null)
            return Response.Fail("Only a Worker deployed and managed by Monkey can be removed automatically.");

        var accountId = string.IsNullOrWhiteSpace(request.CloudflareAccountId)
            ? ValidateAccountId(settings.CloudflareAccountId)
            : ValidateAccountId(request.CloudflareAccountId);
        var apiToken = string.IsNullOrWhiteSpace(request.CloudflareApiToken) && settings.ApiTokenProtected is { } storedToken
            ? Dpapi.Unprotect(storedToken)
            : ValidateApiToken(request.CloudflareApiToken);
        if (settings.CloudflareAccountId is { } storedAccount &&
            !string.Equals(storedAccount, accountId, StringComparison.OrdinalIgnoreCase))
            return Response.Fail("This Account ID does not match the account used to deploy the Worker.");

        // Zuerst Webhooks ordentlich abmelden, dann exakt die gespeicherten
        // Cloudflare-Ressourcen entfernen. Keine Namenssuche, keine Wildcards.
        try
        {
            var secret = Dpapi.Unprotect(settings.SyncSecretProtected);
            using var _ = await PostAsync(
                $"{settings.WorkerUrl}/reset", secret, new { }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write($"Telegram removal: webhook reset failed ({Shorten(ex.Message, 120)}); deleting resources anyway.");
        }

        var cleanupErrors = new List<string>();
        try
        {
            using var _ = await CloudflareAsync(HttpMethod.Delete,
                $"accounts/{accountId}/workers/scripts/{settings.ScriptName}", apiToken);
        }
        catch (Exception ex)
        {
            cleanupErrors.Add($"Worker: {Shorten(ex.Message, 100)}");
        }

        try
        {
            using var _ = await CloudflareAsync(HttpMethod.Delete,
                $"accounts/{accountId}/storage/kv/namespaces/{settings.KvNamespaceId}", apiToken);
        }
        catch (Exception ex)
        {
            cleanupErrors.Add($"KV: {Shorten(ex.Message, 100)}");
        }

        // Lokal in jedem Fall trennen: Nach einem bestaetigten Loeschversuch darf
        // ein teilweise entfernter Worker nicht weiter als aktive Steuerung gelten.
        engine.SetTelegram(false, null, null);
        if (cleanupErrors.Count > 0)
            return Response.Fail(
                "Telegram was disconnected locally, but Cloudflare cleanup was incomplete: " +
                string.Join("; ", cleanupErrors) + ". Open the Cloudflare dashboard to remove the remainder.");

        return Response.Success(
            "Telegram disconnected and the managed Cloudflare Worker plus its KV store were deleted. The API token was not stored and can be revoked now.");
    }

    private static string ValidateAccountId(string? value)
    {
        var accountId = value?.Trim();
        if (accountId is null || !AccountIdPattern.IsMatch(accountId))
            throw new InvalidOperationException("The Cloudflare Account ID must contain exactly 32 hexadecimal characters.");
        return accountId;
    }

    private static string ValidateApiToken(string? value)
    {
        var apiToken = value?.Trim();
        if (apiToken is null || apiToken.Length is < 20 or > 512)
            throw new InvalidOperationException("The Cloudflare API token looks incomplete.");
        return apiToken;
    }

    private static (string ScriptName, string NamespaceTitle) ManagedNames()
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))
            [..10].ToLowerInvariant();
        return ($"monkey-telegram-{suffix}", $"Monkey Telegram {suffix}");
    }

    private static bool WorkerUrlMatchesScript(string workerUrl, string scriptName) =>
        Uri.TryCreate(workerUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.StartsWith($"{scriptName}.", StringComparison.OrdinalIgnoreCase) &&
        uri.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForWorkerAsync(string workerUrl)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(workerUrl);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // DNS und Worker-Deployment brauchen gelegentlich ein paar Sekunden.
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException(
            "Cloudflare accepted the Worker, but its workers.dev address did not become reachable yet. Try setup again in a moment.");
    }

    // ---------------------------------------------------- Cloudflare deployment

    private static async Task<bool> WorkerExistsAsync(
        string accountId, string apiToken, string scriptName)
    {
        using var list = await CloudflareAsync(
            HttpMethod.Get, $"accounts/{accountId}/workers/scripts?per_page=1000", apiToken);
        if (!list.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Cloudflare returned no usable Worker list.");

        return result.EnumerateArray().Any(item =>
            string.Equals(ReadString(item, "id"), scriptName, StringComparison.Ordinal));
    }

    private static async Task<KvNamespaceInfo> EnsureKvNamespaceAsync(
        string accountId, string apiToken, string title)
    {
        using (var list = await CloudflareAsync(HttpMethod.Get,
                   $"accounts/{accountId}/storage/kv/namespaces?per_page=1000", apiToken))
        {
            if (list.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    var existingTitle = ReadString(item, "title");
                    var existingId = ReadString(item, "id");
                    if (existingId is not null && string.Equals(existingTitle, title, StringComparison.Ordinal))
                        return new KvNamespaceInfo(existingId, false);
                }
            }
        }

        using var created = await CloudflareAsync(HttpMethod.Post,
            $"accounts/{accountId}/storage/kv/namespaces", apiToken, new { title });
        if (created.RootElement.TryGetProperty("result", out var createdResult) &&
            ReadString(createdResult, "id") is { } id)
            return new KvNamespaceInfo(id, true);

        throw new InvalidOperationException("Cloudflare created no usable KV namespace.");
    }

    private static async Task UploadWorkerAsync(
        string accountId,
        string apiToken,
        string scriptName,
        string namespaceId,
        WorkerSecrets secrets)
    {
        using var workerStream = typeof(TelegramSync).Assembly.GetManifestResourceStream("telegram-worker.js")
            ?? throw new InvalidOperationException("The embedded Telegram worker is missing from this build.");

        var metadata = JsonSerializer.Serialize(new
        {
            main_module = "worker.js",
            compatibility_date = WorkerCompatibilityDate,
            bindings = new object[]
            {
                new { type = "kv_namespace", name = "KV", namespace_id = namespaceId },
                new { type = "secret_text", name = "SYNC_SECRET", text = secrets.SyncSecret },
                new { type = "secret_text", name = "MONKEY_BOT_TOKEN", text = secrets.MonkeyToken },
                new { type = "secret_text", name = "FRIEND_BOT_TOKEN", text = secrets.FriendToken },
                new { type = "secret_text", name = "MONKEY_WEBHOOK_SECRET", text = secrets.MonkeyWebhookSecret },
                new { type = "secret_text", name = "FRIEND_WEBHOOK_SECRET", text = secrets.FriendWebhookSecret },
            },
        }, Json);

        using var multipart = new MultipartFormDataContent();
        var metadataContent = new StringContent(metadata, Encoding.UTF8, "application/json");
        multipart.Add(metadataContent, "metadata");

        var workerContent = new StreamContent(workerStream);
        workerContent.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        multipart.Add(workerContent, "worker.js", "worker.js");

        using var _ = await CloudflareAsync(HttpMethod.Put,
            $"accounts/{accountId}/workers/scripts/{scriptName}", apiToken, multipart);
    }

    /// <summary>
    /// Aktualisiert ausschliesslich den Worker-Code. Cloudflares Content-API
    /// laesst dabei Konfiguration, KV- und Secret-Bindings unveraendert.
    /// </summary>
    private static async Task UploadWorkerContentAsync(
        string accountId, string apiToken, string scriptName)
    {
        using var workerStream = typeof(TelegramSync).Assembly.GetManifestResourceStream("telegram-worker.js")
            ?? throw new InvalidOperationException("The embedded Telegram worker is missing from this build.");

        var metadata = JsonSerializer.Serialize(new { main_module = "worker.js" }, Json);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");

        var workerContent = new StreamContent(workerStream);
        workerContent.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        multipart.Add(workerContent, "worker.js", "worker.js");

        using var _ = await CloudflareAsync(HttpMethod.Put,
            $"accounts/{accountId}/workers/scripts/{scriptName}/content", apiToken, multipart);
    }

    private static async Task<WorkerSecrets> ReadLegacyWorkerSecretsAsync(
        string accountId, string apiToken, string namespaceId, string syncSecret)
    {
        var text = await CloudflareRawAsync(
            HttpMethod.Get,
            $"accounts/{accountId}/storage/kv/namespaces/{namespaceId}/values/config",
            apiToken);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var monkeyToken = ReadString(root, "monkeyToken");
            var friendToken = ReadString(root, "friendToken");
            if (monkeyToken is null || friendToken is null ||
                !TokenPattern.IsMatch(monkeyToken) || !TokenPattern.IsMatch(friendToken) ||
                monkeyToken == friendToken)
                throw new InvalidOperationException(
                    "The legacy KV configuration contains no usable bot tokens. Reconnect the bots with a fresh automatic deployment.");

            var monkeyWebhook = ReadString(root, "monkeyHookSecret");
            var friendWebhook = ReadString(root, "friendHookSecret");
            if (monkeyWebhook is null || !WebhookSecretPattern.IsMatch(monkeyWebhook))
                monkeyWebhook = NewWebhookSecret();
            if (friendWebhook is null || !WebhookSecretPattern.IsMatch(friendWebhook))
                friendWebhook = NewWebhookSecret();

            return new WorkerSecrets(
                syncSecret, monkeyToken, friendToken, monkeyWebhook, friendWebhook);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "The legacy KV configuration is unreadable. Reconnect the bots with a fresh automatic deployment.");
        }
    }

    private static async Task<int> GetWorkerVersionAsync(string workerUrl, string syncSecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{workerUrl.TrimEnd('/')}/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", syncSecret);
        using var response = await Http.SendAsync(request, CancellationToken.None);

        // Worker v1 hatte noch keine /info-Schnittstelle und antwortet hier mit
        // 404/405. Das ist die eindeutige, rueckwaertskompatible Kennung.
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.MethodNotAllowed)
            return 1;
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The Worker rejected the stored sync secret.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The Worker version check returned {(int)response.StatusCode}.");

        var text = await response.Content.ReadAsStringAsync();
        if (text.Length > 4096)
            throw new InvalidOperationException("The Worker returned an unexpectedly large version response.");

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("version", out var value) &&
                value.TryGetInt32(out var version) && version is >= 1 and <= 1000)
                return version;
        }
        catch (JsonException) { }

        throw new InvalidOperationException("The Worker returned no valid version information.");
    }

    private static async Task EnableWorkerSubdomainAsync(
        string accountId, string apiToken, string scriptName)
    {
        using var _ = await CloudflareAsync(HttpMethod.Post,
            $"accounts/{accountId}/workers/scripts/{scriptName}/subdomain",
            apiToken,
            new { enabled = true, previews_enabled = false });
    }

    private static async Task<string> GetAccountSubdomainAsync(string accountId, string apiToken)
    {
        using var doc = await CloudflareAsync(HttpMethod.Get,
            $"accounts/{accountId}/workers/subdomain", apiToken);
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            ReadString(result, "subdomain") is { Length: > 0 } subdomain)
            return subdomain;

        throw new InvalidOperationException(
            "No workers.dev subdomain is configured for this Cloudflare account. " +
            "Open Workers & Pages once in Cloudflare, choose the account subdomain, then try again.");
    }

    private static Task<JsonDocument> CloudflareAsync(
        HttpMethod method, string path, string apiToken, object? body = null)
    {
        HttpContent? content = body switch
        {
            null => null,
            HttpContent httpContent => httpContent,
            _ => new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        return CloudflareAsync(method, path, apiToken, content);
    }

    private static async Task<string> CloudflareRawAsync(
        HttpMethod method, string path, string apiToken)
    {
        using var request = new HttpRequestMessage(
            method, $"https://api.cloudflare.com/client/v4/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        using var response = await CloudflareHttp.SendAsync(request, CancellationToken.None);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length > 1024 * 1024)
            throw new InvalidOperationException("Cloudflare returned an unexpectedly large response.");
        if (response.IsSuccessStatusCode)
            return text;

        throw new InvalidOperationException(
            $"Cloudflare setup failed ({(int)response.StatusCode}) while reading the legacy Worker configuration.");
    }

    private static async Task TryDeleteCloudflareAsync(string path, string apiToken)
    {
        try
        {
            using var _ = await CloudflareAsync(HttpMethod.Delete, path, apiToken);
        }
        catch (Exception ex)
        {
            Log.Write($"Cloudflare rollback failed: {Shorten(ex.Message, 140)}");
        }
    }

    private static async Task<JsonDocument> CloudflareAsync(
        HttpMethod method, string path, string apiToken, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, $"https://api.cloudflare.com/client/v4/{path}")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        using var response = await CloudflareHttp.SendAsync(request, CancellationToken.None);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length > 1024 * 1024)
            throw new InvalidOperationException("Cloudflare returned an unexpectedly large response.");
        if (response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text))
            return JsonDocument.Parse("{\"success\":true,\"result\":null}");

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { /* klare Fehlermeldung unten */ }

        var success = doc is not null &&
                      doc.RootElement.TryGetProperty("success", out var successValue) &&
                      successValue.ValueKind == JsonValueKind.True;
        if (response.IsSuccessStatusCode && success)
            return doc!;

        var message = "request rejected";
        if (doc is not null && doc.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            message = ReadString(errors[0], "message") ?? message;

        doc?.Dispose();
        throw new InvalidOperationException(
            $"Cloudflare setup failed ({(int)response.StatusCode}): {Shorten(message, 180)}");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record WorkerSecrets(
        string SyncSecret,
        string MonkeyToken,
        string FriendToken,
        string MonkeyWebhookSecret,
        string FriendWebhookSecret);

    private sealed record KvNamespaceInfo(string Id, bool Created);

    private async Task<Response> PairAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled || settings.WorkerUrl is null || settings.SyncSecretProtected is null)
            return Response.Fail("Connect Telegram first (worker address and bot tokens).");

        var role = request.PairRole?.Trim().ToLowerInvariant();
        if (role is not ("monkey" or "friend"))
            return Response.Fail("Unknown pairing role.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var secret = Dpapi.Unprotect(settings.SyncSecretProtected);

        using var _ = await PostAsync($"{settings.WorkerUrl}/pair", secret,
            new { role, code, ttlSeconds = 600 }, CancellationToken.None);

        var who = role == "friend" ? "the friend's" : "Monkey's";
        return Response.Success(
            $"Pairing code for {who} bot:  {code}\n" +
            $"Send   /pair {code}   to that bot within 10 minutes. The code works once.");
    }

    private async Task<Response> DisableAsync(Request request)
    {
        var auth = engine.Authorize(request.Password);
        if (!auth.Ok) return auth;

        var settings = engine.TelegramConfig();
        if (!settings.Enabled) return Response.Fail("Telegram isn't connected.");

        // Erst Webhooks und KV-Daten leeren, dann lokal vergessen. Secret-Bindings
        // kann der Worker selbst nicht entfernen; dafuer gibt es den vollstaendigen
        // Abbau mit einem frischen Cloudflare-Token.
        var wiped = false;
        try
        {
            var secret = Dpapi.Unprotect(settings.SyncSecretProtected!);
            using var _ = await PostAsync($"{settings.WorkerUrl}/reset", secret, new { }, CancellationToken.None);
            wiped = true;
        }
        catch (Exception ex)
        {
            Log.Write($"Telegram teardown: worker wipe failed ({Shorten(ex.Message, 120)}).");
        }

        engine.SetTelegram(false, null, null);
        return Response.Success(wiped
            ? "Telegram disconnected. Webhooks and KV data were removed. The Worker and its encrypted secret bindings remain in Cloudflare; use 'Remove Worker & data' for complete deletion."
            : "Telegram disconnected locally. The Worker could not be reset; delete it in the Cloudflare dashboard.");
    }

    // -------------------------------------------------------------- Werkzeug

    private static async Task<JsonDocument?> PostAsync(string url, string secret, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var response = await Http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "The worker rejected the sync secret. Is the same value stored in Cloudflare as SYNC_SECRET?");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The worker answered with {(int)response.StatusCode}: {Shorten(text, 160)}");

        // Der Worker antwortet knapp; alles darueber hinaus ist verdaechtig.
        if (text.Length > 65536)
            throw new InvalidOperationException("The worker's answer was unreasonably large.");

        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private static List<RemoteCommand> ParseCommands(JsonDocument? doc)
    {
        var result = new List<RemoteCommand>();
        if (doc is null ||
            doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("commands", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var element in array.EnumerateArray().Take(20))
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var id = element.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var minutes = element.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var value) ? value : 0;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type)) continue;
            result.Add(new RemoteCommand(id, type, minutes));
        }

        return result;
    }

    /// <summary>Zeichenvorrat, den Telegram fuer secret_token erlaubt (Hex passt).</summary>
    private static string NewWebhookSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string NewSyncSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max] + "…";
    }
}
