using System.Security.Principal;
using TimeGuard.Core;

namespace TimeGuard.Service;

/// <summary>
/// Verwaltungsbefehle. 'watchdog' ist das Ziel der geplanten Aufgabe. 'init' setzt
/// bei der Erstinstallation Passwort und Grundwerte. Alles Weitere laeuft im Betrieb
/// ueber den passwortgeschuetzten Kanal des Dienstes, nicht ueber die Konsole.
/// </summary>
internal static class Cli
{
    public static int Run(string[] args)
    {
#if DEBUG
        // Damit sich init/status im Test auf einen eigenen Ordner richten lassen.
        Paths.UseTestLocation(Value(args, "--data-dir"), Value(args, "--pipe"));
#endif
        var command = args[0].ToLowerInvariant();

        if (command is "help" or "--help" or "-h" or "/?")
        {
            PrintUsage();
            return 0;
        }

        // Der Watchdog laeuft als SYSTEM aus der geplanten Aufgabe. Er kann nur
        // schuetzen, nie eine Grenze aufheben - deshalb ohne weitere Pruefung.
        if (command == "watchdog")
            return SelfProtect.RunWatchdog();

        if (!IsElevated())
        {
            Console.Error.WriteLine("Dieser Befehl braucht eine Eingabeaufforderung als Administrator.");
            return 2;
        }

        return command switch
        {
            "init" => Init(args),
            "status" => Status(),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unbekannter Befehl '{command}'.");
        PrintUsage();
        return 2;
    }

    private static int Init(string[] args)
    {
        var store = new StateStore();
        var state = store.Load();

        // Ein vorhandenes Passwort wird hier nicht ueberschrieben. Aendern geht im
        // laufenden Betrieb nur ueber die Master-Steuerung, also mit Kenntnis des
        // aktuellen Passworts.
        if (state.HasPassword)
        {
            Console.Error.WriteLine(
                "Es ist bereits ein Master-Passwort gesetzt. Aendern nur ueber die Master-Steuerung " +
                "(dort wird das aktuelle Passwort verlangt).");
            return 2;
        }

        var password = Value(args, "--password");
        if (Flag(args, "--password-stdin"))
            password = ReadStdinPassword();

        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
        {
            Console.Error.WriteLine("Ein Master-Passwort mit mindestens 4 Zeichen ist erforderlich.");
            return 2;
        }

        var (hash, salt, iterations) = PasswordHash.Create(password);
        state.PasswordHash = hash;
        state.PasswordSalt = salt;
        state.PasswordIterations = iterations;

        if (Number(args, "--daily") is { } daily)
            state.Config.DailyGrantMinutes = Math.Clamp(daily, 0, 24 * 60);

        if (Number(args, "--cap") is { } cap)
            state.Config.CapMinutes = Math.Clamp(cap, state.Config.DailyGrantMinutes, 100 * 24 * 60);

        if (Number(args, "--grace") is { } grace)
            state.Config.GraceSeconds = Math.Clamp(grace, 10, 3600);

        if (Number(args, "--balance") is { } balance)
        {
            state.BalanceSeconds = Math.Max(0, balance) * 60.0;
            state.LastAccrualDate = DateOnly.FromDateTime(DateTime.Now);
        }

        state.TrustedNow = DateTimeOffset.Now;
        store.Save(state);

        Console.WriteLine($"Eingerichtet. Tagesbudget {state.Config.DailyGrantMinutes} min, " +
                          $"Deckel {state.Config.CapMinutes} min, " +
                          $"Guthaben {state.BalanceSeconds / 60:0} min.");
        return 0;
    }

    private static int Status()
    {
        if (!File.Exists(Paths.StateFile))
        {
            Console.Error.WriteLine("Es ist kein Zustand hinterlegt.");
            return 1;
        }

        var state = GuardState.FromJson(File.ReadAllText(Paths.StateFile));
        if (state is null)
        {
            Console.Error.WriteLine("Zustandsdatei ist beschaedigt.");
            return 1;
        }

        var span = TimeSpan.FromSeconds(Math.Max(0, state.BalanceSeconds));
        Console.WriteLine($"Guthaben            : {(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");
        Console.WriteLine($"Tagesbudget         : {state.Config.DailyGrantMinutes} min");
        Console.WriteLine($"Deckel              : {state.Config.CapMinutes} min");
        Console.WriteLine($"Warnung bei         : {string.Join(", ", state.Config.WarnAtMinutes)} min");
        Console.WriteLine($"Letzte Gutschrift   : {state.LastAccrualDate}");
        Console.WriteLine($"Passwort gesetzt    : {(state.HasPassword ? "ja" : "NEIN")}");
        Console.WriteLine($"Zeitmanipulationen  : {state.ClockTamperEvents}");
        Console.WriteLine($"Zuletzt gespeichert : {state.LastSaved:g}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            TimeGuardService - Verwaltungsbefehle

              init --password <pw> [--daily <min>] [--cap <min>]
                   [--grace <sek>] [--balance <min>]
                                Erstinstallation: Passwort und Grundwerte setzen.
                                Statt --password geht auch --password-stdin.

              status            Aktuellen Zustand anzeigen (als Administrator).
              watchdog          Interne Wartung. Wird von der geplanten Aufgabe
                                aufgerufen, nicht von Hand.

            Ohne Befehl laeuft das Programm als Windows-Dienst.
            """);
    }

    private static bool IsElevated()
    {
#if DEBUG
        // Im Testbuild ohne erhoehte Rechte pruefbar.
        return true;
#else
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
#endif
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Passwort von der Standardeingabe. PowerShell stellt beim Pipen an ein
    /// natives Programm ein UTF-8-BOM voran, das sonst als erstes Zeichen des
    /// Passworts landen wuerde - dann passt spaeter keine Eingabe mehr dazu.
    /// </summary>
    private static string? ReadStdinPassword() => StripBom(Console.ReadLine());

    private static string? StripBom(string? line) =>
        line is not null && line.Length > 0 && line[0] == '﻿' ? line[1..] : line;

    private static bool Flag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int? Number(string[] args, string name) =>
        int.TryParse(Value(args, name), out var value) ? value : null;
}
