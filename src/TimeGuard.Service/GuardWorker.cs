using Microsoft.Extensions.Hosting;

namespace TimeGuard.Service;

/// <summary>
/// Taktgeber. Fuenf Sekunden sind fein genug fuer eine sekundengenaue Anzeige und
/// grob genug, um praktisch keine Last zu erzeugen.
/// </summary>
internal sealed class GuardWorker(GuardEngine engine) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            // Erster Durchlauf sofort, damit ein leeres Konto beim Hochfahren nicht
            // erst nach fuenf Sekunden auffaellt.
            engine.Tick(Interval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                engine.Tick(Interval);
        }
        catch (OperationCanceledException)
        {
            // Regulaeres Herunterfahren.
        }
        catch (Exception ex)
        {
            Log.Write($"Tick-Schleife abgebrochen: {ex}");
            throw;
        }
        finally
        {
            engine.Flush();
        }
    }
}
