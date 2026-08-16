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

    /// <summary>
    /// Der stille Updater wird vom laufenden Dienst gestartet und erbt deshalb
    /// dessen LocalSystem-Token. Ein bloss erhoehter Administrator darf diesen
    /// passwortlosen Sonderweg nicht von Hand aufrufen.
    /// </summary>
    public static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        return identity.User == system;
    }

    public static bool HasPayload()
    {
        var names = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames();
        return PayloadFiles.All(f => names.Contains(f));
    }

    public static bool ServiceInstalled() => ServiceKeyExists(ServiceName);

    /// <summary>
    /// Erkennt auch eine beschaedigte oder nur teilweise entfernte Installation.
    /// Unlesbarer Zustand darf nie wie "frisch installiert" behandelt werden,
    /// sonst waere das Beschaedigen der Zustandsdatei ein Passwort-Bypass.
    /// </summary>
    public static bool InstallationPresent() =>
        ServiceInstalled()
        || Directory.Exists(TargetDir)
        || Directory.Exists(Paths.DataDir)
        || ProcessExists("MonkeyService")
        || ProcessExists("MonkeyAgent")
        || WatchdogInstalled();

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

    public static bool Install(
        InstallOptions options,
        string? currentPassword,
        Action<string> report,
        out string error)
    {
        error = string.Empty;

        // Noch einmal direkt vor der Aenderung pruefen. Die Anzeige im Wizard ist
        // nur Komfort und darf nicht die Sicherheitsentscheidung treffen.
        if (InstallationPresent()
            && !AuthorizeExistingInstallation(currentPassword, report, out error))
            return false;

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
        return true;
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
            if (!IsLocalSystem())
            {
                error = "silent updates may only be started by the Monkey service";
                return false;
            }

            // Der Dienst legt ausschliesslich diese Datei nach erfolgreicher
            // Signatur- und Hashpruefung ab. Ein beliebiger Installer mit dem
            // undokumentierten 'update'-Argument ist kein Updatepfad.
            var expectedPath = Path.GetFullPath(Path.Combine(Paths.DataDir, "update", "MonkeySetup.exe"));
            var actualPath = Environment.ProcessPath is { } processPath
                ? Path.GetFullPath(processPath)
                : string.Empty;
            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                error = "silent update was not started from the protected staging directory";
                return false;
            }

            if (!ServiceInstalled())
            {
                error = "no installed Monkey service found";
                return false;
            }

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

        if (!InstallationPresent())
        {
            report("No installation found.");
            report("Done.");
            return true;
        }

        if (!AuthorizeExistingInstallation(password, report, out error))
            return false;

        if (ServiceInstalled())
        {
            report("Stopping and removing the service …");
            Sc("stop", ServiceName);
            Thread.Sleep(2000);
            Sc("delete", ServiceName);
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

    /// <summary>
    /// Autorisiert Ersetzen oder Entfernen fail-closed. Solange der Dienst lebt,
    /// prueft ausschliesslich er das Passwort und hebt danach seine Sperren auf.
    /// Bei echten Resten ohne Dienst bleibt nur die lokale Hashpruefung; fehlt
    /// auch dieser Beleg oder ist er unlesbar, wird nichts geloescht.
    /// </summary>
    private static bool AuthorizeExistingInstallation(
        string? password,
        Action<string> report,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            error = "The current master password is required to replace or remove this installation.";
            return false;
        }

        if (ServiceInstalled())
        {
            report("Contacting the existing service …");
            EnsureServiceRunning();

            report("Unlocking the existing installation …");
            var response = SendUnlock(password);
            if (response is null)
            {
                error = "The service is not responding. Without it, the protected installation won't be changed.";
                return false;
            }
            if (!response.Ok)
            {
                error = response.Message ?? "The existing service rejected the request.";
                return false;
            }

            Thread.Sleep(1000);
            return true;
        }

        report("Checking the protected installation remnants …");
        var state = TryReadExistingState();
        if (state is not { HasPassword: true })
        {
            error = "Protected Monkey remnants were found, but their master-password state is missing or unreadable. " +
                    "Setup stopped without changing them.";
            return false;
        }

        if (!PasswordHash.Verify(password, state.PasswordHash, state.PasswordSalt, state.PasswordIterations))
        {
            error = "That's not the current master password.";
            return false;
        }

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

    private static bool WatchdogInstalled()
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("/Query");
            info.ArgumentList.Add("/TN");
            info.ArgumentList.Add("Monkey Watchdog");

            using var process = Process.Start(info);
            if (process is null) return false;
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool ProcessExists(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
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
