using System.Runtime.InteropServices;

namespace Monkey.Service;

internal static class Native
{
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    public enum WtsConnectState
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected,
        Idle, Listen, Reset, Down, Init
    }

    private const int WTSSessionInfoEx = 25;

    /// <summary>Auf Windows 8 und neuer: 0 == gesperrt, 1 == entsperrt.</summary>
    public const int WTS_SESSIONSTATE_LOCK = 0;
    public const int WTS_SESSIONSTATE_UNLOCK = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    /// <summary>
    /// Kopf von WTSINFOEXW. Die eingebettete Union ist wegen enthaltener
    /// LARGE_INTEGER-Felder auf 8 ausgerichtet, deshalb das Padding nach Level.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoExHeader
    {
        public uint Level;
        public uint Padding;
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessageW(IntPtr hServer, int sessionId,
        string pTitle, int titleLength, string pMessage, int messageLength,
        int style, int timeout, out int pResponse, bool bWait);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong lpUnbiasedInterruptTime);

    /// <summary>
    /// Wachzeit seit Systemstart in 100-ns-Einheiten - identisch zur TimeSpan-Tick-Einheit.
    /// Schlaf- und Ruhezustand sind ausgenommen, im Gegensatz zu Environment.TickCount64.
    /// </summary>
    public static ulong GetUnbiasedInterruptTime() =>
        QueryUnbiasedInterruptTime(out var value) ? value : (ulong)Environment.TickCount64 * 10_000UL;

    public readonly record struct SessionInfo(int SessionId, WtsConnectState State, bool Locked);

    public static List<SessionInfo> EnumerateSessions()
    {
        var result = new List<SessionInfo>();
        if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
            return result;

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                // Sitzung 0 ist die isolierte Dienstsitzung, dort sitzt nie ein Mensch.
                if (entry.SessionId == 0) continue;
                result.Add(new SessionInfo(entry.SessionId, entry.State, IsSessionLocked(entry.SessionId)));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    private static bool IsSessionLocked(int sessionId)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, WTSSessionInfoEx,
                out var buffer, out var bytes))
            return false;

        try
        {
            if (bytes < Marshal.SizeOf<WtsInfoExHeader>()) return false;
            var header = Marshal.PtrToStructure<WtsInfoExHeader>(buffer);
            if (header.Level != 1) return false;
            return header.SessionFlags == WTS_SESSIONSTATE_LOCK;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public static bool LogoffSession(int sessionId) =>
        WTSLogoffSession(WTS_CURRENT_SERVER_HANDLE, sessionId, false);

    /// <summary>
    /// Systemeigener Meldungsdialog. Dient als Rueckfallebene, falls der Agent
    /// nicht laeuft - dann sieht der Benutzer die Warnung trotzdem.
    /// </summary>
    public static void SendMessage(int sessionId, string title, string message, int timeoutSeconds = 20)
    {
        const int MB_OK = 0x0;
        const int MB_ICONWARNING = 0x30;
        const int MB_SETFOREGROUND = 0x10000;
        const int MB_TOPMOST = 0x40000;

        WTSSendMessageW(WTS_CURRENT_SERVER_HANDLE, sessionId,
            title, title.Length * 2, message, message.Length * 2,
            MB_OK | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST,
            timeoutSeconds, out _, false);
    }
}
