using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Monkey.Tests;

/// <summary>
/// Ueberspringt den Test, wenn kein Node.js da ist - die End-to-End-Tests
/// fahren den echten worker.js und brauchen es. In der CI ist Node immer da.
/// </summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (!WorkerHarness.NodeAvailable)
            Skip = "Node.js not found - the worker end-to-end tests need it.";
    }
}

/// <summary>
/// Startet cloud/worker.harness.mjs: der ECHTE worker.js hinter einem lokalen
/// HTTP-Server, mit Fake-Telegram und In-Memory-KV. Damit laufen die
/// End-to-End-Tests ueber exakt den Code, der spaeter bei Cloudflare liegt.
/// </summary>
internal sealed class WorkerHarness : IDisposable
{
    // Muss zu cloud/worker.harness.mjs passen.
    public const string SyncSecret = "harness-sync-secret-0123456789abcdef";
    public static readonly string MonkeyHookSecret = new('C', 32);
    public static readonly string FriendHookSecret = new('D', 32);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool NodeAvailable { get; } = DetectNode();

    private readonly Process _process;

    public string Url { get; }

    private WorkerHarness(Process process, string url)
    {
        _process = process;
        Url = url;
    }

    public static WorkerHarness Start()
    {
        var info = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = RepoRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(Path.Combine("cloud", "worker.harness.mjs"));

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("node could not be started.");

        try
        {
            var portLine = ReadPortLine(process, TimeSpan.FromSeconds(15));
            var port = int.Parse(portLine["HARNESS_PORT ".Length..]);
            return new WorkerHarness(process, $"http://127.0.0.1:{port}");
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    private static string ReadPortLine(Process process, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            while (process.StandardOutput.ReadLine() is { } line)
                if (line.StartsWith("HARNESS_PORT ", StringComparison.Ordinal))
                    return line;
            return null;
        });

        if (!read.Wait(timeout) || read.Result is null)
            throw new InvalidOperationException(
                $"The worker harness did not come up. stderr: {process.StandardError.ReadToEnd()}");
        return read.Result;
    }

    private static bool DetectNode()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            probe?.WaitForExit(10_000);
            return probe?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloud", "worker.js")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Repository root with cloud/worker.js not found.");
    }

    // ------------------------------------------------------ Anfragen im Test

    /// <summary>PC-Endpunkt mit Sync-Secret aufrufen, wie es der Dienst taete.</summary>
    public async Task PostAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SyncSecret);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Spielt Telegram: stellt ein Update an den Webhook des Bots zu.</summary>
    public async Task TelegramAsync(string role, long chatId, string text)
    {
        var update = new { message = new { text, chat = new { id = chatId, type = "private" } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/tg/{role}")
        {
            Content = new StringContent(JsonSerializer.Serialize(update, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-telegram-bot-api-secret-token",
            role == "monkey" ? MonkeyHookSecret : FriendHookSecret);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Alle vom Worker "an Telegram" geschickten sendMessage-Texte, in Reihenfolge.</summary>
    public async Task<List<(long ChatId, string Text)>> RepliesAsync()
    {
        var text = await Http.GetStringAsync($"{Url}/__harness/telegram");
        using var doc = JsonDocument.Parse(text);

        var replies = new List<(long, string)>();
        foreach (var call in doc.RootElement.EnumerateArray())
        {
            if (!call.GetProperty("url").GetString()!.EndsWith("/sendMessage")) continue;
            var body = call.GetProperty("body");
            replies.Add((body.GetProperty("chat_id").GetInt64(), body.GetProperty("text").GetString()!));
        }

        return replies;
    }

    public async Task<string> LastReplyAsync() => (await RepliesAsync()).LastOrDefault().Text ?? "";

    public void Dispose()
    {
        try { _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
