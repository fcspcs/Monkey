using Monkey.Core;

namespace Monkey.Service;

/// <summary>
/// Schlankes Dateilog. Bewusst ohne Abhaengigkeiten, damit auch Fehler beim
/// Hochfahren des Hosts noch irgendwo landen.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                Roll();
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Ein kaputtes Log darf den Dienst nie stoppen.
        }
        Console.WriteLine(line);
    }

    private static void Roll()
    {
        var info = new FileInfo(Paths.LogFile);
        if (!info.Exists || info.Length < MaxBytes) return;

        var archive = Paths.LogFile + ".1";
        File.Delete(archive);
        File.Move(Paths.LogFile, archive);
    }
}
