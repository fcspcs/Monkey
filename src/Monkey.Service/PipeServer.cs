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
internal sealed class PipeServer(GuardEngine engine) : BackgroundService
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
                Log.Write($"Pipe-Fehler: {ex.Message}");
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
            response = request is null
                ? Response.Fail("Anfrage nicht lesbar.")
                : engine.Handle(request);
        }
        catch (Exception ex)
        {
            Log.Write($"Anfrage fehlgeschlagen: {ex.Message}");
            response = Response.Fail("Interner Fehler im Dienst.");
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
