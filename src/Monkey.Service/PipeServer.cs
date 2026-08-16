using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Monkey.Core;

namespace Monkey.Service;

/// <summary>
/// Named Pipe, ueber die der Agent Status abruft und Anfragen stellt. Der Agent hat
/// keinerlei eigene Befugnis - er reicht nur das Passwort durch, geprueft wird im
/// Dienst.
/// </summary>
internal sealed class PipeServer(GuardEngine engine, TelegramSync telegram) : BackgroundService
{
    private const int Instances = 6;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Instances).Select(_ => AcceptLoop(stoppingToken)));

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(token);
                await ServeAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"Pipe error: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(token);
        if (string.IsNullOrWhiteSpace(line)) return;

        Response response;
        try
        {
            var request = Request.FromJson(line);

            // Telegram-Anfragen reden mit dem Worker im Netz und laufen deshalb
            // nicht durch das Engine-Lock - die Passwortpruefung holen sie sich
            // selbst bei der Engine ab.
            response = request switch
            {
                null => Response.Fail("Request could not be read."),
                { Type: RequestType.TelegramSetup or RequestType.TelegramDeploy or
                        RequestType.TelegramWorkerCheck or RequestType.TelegramWorkerUpdate or
                        RequestType.TelegramWorkerRemove or RequestType.TelegramPair or
                        RequestType.TelegramOff }
                    => await telegram.HandleAsync(request),
                _ => engine.Handle(request),
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Request failed: {ex.Message}");
            response = Response.Fail("Internal error in the service.");
        }

        await writer.WriteLineAsync(response.ToJson().ReplaceLineEndings(" "));
        pipe.WaitForPipeDrain();
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        // Wer die Pipe anlegt, braucht CreateNewInstance - sonst entsteht nur die
        // erste Instanz und alle weiteren scheitern. Im Dienstbetrieb ist das
        // SYSTEM und damit schon abgedeckt; beim Konsolentest der aufrufende Benutzer.
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } owner)
            security.AddAccessRule(new PipeAccessRule(
                owner, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            Paths.PipeName, PipeDirection.InOut, Instances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 16384, security);
    }
}
