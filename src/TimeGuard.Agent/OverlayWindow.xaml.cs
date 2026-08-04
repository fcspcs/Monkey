using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TimeGuard.Core;

namespace TimeGuard.Agent;

/// <summary>
/// Das Overlay oben rechts. Klickdurchlaessig, ohne Fokus, nicht im Alt-Tab.
/// Ausblenden aendert nichts an der Durchsetzung - die sitzt im Dienst.
///
/// Angezeigt wird entweder die verbleibende oder die bereits angemeldete Zeit;
/// der Mauszeiger darueber dreht die Anzeige jeweils auf den anderen Wert.
///
/// Der Kasten hinter der Zahl laesst sich ausblenden (dann bleibt nur die Zahl,
/// mit leichtem Schatten zur Lesbarkeit), und die Farbe der Zahl ist waehlbar.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double EdgeMargin = 16;
    private static readonly TimeSpan HoverPollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly Color Normal = Color.FromRgb(0xF2, 0xF4, 0xF8);
    private static readonly Color Warning = Color.FromRgb(0xFF, 0xC1, 0x4E);
    private static readonly Color Critical = Color.FromRgb(0xFF, 0x6B, 0x5E);
    private static readonly Color PausedColor = Color.FromRgb(0x7A, 0xC7, 0xFF);
    private static readonly Color Offline = Color.FromRgb(0x9A, 0x9A, 0xA2);

    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x14));
    private static readonly Effect PanelShadow = Freeze(new DropShadowEffect
        { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black });

    // Weicher Schlagschatten hinter der Zahl, wenn der Kasten aus ist - sonst
    // waere helle Schrift auf hellem Desktop kaum lesbar.
    private static readonly Effect TextGlow = Freeze(new DropShadowEffect
        { BlurRadius = 7, ShadowDepth = 0, Opacity = 0.95, Color = Colors.Black });

    private readonly DispatcherTimer _hoverTimer;
    private IntPtr _handle;
    private StatusDto? _last;
    private bool _hovering;

    /// <summary>
    /// true = angemeldete Zeit steht vorne, Restzeit beim Hovern. false = umgekehrt.
    /// </summary>
    public bool CountUp { get; set; }

    /// <summary>Kasten hinter der Zahl anzeigen.</summary>
    public bool ShowBackground { get; private set; } = true;

    /// <summary>Feste Farbe der Zahl, oder null fuer die Ampel nach Restzeit.</summary>
    public Color? CustomColor { get; private set; }

    public OverlayWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();

        _hoverTimer = new DispatcherTimer { Interval = HoverPollInterval };
        _hoverTimer.Tick += (_, _) => CheckHover();
        _hoverTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExtendedStyle(_handle,
            NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        Reposition();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - EdgeMargin;
        Top = area.Top + EdgeMargin;
    }

    public void KeepOnTop()
    {
        if (_handle != IntPtr.Zero && IsVisible)
            NativeMethods.BringToTop(_handle);
    }

    /// <summary>Anzeigevorlieben setzen und sofort neu zeichnen.</summary>
    public void ApplyPreferences(bool showBackground, Color? customColor)
    {
        ShowBackground = showBackground;
        CustomColor = customColor;
        Paint();
    }

    private void CheckHover()
    {
        if (!IsVisible || _handle == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        bool inside;
        try
        {
            var local = PointFromScreen(new Point(cursor.X, cursor.Y));
            inside = local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return; // Fenster gerade nicht verbunden.
        }

        if (inside == _hovering) return;

        _hovering = inside;
        Paint();
    }

    public void Render(StatusDto? status)
    {
        _last = status;
        Paint();
    }

    private void Paint()
    {
        ApplyChrome();

        var status = _last;

        if (status is null)
        {
            TimeLabel.Text = "--:--";
            CaptionLabel.Text = "Dienst nicht erreichbar";
            Colorize(CustomColor ?? Offline);
            return;
        }

        // Waehrend der Schonfrist zaehlt es sekundengenau herunter und bleibt immer
        // rot - eine feste Farbe wuerde diese Warnung entschaerfen.
        if (status.SecondsUntilLogoff is { } grace)
        {
            TimeLabel.Text = $"{Math.Ceiling(grace):0} s";
            CaptionLabel.Text = "Abmeldung - jetzt speichern";
            Colorize(Critical);
            return;
        }

        var showElapsed = CountUp ^ _hovering;

        TimeLabel.Text = showElapsed
            ? FormatElapsed(status.SessionElapsedSeconds)
            : FormatRemaining(status.BalanceSeconds);

        if (status.Paused)
        {
            CaptionLabel.Text = status.PauseUntil is { } until ? $"pausiert bis {until:HH:mm}" : "pausiert";
            Colorize(CustomColor ?? PausedColor);
            return;
        }

        CaptionLabel.Text = showElapsed
            ? (status.Counting ? "angemeldet" : "angemeldet, steht")
            : (status.Counting ? "verbleibend" : "verbleibend, steht");

        if (CustomColor is { } chosen)
        {
            Colorize(chosen);
            return;
        }

        // Ampel nach Restzeit, wenn keine feste Farbe gewaehlt ist.
        var remainingMinutes = status.BalanceSeconds / 60.0;
        Colorize(remainingMinutes switch
        {
            <= 5 => Critical,
            <= 15 => Warning,
            _ => status.Counting ? Normal : Offline,
        });
    }

    /// <summary>Kasten und Beschriftung ein- oder ausblenden.</summary>
    private void ApplyChrome()
    {
        if (ShowBackground)
        {
            Panel.Background = PanelBrush;
            Panel.BorderThickness = new Thickness(1);
            Panel.Effect = PanelShadow;
            TimeLabel.Effect = null;
            CaptionLabel.Visibility = Visibility.Visible;
        }
        else
        {
            // Nur die Zahl: Kasten weg, Beschriftung weg, Schatten fuer Lesbarkeit.
            Panel.Background = Brushes.Transparent;
            Panel.BorderThickness = new Thickness(0);
            Panel.Effect = null;
            TimeLabel.Effect = TextGlow;
            CaptionLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void Colorize(Color color) => TimeLabel.Foreground = new SolidColorBrush(color);

    /// <summary>"#RRGGBB" oder "#AARRGGBB" in eine Farbe, sonst null (= automatisch).</summary>
    public static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        var hex = value.TrimStart('#');
        if ((hex.Length == 6 || hex.Length == 8)
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            var argb = hex.Length == 6 ? 0xFF000000 | packed : packed;
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        return null;
    }

    private static Effect Freeze(Effect effect)
    {
        effect.Freeze();
        return effect;
    }

    private static string FormatRemaining(double seconds) =>
        Compose((int)Math.Ceiling(Math.Max(0, seconds) / 60.0));

    private static string FormatElapsed(double seconds) =>
        Compose((int)Math.Floor(Math.Max(0, seconds) / 60.0));

    private static string Compose(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00} h" : $"{minutes} min";
}
