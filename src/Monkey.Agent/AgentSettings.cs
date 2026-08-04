using Microsoft.Win32;

namespace Monkey.Agent;

/// <summary>
/// Reine Anzeigevorlieben pro Benutzer. Bewusst in HKCU und ohne jeden Einfluss
/// auf die Durchsetzung - wer hier etwas aendert, gewinnt keine Sekunde.
/// </summary>
internal static class AgentSettings
{
    private const string Key = @"Software\Monkey";

    /// <summary>Overlay sichtbar?</summary>
    public static bool OverlayVisible
    {
        get => ReadBool(nameof(OverlayVisible), true);
        set => WriteBool(nameof(OverlayVisible), value);
    }

    /// <summary>
    /// true  = die angemeldete Zeit hochzaehlen, Restzeit beim Hovern.
    /// false = die Restzeit herunterzaehlen, angemeldete Zeit beim Hovern.
    /// </summary>
    public static bool CountUp
    {
        get => ReadBool(nameof(CountUp), false);
        set => WriteBool(nameof(CountUp), value);
    }

    /// <summary>Kasten hinter der Zahl anzeigen? Ausgeschaltet bleibt nur die Zahl.</summary>
    public static bool OverlayBackground
    {
        get => ReadBool(nameof(OverlayBackground), true);
        set => WriteBool(nameof(OverlayBackground), value);
    }

    /// <summary>
    /// Farbe der Zahl. "auto" = nach Restzeit (gruen/gelb/rot), sonst ein
    /// Hex-Wert wie "#7AC7FF".
    /// </summary>
    public static string OverlayColor
    {
        get => ReadString(nameof(OverlayColor), "auto");
        set => WriteString(nameof(OverlayColor), value);
    }

    private static bool ReadBool(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteBool(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Anzeigevorlieben sind nicht wichtig genug, um den Agent zu stoppen.
        }
    }

    private static string ReadString(string name, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) as string ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch
        {
            // siehe oben
        }
    }
}
