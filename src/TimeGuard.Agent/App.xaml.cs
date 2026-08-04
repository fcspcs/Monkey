using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TimeGuard.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TimeGuard.Agent;

/// <summary>
/// Der Agent: Overlay, Tray-Symbol, Warnfenster, Master-Fenster. Er hat bewusst
/// keinerlei Befugnis. Wer ihn abschiesst, verliert nur die Anzeige - gezaehlt und
/// abgemeldet wird weiterhin vom Dienst.
/// </summary>
public partial class App : Application
{
    private const int HotkeyId = 0xA17;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private Mutex? _singleInstance;
    private OverlayWindow? _overlay;
    private Forms::NotifyIcon? _tray;
    private DispatcherTimer? _timer;
    private MasterWindow? _master;
    private WarningWindow? _warning;

    private StatusDto? _status;
    private bool _overlayVisible = true;
    private bool _overlayBackground = true;
    private string _overlayColor = "auto";
    private int? _lastWarningShown;

    private static readonly (string Label, string Value)[] OverlayColors =
    [
        ("Automatisch (nach Restzeit)", "auto"),
        ("Weiß", "#F2F4F8"),
        ("Grün", "#5BD68A"),
        ("Blau", "#7AC7FF"),
        ("Gelb", "#FFC14E"),
        ("Orange", "#FF9F40"),
        ("Rot", "#FF6B5E"),
        ("Pink", "#FF7AC7"),
        ("Violett", "#B57AFF"),
    ];
    private readonly Dictionary<string, Drawing::Icon> _icons = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nur fuer Testlaeufe gegen eine zweite, umgeleitete Dienstinstanz.
        var pipeOverride = ArgumentValue(e.Args, "--pipe");
        if (pipeOverride is not null) Paths.UseTestLocation(null, pipeOverride);

        _singleInstance = new Mutex(true, Paths.MutexName + (pipeOverride ?? string.Empty), out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _overlayVisible = AgentSettings.OverlayVisible;

        _overlayBackground = AgentSettings.OverlayBackground;
        _overlayColor = AgentSettings.OverlayColor;

        _overlay = new OverlayWindow { CountUp = AgentSettings.CountUp };
        _overlay.ApplyPreferences(_overlayBackground, OverlayWindow.ParseColor(_overlayColor));
        _overlay.Show();
        if (!_overlayVisible) _overlay.Hide();

        RegisterHotkey();
        BuildTray();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        _ = PollAsync();
    }

    // ------------------------------------------------------------- Abfrage

    private async Task PollAsync()
    {
        var response = await PipeClient.SendAsync(new Request
        {
            Type = RequestType.Heartbeat,
            SessionId = Process.GetCurrentProcess().SessionId,
            ScreensaverRunning = NativeMethods.IsScreensaverRunning(),
        });

        _status = response?.Status;

        _overlay?.Render(_status);
        if (_overlayVisible) _overlay?.KeepOnTop();

        ShowWarningIfDue();
        UpdateTray();
    }

    /// <summary>
    /// Der Dienst laesst eine ausgeloeste Warnschwelle kurz im Status stehen, damit
    /// der Zwei-Sekunden-Takt sie zuverlaessig aufgreift. Danach faellt sie auf null
    /// zurueck, und dieselbe Schwelle kann spaeter erneut warnen.
    /// </summary>
    private void ShowWarningIfDue()
    {
        var threshold = _status?.WarningMinutes;

        if (threshold is null)
        {
            _lastWarningShown = null;
            return;
        }

        if (threshold == _lastWarningShown) return;
        _lastWarningShown = threshold;

        _warning?.Close();
        _warning = new WarningWindow(threshold.Value);
        _warning.Closed += (_, _) => _warning = null;
        _warning.Show();
    }

    // ---------------------------------------------------------------- Tray

    private void BuildTray()
    {
        var menu = new Forms::ContextMenuStrip();

        var header = new Forms::ToolStripMenuItem("Restzeit: --") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Forms::ToolStripSeparator());

        var toggleOverlay = new Forms::ToolStripMenuItem("Overlay ausblenden");
        toggleOverlay.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(toggleOverlay);

        var toggleMode = new Forms::ToolStripMenuItem("Angemeldete Zeit hochzählen");
        toggleMode.Click += (_, _) => ToggleCountMode();
        menu.Items.Add(toggleMode);

        var toggleBackground = new Forms::ToolStripMenuItem("Hintergrund ausblenden");
        toggleBackground.Click += (_, _) => ToggleBackground();
        menu.Items.Add(toggleBackground);

        var colorMenu = new Forms::ToolStripMenuItem("Farbe der Zahl");
        foreach (var (label, value) in OverlayColors)
        {
            var item = new Forms::ToolStripMenuItem(label) { Tag = value };
            item.Click += (s, _) => SetOverlayColor(((Forms::ToolStripMenuItem)s!).Tag as string ?? "auto");
            colorMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(colorMenu);

        menu.Items.Add(new Forms::ToolStripSeparator());

        var master = new Forms::ToolStripMenuItem("Master-Steuerung ...");
        master.Click += (_, _) => OpenMaster();
        menu.Items.Add(master);

        menu.Items.Add(new Forms::ToolStripSeparator());

        var quit = new Forms::ToolStripMenuItem("Anzeige beenden");
        quit.Click += (_, _) => QuitAgent();
        menu.Items.Add(quit);

        menu.Opening += (_, _) =>
        {
            header.Text = _status is null
                ? "Dienst nicht erreichbar"
                : $"Restzeit: {FormatMinutes(_status.BalanceSeconds)}"
                  + (_status.Paused ? "  (pausiert)" : string.Empty);

            toggleOverlay.Text = _overlayVisible ? "Overlay ausblenden" : "Overlay einblenden";
            toggleMode.Text = _overlay?.CountUp == true
                ? "Verbleibende Zeit herunterzählen"
                : "Angemeldete Zeit hochzählen";
            toggleBackground.Text = _overlayBackground ? "Hintergrund ausblenden" : "Hintergrund einblenden";

            foreach (Forms::ToolStripMenuItem item in colorMenu.DropDownItems)
                item.Checked = string.Equals(item.Tag as string, _overlayColor, StringComparison.OrdinalIgnoreCase);
        };

        _tray = new Forms::NotifyIcon
        {
            Icon = IconFor("offline"),
            Text = "TimeGuard",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenMaster();
    }

    private void UpdateTray()
    {
        if (_tray is null) return;

        string key;
        string tip;

        if (_status is null)
        {
            key = "offline";
            tip = "TimeGuard - Dienst nicht erreichbar";
        }
        else if (_status.Paused)
        {
            key = "paused";
            tip = $"TimeGuard - pausiert, {FormatMinutes(_status.BalanceSeconds)} Guthaben";
        }
        else
        {
            var minutes = _status.BalanceSeconds / 60.0;
            key = minutes switch { <= 5 => "critical", <= 15 => "warning", _ => "normal" };
            tip = $"TimeGuard - {FormatMinutes(_status.BalanceSeconds)} verbleibend, "
                  + $"{FormatMinutes(_status.SessionElapsedSeconds)} angemeldet";
        }

        _tray.Icon = IconFor(key);
        // Der Tooltip der Taskleiste ist auf 63 Zeichen begrenzt.
        _tray.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    private Drawing::Icon IconFor(string key)
    {
        if (_icons.TryGetValue(key, out var cached)) return cached;

        var color = key switch
        {
            "critical" => Drawing::Color.FromArgb(0xFF, 0x6B, 0x5E),
            "warning" => Drawing::Color.FromArgb(0xFF, 0xC1, 0x4E),
            "paused" => Drawing::Color.FromArgb(0x7A, 0xC7, 0xFF),
            "normal" => Drawing::Color.FromArgb(0x5B, 0xD6, 0x8A),
            _ => Drawing::Color.FromArgb(0x9A, 0x9A, 0xA2),
        };

        var icon = CreateDotIcon(color);
        _icons[key] = icon;
        return icon;
    }

    private static Drawing::Icon CreateDotIcon(Drawing::Color color)
    {
        using var bitmap = new Drawing::Bitmap(16, 16);
        using (var graphics = Drawing::Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing::Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing::Color.Transparent);
            using var brush = new Drawing::SolidBrush(color);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing::Icon.FromHandle(handle);
            return (Drawing::Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    // ------------------------------------------------------------- Aktionen

    private void ToggleOverlay()
    {
        if (_overlay is null) return;

        _overlayVisible = !_overlayVisible;
        AgentSettings.OverlayVisible = _overlayVisible;

        if (_overlayVisible)
        {
            _overlay.Show();
            _overlay.KeepOnTop();
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void ToggleCountMode()
    {
        if (_overlay is null) return;

        _overlay.CountUp = !_overlay.CountUp;
        AgentSettings.CountUp = _overlay.CountUp;
        _overlay.Render(_status);
    }

    private void ToggleBackground()
    {
        _overlayBackground = !_overlayBackground;
        AgentSettings.OverlayBackground = _overlayBackground;
        _overlay?.ApplyPreferences(_overlayBackground, OverlayWindow.ParseColor(_overlayColor));
    }

    private void SetOverlayColor(string value)
    {
        _overlayColor = value;
        AgentSettings.OverlayColor = value;
        _overlay?.ApplyPreferences(_overlayBackground, OverlayWindow.ParseColor(value));
    }

    private void OpenMaster()
    {
        if (_master is { IsLoaded: true })
        {
            _master.Activate();
            return;
        }

        _master = new MasterWindow();
        _master.Closed += (_, _) => _master = null;
        _master.Show();
        _master.Activate();
    }

    private void QuitAgent()
    {
        var answer = MessageBox.Show(
            "Nur die Anzeige wird beendet. Die Zeitkontrolle läuft im Dienst weiter und meldet " +
            "dich weiterhin ab, wenn das Kontingent aufgebraucht ist.\n\nAnzeige wirklich beenden?",
            "TimeGuard", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) Shutdown();
    }

    // -------------------------------------------------------- Tastenkuerzel

    private void RegisterHotkey()
    {
        if (_overlay is null) return;

        var handle = new WindowInteropHelper(_overlay).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(HotkeyHook);

        NativeMethods.RegisterHotKey(handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_T);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleOverlay();
            handled = true;
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------- Aufraeumen

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();

        if (_overlay is not null)
        {
            var handle = new WindowInteropHelper(_overlay).Handle;
            if (handle != IntPtr.Zero) NativeMethods.UnregisterHotKey(handle, HotkeyId);
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        foreach (var icon in _icons.Values) icon.Dispose();

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string FormatMinutes(double seconds)
    {
        var minutes = (int)Math.Round(Math.Max(0, seconds) / 60.0, MidpointRounding.AwayFromZero);
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00} h" : $"{minutes} min";
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
