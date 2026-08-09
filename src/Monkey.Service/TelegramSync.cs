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
    public int MaxManualGrantMinutes { get; init; }
    public int MaxPauseMinutes { get; init; }
    public int EvolutionStage { get; init; }
    public bool Counting { get; init; }
    public double PauseRemainingSeconds { get; init; }
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex TokenPattern = new(@"^\d{5,12}:[A-Za-z0-9_\-]{30,64}$", RegexOptions.Compiled);

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

        var monkeyToken = request.MonkeyToken?.Trim();
        var friendToken = request.FriendToken?.Trim();
        if (monkeyToken is null || friendToken is null ||
            !TokenPattern.IsMatch(monkeyToken) || !TokenPattern.IsMatch(friendToken))
            return Response.Fail("One of the bot tokens doesn't look like a token from @BotFather.");
        if (monkeyToken == friendToken)
            return Response.Fail("The two bots need different tokens - the same one was entered twice.");

        var url = rawUrl!;
        var body = new
        {
            monkeyToken,
            friendToken,
            monkeyWebhookSecret = NewWebhookSecret(),
            friendWebhookSecret = NewWebhookSecret(),
        };

        using var _ = await PostAsync($"{url}/provision", secret, body, CancellationToken.None);

        engine.SetTelegram(true, url, Dpapi.Protect(secret));
        return Response.Success(
            "Telegram is connected - both bots are registered with the worker.\n" +
            "Next step: create a pairing code for each bot below.");
    }

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

        // Erst den Worker leeren (loescht Webhooks, Tokens, Stand und Warteschlange),
        // dann lokal vergessen. Scheitert das Leeren, wird trotzdem getrennt - der
        // Hinweis sagt, wo aufzuraeumen ist.
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
            ? "Telegram disconnected. Webhooks removed and the worker's data wiped."
            : "Telegram disconnected locally. The worker couldn't be wiped - delete it (or its data) in the Cloudflare dashboard.");
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

    private static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max] + "…";
    }
}
