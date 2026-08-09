using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Monkey.Core;

namespace Monkey.Setup;

/// <summary>
/// Die gesamte Installationslogik - ohne jede Oberflaeche. Fortschritt wird ueber
/// einen Rueckruf gemeldet, damit sowohl der Assistent als auch ein Konsolenlauf
/// dieselbe Logik nutzen koennen.
/// </summary>
internal static class SetupEngine
{
    public const string ServiceName = "MonkeySrv";
    public const string DisplayName = "Monkey screen time";
    private const string LegacyServiceName = "TimeGuardSrv";

    public static string TargetDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Monkey");

    private static readonly string[] PayloadFiles = ["MonkeyService.exe", "MonkeyAgent.exe"];

    // ------------------------------------------------------------ Zustandsabfragen

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool HasPayload()
    {
        var names = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames();
        return PayloadFiles.All(f => names.Contains(f));
    }

    public static bool ServiceInstalled() => ServiceKeyExists(ServiceName);

    /// <summary>Laeuft noch der Dienst der Vorgaengerversion "TimeGuard"?</summary>
    public static bool LegacyInstalled() => ServiceKeyExists(LegacyServiceName);

    /// <summary>
    /// Fragt den Dienst ueber die Dienststeuerung ab statt ueber die Registry.
    /// Der Registry-Schluessel eines laufenden Dienstes ist absichtlich gesperrt -
    /// wer ihn zum Erkennen benutzt, haelt den eigenen Dienst faelschlich fuer
    /// nicht vorhanden und ueberspringt dann den Abbau.
    /// </summary>
    private static bool ServiceKeyExists(string name)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("query");
            info.ArgumentList.Add(name);

            using var process = Process.Start(info);
            if (process is null) return false;

            process.WaitForExit(15000);

            // 0 = vorhanden, 1060 = nicht vorhanden.
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Liest den Zustand einer vorhandenen Installation - ueber das Sicherungs-
    /// Privileg und ohne die Rechte zu veraendern, damit ein Fehlversuch nichts
    /// hinterlaesst. Gibt null zurueck, wenn nichts Lesbares da ist.
    /// </summary>
    public static GuardState? TryReadExistingState()
    {
        var bytes = NativeSecurity.ReadPrivileged(Paths.StateFile);
        if (bytes is null || bytes.Length == 0) return null;
        try { return GuardState.FromJson(Encoding.UTF8.GetString(bytes)); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- Installieren

    public sealed record InstallOptions(string Password, int DailyMinutes, int CapMinutes, int MaxGrantMinutes);

    public static void Install(InstallOptions options, Action<string> report)
    {
        report("Clearing out leftovers from an earlier install …");
        CleanForFreshInstall(report);

        report("Writing program files …");
        ExtractPayload(TargetDir);

        report("Storing master password and settings …");
        WriteInitialState(options);

        report("Creating the service …");
        var serviceExe = Path.Combine(TargetDir, "MonkeyService.exe");
        Sc("create", ServiceName, "binPath=", serviceExe, "start=", "auto", "obj=", "LocalSystem",
            "DisplayName=", DisplayName);
        Sc("description", ServiceName, "Limits daily computer time, unused time rolls over.");
        Sc("failure", ServiceName, "reset=", "0", "actions=", "restart/2000/restart/2000/restart/2000");

        report("Starting the service (it locks itself down) …");
        Sc("start", ServiceName);
        Thread.Sleep(3000);

        report("Starting the on-screen display …");
        TryStartAgent();

        report("Done.");
    }

    // ------------------------------------------------------------- Aktualisieren

    /// <summary>
    /// Stiller Update-Modus: nur die Programmdateien tauschen, sonst nichts.
    /// Wird vom Dienst gestartet, nachdem der Signatur- und Hash-Check des neuen
    /// Installers bestanden ist. Zustand, Passwort und Einstellungen bleiben
    /// unangetastet; der Dienst richtet seine Sperren beim Neustart selbst
    /// wieder auf.
    /// </summary>
    public static bool UpdateInPlace(out string error)
    {
        error = string.Empty;

        try
        {
            if (!HasPayload())
            {
                error = "installer payload missing";
                return false;
            }

            TryLog("Update: swapping program files …");

            // Watchdog schlafen legen, damit er den Dienst nicht mitten im
            // Tausch neu startet und die Dateien wieder sperrt.
            SchTasks("/Change", "/TN", "Monkey Watchdog", "/DISABLE");
            try
            {
                Sc("stop", ServiceName);
                WaitForExit("MonkeyService", TimeSpan.FromSeconds(30));

                // Laufende Programme lassen sich nicht ueberschreiben, wohl aber
                // umbenennen: Alte Dateien zur Seite, neue an ihren Platz. Ein
                // noch laufender Agent arbeitet von der beiseite gelegten Datei
                // weiter und startet sich selbst neu, sobald er die neue
                // Dienstversion sieht. Die Reste raeumt der Dienst beim
                // naechsten Start weg.
                Directory.CreateDirectory(TargetDir);
                foreach (var name in PayloadFiles)
                    MoveAside(Path.Combine(TargetDir, name));

                ExtractPayload(TargetDir);

                Sc("start", ServiceName);
            }
            finally
            {
                SchTasks("/Change", "/TN", "Monkey Watchdog", "/ENABLE");
            }

            TryLog("Update: done, service restarted.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WaitForExit(string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName(processName).Length > 0)
            Thread.Sleep(500);
    }

    private static void MoveAside(string path)
    {
        if (!File.Exists(path)) return;

        // Eindeutiger Name, damit auch eine noch gesperrte Beiseite-Datei vom
        // vorletzten Update nicht im Weg steht.
        var aside = $"{path}.{Path.GetRandomFileName()}.old";
        try { File.Move(path, aside); }
        catch
        {
            try { File.Delete(path); }
            catch { /* dann scheitert gleich das Schreiben - mit klarer Meldung */ }
        }
    }

    /// <summary>
    /// Der stille Modus hat kein Fenster - was passiert, landet im Dienstlog.
    /// </summary>
    public static void TryLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.AppendAllText(Paths.LogFile,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [setup] {message}{Environment.NewLine}");
        }
        catch { /* Log ist Kuer */ }
    }

    // ------------------------------------------------------------------- Entfernen

    /// <summary>
    /// Baut die Installation ab. Der Dienst hebt seine Sperren nur nach korrektem
    /// Master-Passwort auf - schlaegt das fehl, bleibt alles unveraendert.
    /// </summary>
    public static bool Uninstall(string password, Action<string> report, out string error)
    {
        error = string.Empty;

        if (ServiceInstalled())
        {
            report("Contacting the service …");
            EnsureServiceRunning();

            report("Unlocking …");
            var response = SendUnlock(password);
            if (response is null)
            {
                error = "The service is not responding. Without it running, the unlock can't be triggered.";
                return false;
            }
            if (!response.Ok)
            {
                error = response.Message ?? "Rejected.";
                return false;
            }

            Thread.Sleep(1000);
            report("Stopping and removing the service …");
            Sc("stop", ServiceName);
            Thread.Sleep(2000);
            Sc("delete", ServiceName);
        }
        else
        {
            report("No service found - clearing leftovers …");
        }

        report("Closing the display …");
        foreach (var p in Process.GetProcessesByName("MonkeyAgent")) TryKill(p);

        report("Cleaning up …");
        SchTasks("/Delete", "/F", "/TN", "Monkey Watchdog");
        foreach (var dir in new[] { TargetDir, Paths.DataDir })
        {
            if (!Directory.Exists(dir)) continue;
            try { NativeSecurity.ForceDelete(dir); }
            catch (Exception ex) { report($"Note: '{dir}' was partly left behind ({ex.Message})."); }
        }

        report("Done.");
        return true;
    }

    // ----------------------------------------------------------------- Bausteine

    private static void CleanForFreshInstall(Action<string> report)
    {
        foreach (var name in new[] { "MonkeyAgent", "MonkeyService" })
            foreach (var p in Process.GetProcessesByName(name))
                TryKill(p);

        SchTasks("/Delete", "/F", "/TN", "Monkey Watchdog");
        Sc("stop", ServiceName);
        Sc("delete", ServiceName);

        foreach (var dir in new[] { TargetDir, Paths.DataDir })
        {
            try { NativeSecurity.ForceDelete(dir); }
            catch (Exception ex) { report($"Note: '{dir}' could not be fully removed ({ex.Message})."); }
        }
    }

    private static void ExtractPayload(string target)
    {
        Directory.CreateDirectory(target);
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        foreach (var name in PayloadFiles)
        {
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded file '{name}' is missing.");
            using var destination = File.Create(Path.Combine(target, name));
            source.CopyTo(destination);
        }
    }

    private static void WriteInitialState(InstallOptions options)
    {
        Directory.CreateDirectory(Paths.DataDir);

        var state = new GuardState();
        var (hash, salt, iterations) = PasswordHash.Create(options.Password);
        state.PasswordHash = hash;
        state.PasswordSalt = salt;
        state.PasswordIterations = iterations;
        state.Config.DailyGrantMinutes = Math.Clamp(options.DailyMinutes, 0, 24 * 60);
        state.Config.CapMinutes = Math.Clamp(options.CapMinutes, state.Config.DailyGrantMinutes, 100 * 24 * 60);
        state.Config.MaxManualGrantMinutes = Math.Clamp(options.MaxGrantMinutes, 0, 24 * 60);
        state.TrustedNow = DateTimeOffset.Now;
        // Guthaben bleibt 0; der Dienst schreibt beim ersten Start die Erstgutschrift.

        File.WriteAllText(Paths.StateFile, state.ToJson());
    }

    private static Response? SendUnlock(string password)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut);
            pipe.Connect(4000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var request = new Request { Type = RequestType.Unlock, Password = password };
            writer.WriteLine(request.ToJson().ReplaceLineEndings(" "));
            var line = reader.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? null : Response.FromJson(line);
        }
        catch { return null; }
    }

    private static void EnsureServiceRunning()
    {
        using var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut);
        try { pipe.Connect(500); return; } catch { /* nicht erreichbar */ }

        Sc("start", ServiceName);
        Thread.Sleep(3000);
    }

    private static void TryStartAgent()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(TargetDir, "MonkeyAgent.exe"),
                UseShellExecute = true,
            });
        }
        catch { /* startet spaetestens nach der naechsten Anmeldung */ }
    }

    // --------------------------------------------------------------- Prozesshilfen

    private static void Sc(params string[] args) => RunHidden("sc.exe", args);
    private static void SchTasks(params string[] args) => RunHidden("schtasks.exe", args);

    private static void RunHidden(string file, IReadOnlyList<string> args)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);

            using var process = Process.Start(info);
            process?.WaitForExit(20000);
        }
        catch { /* Fehler zeigen sich beim naechsten Schritt */ }
    }

    private static void TryKill(Process p) { try { p.Kill(); } catch { /* egal */ } }
}
