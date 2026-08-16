using System.Runtime.CompilerServices;
using Monkey.Core;
using Monkey.Service;
using Xunit;

// Paths ist prozessweit - die Tests laufen deshalb nacheinander, jeder mit
// eigenem frischem Datenordner.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Monkey.Tests;

internal static class TestBoot
{
    /// <summary>
    /// Laeuft vor jedem Test-Code: nie unter %ProgramData% schreiben, und eine
    /// Abmeldung darf ein Testlauf grundsaetzlich nur protokollieren.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        RunMode.DryRunLogoff = true;
        Paths.UseTestLocation(Path.Combine(Path.GetTempPath(), "MonkeyTests", "boot"), "MonkeyTests.boot");
    }
}

/// <summary>
/// Jeder Test bekommt einen frischen Datenordner unterhalb von %TEMP%. Die
/// Umleitung nutzt denselben Haken wie Konsolentestlaeufe des Dienstes; der
/// installierte Dienst laeuft nie hierdurch.
/// </summary>
internal static class TestEnv
{
    public const string Password = "correct-horse-battery-staple";

    /// <summary>
    /// Bewusst wenige PBKDF2-Iterationen: die Zahl steht im Testzustand und
    /// beschleunigt nur die Tests - das Produkt behaelt seinen Standard.
    /// </summary>
    public const int FastIterations = 1_000;

    public static string FreshDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MonkeyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Paths.UseTestLocation(dir, "MonkeyTests." + Guid.NewGuid().ToString("N"));
        return dir;
    }

    public static GuardState NewState(Action<GuardState>? mutate = null)
    {
        var (hash, salt, iterations) = PasswordHash.Create(Password, FastIterations);
        var state = new GuardState
        {
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            LastAccrualDate = DateOnly.FromDateTime(DateTime.Now),
        };
        mutate?.Invoke(state);
        return state;
    }

    /// <summary>Engine mit vorbereitetem Zustand und simulierten Sitzungen.</summary>
    public static GuardEngine Engine(Action<GuardState>? mutate = null, params Native.SessionInfo[] sessions)
    {
        FreshDataDir();
        File.WriteAllText(Paths.StateFile, NewState(mutate).ToJson());
        return new GuardEngine(() => [.. sessions]);
    }

    public static Native.SessionInfo User(int id = 7, bool locked = false) =>
        new(id, Native.WtsConnectState.Active, locked, "kid");

    public static StatusDto Status(this GuardEngine engine, int sessionId = 0) =>
        engine.Handle(new Request { Type = RequestType.Status, SessionId = sessionId }).Status!;

    public static Response Handle(this GuardEngine engine, string type, string? password = null,
        Action<Request>? mutate = null)
    {
        var request = new Request { Type = type, Password = password };
        mutate?.Invoke(request);
        return engine.Handle(request);
    }

    /// <summary>Zuletzt gespeicherter Zustand, so wie ihn der naechste Dienststart laese.</summary>
    public static GuardState PersistedState() =>
        GuardState.FromJson(File.ReadAllText(Paths.StateFile))!;
}
