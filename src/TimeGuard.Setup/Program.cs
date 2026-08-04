using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using TimeGuard.Core;

namespace TimeGuard.Setup;

/// <summary>
/// Doppelklick-Installer. Das Manifest erzwingt Adminrechte, deshalb steht hier
/// beim Start bereits eine erhoehte Sitzung zur Verfuegung. Der Installer kopiert
/// die Programmdateien, setzt das Master-Passwort und legt den Dienst an - der
/// Dienst sichert sich beim ersten Start selbst ab.
/// </summary>
internal static class Program
{
    private const string ServiceName = "TimeGuardSrv";
    private const string DisplayName = "TimeGuard Zeitkontrolle";
    private static readonly string TargetDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TimeGuard");

    private static int Main(string[] args)
    {
        Console.Title = "TimeGuard Setup";
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (!IsElevated())
            {
                // Sollte durch das Manifest nie eintreten - zur Sicherheit dennoch.
                Error("Bitte mit Rechtsklick > \"Als Administrator ausfuehren\" starten.");
                return Pause(2);
            }

            var installed = ServiceInstalled();

            Banner();
            Console.WriteLine(installed
                ? "TimeGuard ist auf diesem Rechner installiert."
                : "TimeGuard ist auf diesem Rechner noch nicht installiert.");
            Console.WriteLine();
            Console.WriteLine("  [1] Installieren" + (installed ? " / neu einrichten" : string.Empty));
            Console.WriteLine("  [2] Entfernen");
            Console.WriteLine("  [3] Abbrechen");
            Console.WriteLine();
            Console.Write("Auswahl: ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            return choice switch
            {
                "1" => Install(installed),
                "2" => Uninstall(installed),
                _ => Pause(0, "Abgebrochen."),
            };
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return Pause(1);
        }
    }

    // ------------------------------------------------------------- Installieren

    private static int Install(bool alreadyInstalled)
    {
        if (alreadyInstalled)
        {
            Error("Es ist bereits ein Dienst installiert. Bitte zuerst 'Entfernen' waehlen.");
            return Pause(2);
        }

        if (!HasPayload())
        {
            Error("Diese Setup-Datei enthaelt keine Programmdateien.\n" +
                  "Bitte mit .\\build.ps1 neu bauen.");
            return Pause(2);
        }

        // Ist noch Zustand einer frueheren (evtl. defekten) Installation da, wird er
        // nur ueberschrieben, wenn das bestehende Master-Passwort stimmt. Sonst
        // waere das Neu-Aufsetzen ein passwortfreier Weg, den Schutz abzuraeumen.
        var existing = TryReadExistingState();
        if (existing is { HasPassword: true })
        {
            Console.WriteLine();
            Warn("Es ist bereits eine TimeGuard-Installation vorhanden (moeglicherweise defekt).");
            Console.WriteLine("Zum Neu-Aufsetzen bitte das BESTEHENDE Master-Passwort eingeben.");
            var check = AskPassword("Bestehendes Master-Passwort");
            if (!PasswordHash.Verify(check, existing.PasswordHash, existing.PasswordSalt, existing.PasswordIterations))
            {
                Console.WriteLine();
                Error("Falsches Master-Passwort. Neuinstallation abgebrochen.");
                return Pause(2);
            }
            Console.WriteLine("  Passwort ok - die vorhandene Installation wird ersetzt.");
        }

        var daily = AskInt("Tagesbudget in Minuten", 30);
        var cap = AskInt("Hoechstguthaben (Deckel) in Minuten", Math.Max(daily, 240));
        if (cap < daily) cap = daily;

        Console.WriteLine();
        Console.WriteLine("Master-Passwort festlegen. Es ist der einzige Schluessel -");
        Console.WriteLine("bewahre es ausserhalb des Rechners auf (Handy, Zettel).");
        var password = AskNewPassword();
        if (password is null) return Pause(2);

        Console.WriteLine();
        Section("Installation laeuft");

        Step("Reste einer frueheren Installation entfernen");
        CleanForFreshInstall();

        Step("Programmdateien schreiben");
        ExtractPayload(TargetDir);

        Step("Master-Passwort und Grundwerte schreiben");
        WriteInitialState(password, daily, cap);

        Step("Dienst anlegen");
        var serviceExe = Path.Combine(TargetDir, "TimeGuardService.exe");
        Sc("create", ServiceName, "binPath=", serviceExe, "start=", "auto", "obj=", "LocalSystem",
            "DisplayName=", DisplayName);
        Sc("description", ServiceName, "Begrenzt die taegliche Computerzeit mit Uebertrag ungenutzter Zeit.");
        Sc("failure", ServiceName, "reset=", "0", "actions=", "restart/2000/restart/2000/restart/2000");

        Step("Dienst starten (richtet den Selbstschutz ein)");
        Sc("start", ServiceName);
        Thread.Sleep(3000);

        Step("Anzeige (Agent) starten");
        TryStartAgent();

        Console.WriteLine();
        Section("Fertig");
        Console.WriteLine($"  Tagesbudget      : {daily} Minuten");
        Console.WriteLine($"  Hoechstguthaben  : {cap} Minuten");
        Console.WriteLine($"  Warnung          : bei 10 Minuten Restzeit");
        Console.WriteLine($"  Nachlegen        : max. 4 Stunden pro Vorgang, beliebig oft");
        Console.WriteLine();
        Console.WriteLine("  Restzeit         : Overlay oben rechts");
        Console.WriteLine("  Master-Steuerung : Doppelklick auf das Tray-Symbol");
        Console.WriteLine("  Overlay ein/aus  : Strg+Alt+Umschalt+T");
        return Pause(0);
    }

    // --------------------------------------------------------------- Entfernen

    private static int Uninstall(bool installed)
    {
        if (!installed)
        {
            Warn("Es ist kein Dienst installiert. Ich raeume nur eventuelle Reste weg.");
            RemoveLeftovers();
            return Pause(0);
        }

        EnsureServiceRunning();

        Console.WriteLine("Zum Entfernen das Master-Passwort eingeben.");
        var password = AskPassword("Master-Passwort");
        Console.WriteLine();

        Step("Sperren aufheben");
        var response = SendUnlock(password);
        if (response is null)
        {
            Error("Der Dienst antwortet nicht. Ohne laufenden Dienst laesst sich der Abbau nicht ausloesen.");
            return Pause(1);
        }
        if (!response.Ok)
        {
            Error("Abgelehnt: " + response.Message);
            return Pause(1);
        }
        Thread.Sleep(1000);

        Step("Dienst anhalten und entfernen");
        Sc("stop", ServiceName);
        Thread.Sleep(2000);
        Sc("delete", ServiceName);

        Step("Anzeige beenden");
        foreach (var p in Process.GetProcessesByName("TimeGuardAgent"))
            TryKill(p);

        RemoveLeftovers();

        Console.WriteLine();
        Section("Entfernt");
        return Pause(0);
    }

    private static void RemoveLeftovers()
    {
        Step("Aufraeumen");
        SchTasks("/Delete", "/F", "/TN", "TimeGuard Watchdog");

        foreach (var dir in new[] { TargetDir, Paths.DataDir })
        {
            if (!Directory.Exists(dir)) continue;
            try { NativeSecurity.ForceDelete(dir); }
            catch (Exception ex) { Warn($"'{dir}' blieb teils zurueck ({ex.Message}). Nach einem Neustart erneut versuchen."); }
        }
    }

    /// <summary>
    /// Vor einer frischen Installation aufraeumen: laufende Prozesse beenden, die
    /// Watchdog-Aufgabe und einen etwaigen Restdienst entfernen und die - womoeglich
    /// gegen Administratoren gesperrten - Ordner freiraeumen. Ohne das schlaegt das
    /// Schreiben nach einem abgebrochenen frueheren Versuch fehl.
    /// </summary>
    private static void CleanForFreshInstall()
    {
        foreach (var name in new[] { "TimeGuardAgent", "TimeGuardService" })
            foreach (var p in Process.GetProcessesByName(name))
                TryKill(p);

        SchTasks("/Delete", "/F", "/TN", "TimeGuard Watchdog");
        Sc("stop", ServiceName);
        Sc("delete", ServiceName);

        foreach (var dir in new[] { TargetDir, Paths.DataDir })
        {
            try { NativeSecurity.ForceDelete(dir); }
            catch (Exception ex)
            {
                Warn($"'{dir}' liess sich nicht vollstaendig entfernen ({ex.Message}).");
            }
        }
    }

    // ---------------------------------------------------------------- Bausteine

    /// <summary>
    /// Liest den Zustand einer vorhandenen Installation, falls einer da ist - ueber
    /// das Sicherungs-Privileg und ohne die Rechte zu veraendern. Gibt null zurueck,
    /// wenn nichts (Lesbares) vorhanden ist.
    /// </summary>
    private static GuardState? TryReadExistingState()
    {
        var bytes = NativeSecurity.ReadPrivileged(Paths.StateFile);
        if (bytes is null || bytes.Length == 0) return null;
        try { return GuardState.FromJson(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch { return null; }
    }

    private static readonly string[] PayloadFiles = ["TimeGuardService.exe", "TimeGuardAgent.exe"];

    private static bool HasPayload()
    {
        var names = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames();
        return PayloadFiles.All(f => names.Contains(f));
    }

    /// <summary>
    /// Dienst und Agent stecken als eingebettete Ressourcen im Installer. Beim
    /// Einrichten werden sie in den Zielordner geschrieben - deshalb ist die
    /// Setup-Datei die einzige, die man weitergeben muss.
    /// </summary>
    private static void ExtractPayload(string target)
    {
        Directory.CreateDirectory(target);
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        foreach (var name in PayloadFiles)
        {
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Eingebettete Datei '{name}' fehlt.");
            using var destination = File.Create(Path.Combine(target, name));
            source.CopyTo(destination);
        }
    }

    private static void WriteInitialState(string password, int daily, int cap)
    {
        Directory.CreateDirectory(Paths.DataDir);

        var state = new GuardState();
        var (hash, salt, iterations) = PasswordHash.Create(password);
        state.PasswordHash = hash;
        state.PasswordSalt = salt;
        state.PasswordIterations = iterations;
        state.Config.DailyGrantMinutes = Math.Clamp(daily, 0, 24 * 60);
        state.Config.CapMinutes = Math.Clamp(cap, state.Config.DailyGrantMinutes, 100 * 24 * 60);
        state.TrustedNow = DateTimeOffset.Now;
        // Balance bleibt 0; der Dienst schreibt beim ersten Start die Erstgutschrift.

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
        catch
        {
            return null;
        }
    }

    private static void EnsureServiceRunning()
    {
        using var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut);
        try { pipe.Connect(500); return; } catch { /* nicht erreichbar */ }

        Step("Dienst starten, um die Sperren aufheben zu koennen");
        Sc("start", ServiceName);
        Thread.Sleep(3000);
    }

    private static void TryStartAgent()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(TargetDir, "TimeGuardAgent.exe"),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Warn($"Agent nicht automatisch gestartet ({ex.Message}). Startet nach der naechsten Anmeldung.");
        }
    }

    // ------------------------------------------------------------- Prozesshilfen

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
        catch (Exception ex)
        {
            Warn($"'{file}' fehlgeschlagen: {ex.Message}");
        }
    }

    private static void TryKill(Process p) { try { p.Kill(); } catch { /* egal */ } }
    private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // --------------------------------------------------------------- Eingabe

    private static int AskInt(string label, int fallback)
    {
        Console.Write($"{label} [{fallback}]: ");
        var text = Console.ReadLine()?.Trim();
        return int.TryParse(text, out var value) && value >= 0 ? value : fallback;
    }

    private static string? AskNewPassword()
    {
        var first = AskPassword("Master-Passwort");
        if (first.Length < 4)
        {
            Error("Mindestens 4 Zeichen.");
            return null;
        }

        var again = AskPassword("Wiederholen");
        if (first != again)
        {
            Error("Die Eingaben stimmen nicht ueberein.");
            return null;
        }

        return first;
    }

    private static string AskPassword(string label)
    {
        Console.Write($"{label}: ");
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Remove(buffer.Length - 1, 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        return buffer.ToString();
    }

    // --------------------------------------------------------------- Ausgabe

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ServiceInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key is not null;
        }
        catch { return false; }
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  ============================================");
        Console.WriteLine("   TimeGuard - Zeitkontrolle einrichten");
        Console.WriteLine("  ============================================");
        Console.WriteLine();
    }

    private static void Section(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("== " + text + " ==");
        Console.ResetColor();
    }

    private static void Step(string text) => Console.WriteLine("  - " + text);

    private static void Warn(string text)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ! " + text);
        Console.ResetColor();
    }

    private static void Error(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  Fehler: " + text);
        Console.ResetColor();
    }

    private static int Pause(int code, string? message = null)
    {
        if (message is not null) Console.WriteLine(message);
        Console.WriteLine();
        Console.WriteLine("Zum Schliessen die Eingabetaste druecken ...");
        Console.ReadLine();
        return code;
    }
}
