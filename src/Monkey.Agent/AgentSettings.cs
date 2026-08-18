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

    /// <summary>
    /// Kasten hinter der Zahl anzeigen? Standardmaessig aus - dann steht nur die
    /// Zahl auf dem Desktop. Wer den Kasten will, schaltet ihn im Tray-Menue ein.
    /// </summary>
    public static bool OverlayBackground
    {
        get => ReadBool(nameof(OverlayBackground), false);
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

    /// <summary>
    /// Faehrt der Affe heraus, wenn der Zeiger auf dem Overlay steht? Standard
    /// an - es ist die einzige Stelle, an der das Programm Laune zeigt.
    /// </summary>
    public static bool HoverIcon
    {
        get => ReadBool(nameof(HoverIcon), true);
        set => WriteBool(nameof(HoverIcon), value);
    }

    /// <summary>
    /// Deckkraft des Overlays in Prozent. Blass genug, um nicht zu stoeren,
    /// aber nie ganz unsichtbar - siehe <see cref="OverlayWindow.MinOpacityPercent"/>.
    /// Wer es ganz weg haben will, nimmt <see cref="OverlayVisible"/>.
    /// </summary>
    public static int OverlayOpacity
    {
        get => OverlayWindow.ClampOpacity(ReadInt(nameof(OverlayOpacity), 100));
        set => WriteInt(nameof(OverlayOpacity), OverlayWindow.ClampOpacity(value));
    }

    /// <summary>In welcher Bildschirmecke das Overlay sitzt.</summary>
    public static OverlayCorner OverlayCorner
    {
        get => Enum.TryParse<OverlayCorner>(ReadString(nameof(OverlayCorner), string.Empty), out var value)
            ? value
            : Agent.OverlayCorner.TopRight;
        set => WriteString(nameof(OverlayCorner), value.ToString());
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

    private static int ReadInt(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteInt(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch
        {
            // siehe oben
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
