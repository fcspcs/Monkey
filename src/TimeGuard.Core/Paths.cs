namespace TimeGuard.Core;

/// <summary>
/// Zentrale Ablageorte. Alles Zustandsbehaftete liegt unter %ProgramData%\TimeGuard,
/// damit der Ordner per ACL gegen den angemeldeten Benutzer abgedichtet werden kann.
/// </summary>
public static class Paths
{
    public const string DefaultPipeName = "TimeGuard.v1";
    public const string ServiceName = "TimeGuardSrv";

    /// <summary>Sitzungslokal: pro angemeldetem Benutzer laeuft genau ein Agent.</summary>
    public const string MutexName = @"Local\TimeGuard.Agent";

    private static string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeGuard");

    private static string _pipeName = DefaultPipeName;

    public static string DataDir => _dataDir;
    public static string PipeName => _pipeName;

    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string StateBackup => Path.Combine(DataDir, "state.bak");
    public static string LogFile => Path.Combine(DataDir, "timeguard.log");

    /// <summary>
    /// Solange diese Datei existiert, faehrt der Selbstschutz nichts wieder hoch.
    /// Sie wird beim autorisierten Teardown gesetzt, damit ein Neustart oder ein
    /// Watchdog-Tick mitten in der Deinstallation nicht alles neu sperrt.
    /// </summary>
    public static string TeardownMarker => Path.Combine(DataDir, ".teardown");

    /// <summary>
    /// Nur fuer Testlaeufe aus der Konsole. Der installierte Dienst kann das nicht
    /// bekommen: seine Befehlszeile steht in der Dienstkonfiguration, und die ist
    /// per SDDL gegen Aenderung gesperrt. Ein zweiter, umgeleiteter Prozess haelt
    /// den echten Dienst ausserdem nicht auf.
    /// </summary>
    public static void UseTestLocation(string? dataDir, string? pipeName)
    {
        if (!string.IsNullOrWhiteSpace(dataDir)) _dataDir = Path.GetFullPath(dataDir);
        if (!string.IsNullOrWhiteSpace(pipeName)) _pipeName = pipeName;
    }
}
