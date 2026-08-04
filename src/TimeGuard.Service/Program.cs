using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using TimeGuard.Core;
using TimeGuard.Service;

if (args.Length > 0 && !args[0].StartsWith('-'))
    return Cli.Run(args);

var runningAsService = WindowsServiceHelpers.IsWindowsService();

if (runningAsService)
{
    // Bei jedem Start alle Riegel wieder aufrichten. Wer einen entfernt hat,
    // findet ihn nach dem naechsten Start - oder dem naechsten Watchdog-Tick -
    // wieder vor.
    SelfProtect.ApplyAll();
}
#if DEBUG
else
{
    // Umleitung und Trockenlauf ausschliesslich fuer Testlaeufe. Dieser Zweig
    // existiert nur im Debug-Build; das ausgelieferte Programm hat ihn nicht.
    Paths.UseTestLocation(ArgumentValue("--data-dir"), ArgumentValue("--pipe"));
    RunMode.DryRunLogoff = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
}
#endif

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = Paths.ServiceName);
builder.Services.AddSingleton<GuardEngine>();
builder.Services.AddHostedService<GuardWorker>();
builder.Services.AddHostedService<PipeServer>();

try
{
    builder.Build().Run();
}
catch (Exception ex)
{
    Log.Write($"Dienst abgestuerzt: {ex}");
    throw;
}

return 0;

#if DEBUG
string? ArgumentValue(string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
#endif
