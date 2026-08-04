using System.IO;
using System.IO.Pipes;
using System.Text;
using TimeGuard.Core;

namespace TimeGuard.Agent;

/// <summary>
/// Duenner Draht zum Dienst. Eine Verbindung pro Anfrage - das ist bei einem
/// Zwei-Sekunden-Takt billig genug und erspart jede Zustandshaltung.
/// </summary>
internal static class PipeClient
{
    private const int ConnectTimeoutMs = 3000;

    public static async Task<Response?> SendAsync(Request request, CancellationToken token = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(ConnectTimeoutMs, token);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            await writer.WriteLineAsync(request.ToJson().ReplaceLineEndings(" "));

            var line = await reader.ReadLineAsync(token);
            return string.IsNullOrWhiteSpace(line) ? null : Response.FromJson(line);
        }
        catch (Exception)
        {
            // Dienst nicht erreichbar. Der Aufrufer zeigt das an; erzwungen wird
            // ohnehin im Dienst, nicht hier.
            return null;
        }
    }
}
