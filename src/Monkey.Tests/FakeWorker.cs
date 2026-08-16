using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Monkey.Tests;

internal sealed record RecordedRequest(string Path, string? Authorization, JsonElement Body);

/// <summary>
/// Spielt den Cloudflare Worker: ein Kestrel auf 127.0.0.1 mit zufaelligem
/// Port. Zeichnet jede Anfrage auf und antwortet nach Drehbuch - damit laesst
/// sich die komplette PC-Seite des Telegram-Abgleichs ohne Netz durchspielen.
/// </summary>
internal sealed class FakeWorker : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _syncCalls;

    public string Url { get; private set; } = "";

    /// <summary>Antwort auf den n-ten /sync-Aufruf (1-basiert).</summary>
    public Func<int, object> SyncResponder { get; set; } = _ => new { commands = Array.Empty<object>() };

    public int PairStatus { get; set; } = StatusCodes.Status200OK;

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    private FakeWorker(WebApplication app) => _app = app;

    public static async Task<FakeWorker> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var worker = new FakeWorker(app);

        app.MapPost("/sync", async context =>
        {
            await worker.RecordAsync(context);
            var call = Interlocked.Increment(ref worker._syncCalls);
            await context.Response.WriteAsJsonAsync(worker.SyncResponder(call));
        });

        app.MapPost("/pair", async context =>
        {
            await worker.RecordAsync(context);
            context.Response.StatusCode = worker.PairStatus;
            await context.Response.WriteAsJsonAsync(new { ok = worker.PairStatus == StatusCodes.Status200OK });
        });

        app.MapPost("/reset", async context =>
        {
            await worker.RecordAsync(context);
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });

        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        worker.Url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return worker;
    }

    private async Task RecordAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync();

        JsonElement body = default;
        if (text.Length > 0)
        {
            using var doc = JsonDocument.Parse(text);
            body = doc.RootElement.Clone();
        }

        _requests.Enqueue(new RecordedRequest(
            context.Request.Path.Value ?? "",
            context.Request.Headers.Authorization.FirstOrDefault(),
            body));
    }

    public async Task<bool> WaitUntilAsync(
        Func<IReadOnlyList<RecordedRequest>, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(Requests)) return true;
            await Task.Delay(25);
        }

        return condition(Requests);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
