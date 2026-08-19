using System.Runtime.InteropServices;
using System.Text;

namespace Monkey.Agent;

internal static class NativeMethods
{
    // --- Fensterstile fuer das Overlay ---

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // Mausklicks gehen hindurch
    public const int WS_EX_TOOLWINDOW = 0x00000080;    // nicht im Alt-Tab
    public const int WS_EX_NOACTIVATE = 0x08000000;    // stiehlt nie den Fokus

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void AddExtendedStyle(IntPtr handle, int styles) =>
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | styles);

    /// <summary>
    /// Schaltet WS_EX_TRANSPARENT um. Eingeschaltet fallen Mausklicks durch das
    /// Fenster hindurch; ausgeschaltet nimmt es sie an.
    /// </summary>
    public static void SetClickThrough(IntPtr handle, bool clickThrough)
    {
        if (handle == IntPtr.Zero) return;

        var style = GetWindowLong(handle, GWL_EXSTYLE);
        var updated = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        if (updated != style) SetWindowLong(handle, GWL_EXSTYLE, updated);
    }

    // --- Immer oben halten, ohne zu aktivieren ---

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static void BringToTop(IntPtr handle) =>
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

    // --- Mauszeiger ---
    // Das Overlay ist klickdurchlaessig, also kommen bei ihm nie Mausereignisse an.
    // Fuer die Hover-Anzeige wird die Zeigerposition deshalb abgefragt.

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // --- Bildschirmschoner ---

    private const uint SPI_GETSCREENSAVERRUNNING = 0x0072;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        ref int pvParam, uint fWinIni);

    /// <summary>Der Desktop, auf dem die Eingabe gerade liegt - der sichtbare.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>
    /// nLength zaehlt Bytes, nicht Zeichen - bei einem UTF-16-Puffer also das
    /// Doppelte seiner Zeichenzahl.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        [Out] StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    /// <summary>Name des Desktops, auf dem Windows den Bildschirmschoner laufen laesst.</summary>
    private const string ScreensaverDesktop = "Screen-saver";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Laeuft gerade ein Bildschirmschoner? Drei Blickwinkel, weil keiner allein
    /// alle Faelle trifft: die offizielle Abfrage; der Desktop "Screen-saver",
    /// auf dem Windows den Schoner laufen laesst; und zuletzt das
    /// Vordergrundfenster. Letzteres faengt den von Hand gestarteten Schoner
    /// (Verknuepfung oder Hotkey auf eine .scr-Datei) - fuer Windows ist der nur
    /// ein normales Vollbildprogramm, das System-Flag bleibt dann aus.
    ///
    /// Jeder Blickwinkel muss den laufenden Schoner belegen, nicht bloss seine
    /// Spuren: Ein falsches Ja haelt die Uhr an, und eine angehaltene Uhr ist
    /// eine ausgeschaltete Aufsicht.
    /// </summary>
    public static bool IsScreensaverRunning()
    {
        var running = 0;
        if (SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, ref running, 0) && running != 0)
            return true;

        return InputDesktopIsScreensaver() || ForegroundWindowIsScreensaver();
    }

    /// <summary>
    /// Liegt die Eingabe gerade auf dem Bildschirmschoner-Desktop?
    ///
    /// Hier stand frueher nur die Frage, ob es den Desktop "Screen-saver"
    /// ueberhaupt gibt. Das war falsch: Windows legt ihn beim ersten Schonerlauf
    /// an und raeumt ihn nicht verlaesslich wieder weg. Ein einziger Schonerlauf
    /// hat die Uhr damit fuer den Rest der Anmeldung angehalten - der Dienst
    /// zog keine Sekunde mehr ab, und die Aufsicht war still ausser Kraft.
    /// Es zaehlt nicht, ob der Desktop existiert, sondern ob er vorn ist.
    /// </summary>
    private static bool InputDesktopIsScreensaver()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);

        // Schlaegt fehl, wenn die Eingabe auf einem Desktop liegt, auf den wir
        // kein Recht haben: Sperrbildschirm oder Rechteabfrage. Beides ist kein
        // Schoner - das Sperren haelt die Uhr ohnehin schon an, und vor einer
        // Rechteabfrage sitzt jemand. Im Zweifel also weiterzaehlen.
        if (desktop == IntPtr.Zero) return false;

        try
        {
            return string.Equals(DesktopName(desktop), ScreensaverDesktop,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>
    /// Name eines Desktops, null wenn er sich nicht lesen laesst. Der Puffer ist
    /// fest: Desktopnamen sind kurz, und was laenger als 255 Zeichen ist, ist
    /// ganz sicher nicht der gesuchte.
    /// </summary>
    private static string? DesktopName(IntPtr desktop)
    {
        const int maxChars = 256;

        var buffer = new StringBuilder(maxChars);
        return GetUserObjectInformation(desktop, UOI_NAME, buffer, maxChars * sizeof(char), out _)
            ? buffer.ToString()
            : null;
    }

    private static bool ForegroundWindowIsScreensaver()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.MainModule?.FileName is { } file
                   && file.EndsWith(".scr", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Prozess schon weg oder Zugriff verweigert - dann eben kein Schoner.
            return false;
        }
    }

    // --- Bildschirm aus ---
    // Auf modernem Windows gibt es haeufig gar keinen Bildschirmschoner mehr:
    // der Monitor geht nach der Wartezeit schlicht aus. Das meldet Windows
    // ueber eine Power-Benachrichtigung - fuer die Uhr zaehlt es wie ein
    // laufender Schoner.

    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    private static Guid _consoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient,
        ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>Meldet dem Fenster kuenftig jeden Wechsel des Anzeigezustands.</summary>
    public static IntPtr RegisterDisplayStateNotifications(IntPtr window) =>
        RegisterPowerSettingNotification(window, ref _consoleDisplayState, 0 /* Fensterhandle */);

    /// <summary>
    /// Liest aus einer WM_POWERBROADCAST/PBT_POWERSETTINGCHANGE-Nachricht den
    /// Anzeigezustand. 0 = aus, 1 = an, 2 = gedimmt - nur "aus" haelt die Uhr an.
    /// </summary>
    public static bool TryReadDisplayOff(IntPtr lParam, out bool displayOff)
    {
        displayOff = false;
        if (lParam == IntPtr.Zero) return false;

        try
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting != _consoleDisplayState || setting.DataLength < 1) return false;

            displayOff = setting.Data == 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Globales Tastenkuerzel ---

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_T = 0x54;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
