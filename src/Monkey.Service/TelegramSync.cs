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
internal sealed class TelegramSync(GuardEngine engine) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient CloudflareHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex TokenPattern = new(@"^\d{5,12}:[A-Za-z0-9_\-]{30,64}$", RegexOptions.Compiled);
    private static readonly Regex WebhookSecretPattern = new(@"^[A-Za-z0-9_\-]{16,128}$", RegexOptions.Compiled);
    private static readonly Regex AccountIdPattern = new(@"^[a-fA-F0-9]{32}$", RegexOptions.Compiled);
    private const string WorkerCompatibilityDate = "2024-11-01";
    private const int CurrentWorkerVersion = 3;

    /// <summary>
    /// Quittungen, die den Worker noch nicht erreicht haben. Bleiben liegen, bis
    /// ein Abgleich klappt - erst mit der Quittung loescht der Worker den Befehl
    /// aus seiner Warteschlange und benachrichtigt den Absender.
    /// </summary>
    private readonly List<RemoteResult> _unsentResults = [];

    private int _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Weckruf durch die Engine (Guthaben geaendert) oder regulaerer Takt.
                try { await engine.TelegramKick.WaitAsync(SyncInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }

                try { await SyncOnceAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // Letzter Stand beim Herunterfahren - genau davon lebt die Abfrage bei
            // ausgeschaltetem PC. Nur ein Versuch, mit knapper Frist.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await SyncOnceAsync(timeout.Token); }
            catch (Exception) { /* Best effort - das Herunterfahren wartet nicht. */ }
        }
    }

    private async Task SyncOnceAsync(CancellationToken token)
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

        try
        {
            // Bis zu drei Runden: Stand melden und Befehle holen, Befehle anwenden,
            // Quittungen sofort nachreichen (statt erst im naechsten Takt).
            for (var round = 0; round < 3; round++)
            {
                var body = new { state = engine.BuildTelegramSnapshot(), results = _unsentResults.ToArray() };
                using var doc = await PostAsync($"{settings.WorkerUrl}/sync", secret, body, token);
                _unsentResults.Clear();

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
            $"Cloudflare Worker v{CurrentWorkerVersion} deployed and Telegram connected. The API token was not stored; bot and webhook tokens are encrypted Cloudflare secrets.\n" +
            "You can revoke the one-time Cloudflare API token now. Next: create one pairing code for each bot below.");
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
            < CurrentWorkerVersion => "An update is available. Use the one-time Cloudflare token below; chats, state and queued commands will be preserved.",
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

        var accountId = ValidateAccountId(request.CloudflareAccountId);
        var apiToken = ValidateApiToken(request.CloudflareApiToken);
        if (settings.CloudflareAccountId is { } storedAccount &&
            !string.Equals(storedAccount, accountId, StringComparison.OrdinalIgnoreCase))
            return Response.Fail("This Account ID does not match the account used to deploy the Worker.");

        var (expectedScript, namespaceTitle) = ManagedNames();
        var scriptName = settings.ScriptName ?? expectedScript;
        if (!WorkerUrlMatchesScript(settings.WorkerUrl, scriptName) ||
            (!settings.Managed && !string.Equals(scriptName, expectedScript, StringComparison.Ordinal)))
            return Response.Fail(
                "Automatic updates are only available for Workers deployed by Monkey. Update this custom Worker in Cloudflare.");

        var secret = Dpapi.Unprotect(settings.SyncSecretProtected);
        var version = await GetWorkerVersionAsync(settings.WorkerUrl, secret);
        if (version > CurrentWorkerVersion)
            return Response.Fail("The Worker is newer than this Monkey installation. Update Monkey first.");
        if (version == CurrentWorkerVersion)
            return Response.Success($"Worker v{version} is already up to date. The Cloudflare token was not stored.");

        var namespaceId = settings.KvNamespaceId ??
                          (await EnsureKvNamespaceAsync(accountId, apiToken, namespaceTitle)).Id;

        if (version < 2)
        {
            // Worker v1 hielt Bot- und Webhook-Tokens noch in KV. Sie werden nur
            // im Arbeitsspeicher gelesen, als Secret-Bindings hochgeladen und
            // anschliessend durch /provision aus KV entfernt.
            var legacy = await ReadLegacyWorkerSecretsAsync(accountId, apiToken, namespaceId, secret);
            await UploadWorkerAsync(accountId, apiToken, scriptName, namespaceId, legacy);
            await WaitForWorkerAsync(settings.WorkerUrl);
            await ProvisionBotsAsync(settings.WorkerUrl, secret);
        }
        else
        {
            // Ab v2 bleiben alle Bindings unangetastet; nur der eingebettete Code
            // wird ersetzt. So muessen Secret-Werte nie wieder ausgelesen werden.
            await UploadWorkerContentAsync(accountId, apiToken, scriptName);
            await WaitForWorkerAsync(settings.WorkerUrl);
        }

        var installedVersion = await GetWorkerVersionAsync(settings.WorkerUrl, secret);
        if (installedVersion != CurrentWorkerVersion)
            return Response.Fail("Cloudflare accepted the upload, but the expected Worker version is not active yet.");

        engine.SetTelegram(
            true, settings.WorkerUrl, settings.SyncSecretProtected, true,
            accountId, scriptName, namespaceId, installedVersion);
        return Response.Success(
            $"Worker updated to v{installedVersion}. Pairings, status and queued commands were preserved; the API token was not stored and can be revoked now.");
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

        var accountId = ValidateAccountId(request.CloudflareAccountId);
        var apiToken = ValidateApiToken(request.CloudflareApiToken);
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
