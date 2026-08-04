using System.Runtime.InteropServices;

namespace TimeGuard.Agent;

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
        ref bool pvParam, uint fWinIni);

    public static bool IsScreensaverRunning()
    {
        var running = false;
        return SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, ref running, 0) && running;
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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
