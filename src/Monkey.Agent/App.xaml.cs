using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Monkey.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Monkey.Agent;

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
    private string _overlayColor = OverlayWindow.AutoLight;
    private OverlayCorner _overlayCorner = OverlayCorner.TopRight;

    private static readonly (string Label, OverlayCorner Value)[] OverlayCorners =
    [
        ("Top left", OverlayCorner.TopLeft),
        ("Top right", OverlayCorner.TopRight),
        ("Bottom left", OverlayCorner.BottomLeft),
        ("Bottom right", OverlayCorner.BottomRight),
    ];
    private int? _lastWarningShown;

    private static readonly (string Label, string Value)[] OverlayColors =
    [
        ("Automatic, light", OverlayWindow.AutoLight),
        ("Automatic, dark", OverlayWindow.AutoDark),
        ("White", "#F2F4F8"),
        ("Black", "#14141A"),
        ("Green", "#5BD68A"),
        ("Blue", "#7AC7FF"),
        ("Yellow", "#FFC14E"),
        ("Orange", "#FF9F40"),
        ("Red", "#FF6B5E"),
        ("Pink", "#FF7AC7"),
        ("Purple", "#B57AFF"),
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nur fuer Testlaeufe gegen eine zweite, umgeleitete Dienstinstanz.
        var pipeOverride = ArgumentValue(e.Args, "--pipe");
        if (pipeOverride is not null) Paths.UseTestLocation(null, pipeOverride);

        // Nach einem Selbst-Update: erst warten, bis der alte Agent weg ist,
        // sonst scheitert diese Instanz gleich am Einzelinstanz-Mutex.
        if (ArgumentValue(e.Args, "--restart") is { } oldPid && int.TryParse(oldPid, out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); }
            catch { /* schon weg */ }
        }

        _singleInstance = new Mutex(true, Paths.MutexName + (pipeOverride ?? string.Empty), out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _overlayVisible = AgentSettings.OverlayVisible;

        _overlayBackground = AgentSettings.OverlayBackground;
        _overlayColor = AgentSettings.OverlayColor;

        _overlayCorner = AgentSettings.OverlayCorner;

        _overlay = new OverlayWindow { CountUp = AgentSettings.CountUp };
        _overlay.SetCorner(_overlayCorner);
        _overlay.ApplyPreferences(_overlayBackground, _overlayColor);
        _overlay.Clicked += (_, _) => OpenMaster();
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
            DisplayOff = _displayOff,
        });

        _status = response?.Status;

        _overlay?.Render(_status);
        if (_overlayVisible) _overlay?.KeepOnTop();

        ShowWarningIfDue();
        UpdateTray();
        RestartIfOutdated();
    }

    private bool _restartQueued;

    /// <summary>
    /// Nach einem Auto-Update laeuft dieser Prozess noch als alte Version von
    /// einer beiseite gelegten Datei weiter. Sobald der Dienst eine andere
    /// Version meldet UND auf der Platte wirklich eine andere Agent-Fassung
    /// liegt, startet die Anzeige sich selbst neu. Der Datei-Vergleich
    /// verhindert eine Neustartschleife, falls nur der Dienst getauscht wurde.
    /// </summary>
    private void RestartIfOutdated()
    {
        if (_restartQueued || _status?.ServiceVersion is not { } serviceVersion) return;

        var mine = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var mineText = $"{Math.Max(mine.Major, 0)}.{Math.Max(mine.Minor, 0)}.{Math.Max(mine.Build, 0)}";
        if (serviceVersion == mineText) return;

        // Nicht mitten aus einer Passworteingabe kippen - dann eben, sobald das
        // Fenster wieder zu ist.
        if (_master is { IsLoaded: true }) return;

        if (Path.GetDirectoryName(Environment.ProcessPath) is not { } dir) return;
        var exe = Path.Combine(dir, "MonkeyAgent.exe");
        if (!File.Exists(exe)) return;

        try
        {
            var onDisk = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (onDisk is null || !Version.TryParse(onDisk, out var disk)) return;
            var diskText = $"{Math.Max(disk.Major, 0)}.{Math.Max(disk.Minor, 0)}.{Math.Max(disk.Build, 0)}";
            if (diskText == mineText) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                ArgumentList = { "--restart", Environment.ProcessId.ToString() },
            });
            _restartQueued = true;
            Shutdown();
        }
        catch
        {
            // Dann eben beim naechsten Anmelden - der Autostart nimmt ohnehin
            // die neue Datei.
        }
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

        var header = new Forms::ToolStripMenuItem("Time left: --") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Forms::ToolStripSeparator());

        var toggleOverlay = new Forms::ToolStripMenuItem("Hide overlay");
        toggleOverlay.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(toggleOverlay);

        var toggleMode = new Forms::ToolStripMenuItem("Count time used instead");
        toggleMode.Click += (_, _) => ToggleCountMode();
        menu.Items.Add(toggleMode);

        var toggleBackground = new Forms::ToolStripMenuItem("Hide background");
        toggleBackground.Click += (_, _) => ToggleBackground();
        menu.Items.Add(toggleBackground);

        var cornerMenu = new Forms::ToolStripMenuItem("Screen corner");
        foreach (var (label, value) in OverlayCorners)
        {
            var item = new Forms::ToolStripMenuItem(label) { Tag = value };
            item.Click += (s, _) =>
            {
                if (((Forms::ToolStripMenuItem)s!).Tag is OverlayCorner chosen) SetOverlayCorner(chosen);
            };
            cornerMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(cornerMenu);

        var colorMenu = new Forms::ToolStripMenuItem("Number colour");
        foreach (var (label, value) in OverlayColors)
        {
            var item = new Forms::ToolStripMenuItem(label) { Tag = value };
            item.Click += (s, _) => SetOverlayColor(((Forms::ToolStripMenuItem)s!).Tag as string ?? "auto");
            colorMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(colorMenu);

        menu.Items.Add(new Forms::ToolStripSeparator());

        var master = new Forms::ToolStripMenuItem("Control panel ...");
        master.Click += (_, _) => OpenMaster();
        menu.Items.Add(master);

        menu.Items.Add(new Forms::ToolStripSeparator());

        var quit = new Forms::ToolStripMenuItem("Quit display");
        quit.Click += (_, _) => QuitAgent();
        menu.Items.Add(quit);

        menu.Opening += (_, _) =>
        {
            header.Text = _status is null
                ? "Service unreachable"
                : $"Time left: {FormatMinutes(_status.BalanceSeconds)}"
                  + (_status.Paused ? "  (paused)" : string.Empty);

            toggleOverlay.Text = _overlayVisible ? "Hide overlay" : "Show overlay";
            toggleMode.Text = _overlay?.CountUp == true
                ? "Count time left instead"
                : "Count time used instead";
            toggleBackground.Text = _overlayBackground ? "Hide background" : "Show background";

            foreach (Forms::ToolStripMenuItem item in colorMenu.DropDownItems)
                item.Checked = string.Equals(item.Tag as string, _overlayColor, StringComparison.OrdinalIgnoreCase);

            foreach (Forms::ToolStripMenuItem item in cornerMenu.DropDownItems)
                item.Checked = item.Tag is OverlayCorner c && c == _overlayCorner;
        };

        _tray = new Forms::NotifyIcon
        {
            Icon = TrayImage(),
            Text = "Monkey",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenMaster();
    }

    private void UpdateTray()
    {
        if (_tray is null) return;

        // Das Symbol bleibt immer dasselbe; den Zustand sagt der Tooltip.
        var tip = _status switch
        {
            null => "Monkey - service unreachable",
            { Paused: true } => $"Monkey - paused, {FormatMinutes(_status.BalanceSeconds)} banked",
            _ => $"Monkey - {FormatMinutes(_status.BalanceSeconds)} left, "
                 + $"{FormatMinutes(_status.SessionElapsedSeconds)} used",
        };

        // Der Tooltip der Taskleiste ist auf 63 Zeichen begrenzt.
        _tray.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    private Drawing::Icon? _trayImage;

    /// <summary>
    /// Das Tray-Symbol: schlicht der Affe, ohne Statuspunkt. Wie es um die Zeit
    /// steht, sagt der Tooltip - und wer es genau wissen will, klickt auf das
    /// Overlay. Faellt das eingebettete Symbol aus, springt das Standardsymbol
    /// ein, damit der Agent trotzdem im Infobereich auftaucht.
    /// </summary>
    private Drawing::Icon TrayImage()
    {
        if (_trayImage is not null) return _trayImage;

        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("monkey.ico");

            if (stream is not null)
            {
                // Die Groesse, die Windows im Infobereich erwartet - sonst wird
                // skaliert und das Symbol wirkt unscharf.
                var size = Forms::SystemInformation.SmallIconSize;
                _trayImage = new Drawing::Icon(stream, size.Width, size.Height);
            }
        }
        catch
        {
            _trayImage = null;
        }

        _trayImage ??= Drawing::SystemIcons.Application;
        return _trayImage;
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
        _overlay?.ApplyPreferences(_overlayBackground, _overlayColor);
    }

    private void SetOverlayColor(string value)
    {
        _overlayColor = value;
        AgentSettings.OverlayColor = value;
        _overlay?.ApplyPreferences(_overlayBackground, value);
    }

    private void SetOverlayCorner(OverlayCorner corner)
    {
        _overlayCorner = corner;
        AgentSettings.OverlayCorner = corner;
        _overlay?.SetCorner(corner);
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
            "This only closes the display. The time limit keeps running in the service and " +
            "will still sign you out when the balance runs out.\n\nQuit the display?",
            "Monkey", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) Shutdown();
    }

    // ------------------------------------ Tastenkuerzel und Anzeigezustand

    /// <summary>Vom Power-Broadcast gemeldet: Bildschirm ist gerade aus.</summary>
    private bool _displayOff;

    private IntPtr _displayNotification = IntPtr.Zero;

    private void RegisterHotkey()
    {
        if (_overlay is null) return;

        var handle = new WindowInteropHelper(_overlay).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(WindowHook);

        NativeMethods.RegisterHotKey(handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_T);

        // Ausgeschalteter Monitor zaehlt wie Bildschirmschoner - siehe
        // NativeMethods. Ohne diese Registrierung kommt die Meldung nie an.
        _displayNotification = NativeMethods.RegisterDisplayStateNotifications(handle);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleOverlay();
            handled = true;
        }
        else if (msg == NativeMethods.WM_POWERBROADCAST
                 && wParam.ToInt32() == NativeMethods.PBT_POWERSETTINGCHANGE
                 && NativeMethods.TryReadDisplayOff(lParam, out var displayOff))
        {
            _displayOff = displayOff;

            // Bildschirm wieder an: nicht auf den naechsten Timertakt warten,
            // sonst zaehlt der Dienst bis zu zwei Sekunden zu wenig - und beim
            // Ausschalten umgekehrt zu viel.
            _ = PollAsync();
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

        if (_displayNotification != IntPtr.Zero)
            NativeMethods.UnregisterPowerSettingNotification(_displayNotification);

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        // SystemIcons.Application gehoert dem System und wird nicht freigegeben.
        if (_trayImage is not null && _trayImage != Drawing::SystemIcons.Application)
            _trayImage.Dispose();

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
