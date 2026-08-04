using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Monkey.Core;

namespace Monkey.Agent;

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

    /// <summary>
    /// Durchsichtiger Rand zwischen Kasten und Fensterkante. Das Fenster ist genau
    /// so gross wie sein Inhalt, ein Schlagschatten zeichnet aber ausserhalb davon -
    /// ohne diesen Rand bliebe vom Schatten nur der Teil uebrig, der zufaellig noch
    /// ins Fenster faellt. Reposition rechnet ihn wieder heraus.
    /// </summary>
    private const double ShadowGutter = 14;

    /// <summary>Farbwert fuer die Ampel in dunklen Toenen.</summary>
    public const string AutoDark = "auto-dark";
    /// <summary>Farbwert fuer die Ampel in hellen Toenen.</summary>
    public const string AutoLight = "auto";
    // Bestimmt auch, wie schnell das Overlay nach dem Hinfahren Klicks annimmt.
    private static readonly TimeSpan HoverPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Farbsatz fuer die Ampel. Es gibt zwei davon: einen hellen fuer dunkle
    /// Hintergruende und einen dunklen fuer helle - auf einem weissen Desktop
    /// waere die helle Fassung sonst kaum zu lesen.
    /// </summary>
    private readonly record struct Palette(Color Normal, Color Warning, Color Critical, Color Paused, Color Idle);

    private static readonly Palette LightPalette = new(
        Normal: Color.FromRgb(0xF2, 0xF4, 0xF8),
        Warning: Color.FromRgb(0xFF, 0xC1, 0x4E),
        Critical: Color.FromRgb(0xFF, 0x6B, 0x5E),
        Paused: Color.FromRgb(0x7A, 0xC7, 0xFF),
        Idle: Color.FromRgb(0x9A, 0x9A, 0xA2));

    private static readonly Palette DarkPalette = new(
        Normal: Color.FromRgb(0x14, 0x14, 0x1A),
        Warning: Color.FromRgb(0xA8, 0x66, 0x00),
        Critical: Color.FromRgb(0xC0, 0x2E, 0x22),
        Paused: Color.FromRgb(0x1B, 0x63, 0xA6),
        Idle: Color.FromRgb(0x6A, 0x6A, 0x72));

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

    /// <summary>Ampel in dunklen Toenen - fuer helle Desktops.</summary>
    private bool _autoDark;

    private Palette Tones => _autoDark ? DarkPalette : LightPalette;

    public OverlayWindow()
    {
        InitializeComponent();
        Panel.Margin = new Thickness(ShadowGutter);
        SizeChanged += (_, _) => Reposition();

        _hoverTimer = new DispatcherTimer { Interval = HoverPollInterval };
        _hoverTimer.Tick += (_, _) => CheckHover();
        _hoverTimer.Start();
    }

    /// <summary>Wird ausgeloest, wenn auf das Overlay geklickt wird.</summary>
    public event EventHandler? Clicked;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExtendedStyle(_handle,
            NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        Reposition();
    }

    private void OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Clicked?.Invoke(this, EventArgs.Empty);

    /// <summary>In welcher Bildschirmecke das Overlay sitzt.</summary>
    public OverlayCorner Corner { get; private set; } = OverlayCorner.TopRight;

    public void SetCorner(OverlayCorner corner)
    {
        Corner = corner;
        Reposition();
    }

    /// <summary>
    /// Haengt das Overlay in die gewaehlte Ecke. Weil sich die Groesse aendert,
    /// sobald das Symbol ein- oder ausfaehrt, wird von der jeweils verankerten
    /// Kante aus gerechnet - unten waechst es dann nach oben statt aus dem Bild.
    ///
    /// Gemessen wird der Kasten, nicht das Fenster: der Rand fuer den Schatten
    /// wird herausgerechnet, sonst rutschte das Overlay um dessen Breite von der
    /// Ecke weg.
    /// </summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;

        var left = Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft;
        var top = Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;

        var inset = EdgeMargin - ShadowGutter;

        Left = left ? area.Left + inset : area.Right - ActualWidth - inset;
        Top = top ? area.Top + inset : area.Bottom - ActualHeight - inset;
    }

    public void KeepOnTop()
    {
        if (_handle != IntPtr.Zero && IsVisible)
            NativeMethods.BringToTop(_handle);
    }

    /// <summary>
    /// Anzeigevorlieben setzen. Der Farbwert ist entweder "auto" (helle Ampel),
    /// "auto-dark" (dunkle Ampel) oder ein fester Hex-Wert.
    /// </summary>
    public void ApplyPreferences(bool showBackground, string colorValue)
    {
        ShowBackground = showBackground;
        _autoDark = string.Equals(colorValue, AutoDark, StringComparison.OrdinalIgnoreCase);
        CustomColor = ParseColor(colorValue);
        Paint();
    }

    private void CheckHover()
    {
        if (!IsVisible || _handle == IntPtr.Zero)
        {
            // Ausgeblendet: Zustand zuruecksetzen, damit das Fenster nicht
            // klickfangend bleibt, wenn der Zeiger beim Ausblenden darueber stand.
            if (_hovering)
            {
                _hovering = false;
                NativeMethods.SetClickThrough(_handle, clickThrough: true);
            }
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        bool inside;
        try
        {
            // Gegen den Kasten geprueft, nicht gegen das Fenster: der Rand fuer den
            // Schatten gehoert nicht zur Anzeige und soll sie auch nicht umschalten.
            var local = Panel.PointFromScreen(new Point(cursor.X, cursor.Y));
            inside = local.X >= 0 && local.Y >= 0
                     && local.X <= Panel.ActualWidth && local.Y <= Panel.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return; // Fenster gerade nicht verbunden.
        }

        if (inside == _hovering) return;

        _hovering = inside;

        // Klickdurchlaessig bleiben, solange der Zeiger woanders ist - sonst waere
        // die Ecke des Bildschirms blockiert. Nur unter dem Zeiger nimmt das
        // Overlay Klicks an, damit es sich anklicken laesst.
        NativeMethods.SetClickThrough(_handle, clickThrough: !inside);

        Paint();
    }

    /// <summary>
    /// Gesamthoehe des ausgefahrenen Symbols: Abstand darueber, Bild, und darunter
    /// noch etwas Luft fuer den Schatten - der Vorhang beschneidet alles, was ueber
    /// diese Hoehe hinausragt.
    /// </summary>
    private const double HoverIconHeight = 50;

    private static readonly Duration HoverFade = new(TimeSpan.FromMilliseconds(160));

    private bool? _iconShown;

    /// <summary>
    /// Blendet das Symbol weich ein und aus, statt es hart umzuschalten. Animiert
    /// werden Hoehe und Deckkraft gemeinsam, damit das Overlay nicht springt.
    /// </summary>
    private void AnimateHoverIcon(bool show)
    {
        if (_iconShown == show) return;
        _iconShown = show;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        HoverIconBox.BeginAnimation(HeightProperty,
            new DoubleAnimation(show ? HoverIconHeight : 0, HoverFade) { EasingFunction = ease });
        HoverIconBox.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, HoverFade) { EasingFunction = ease });
    }

    public void Render(StatusDto? status)
    {
        _last = status;
        Paint();
    }

    private void Paint()
    {
        ApplyChrome();

        AnimateHoverIcon(_hovering);

        var status = _last;

        if (status is null)
        {
            TimeLabel.Text = "--:--";
            CaptionLabel.Text = "service unreachable";
            Colorize(CustomColor ?? Tones.Idle);
            return;
        }

        // Waehrend der Schonfrist zaehlt es sekundengenau herunter und bleibt immer
        // rot - eine feste Farbe wuerde diese Warnung entschaerfen.
        if (status.SecondsUntilLogoff is { } grace)
        {
            TimeLabel.Text = $"{Math.Ceiling(grace):0} s";
            CaptionLabel.Text = "signing out - save now";
            Colorize(Tones.Critical);
            return;
        }

        var showElapsed = CountUp ^ _hovering;

        TimeLabel.Text = showElapsed
            ? FormatElapsed(status.SessionElapsedSeconds)
            : FormatRemaining(status.BalanceSeconds);

        if (status.Paused)
        {
            // Auch hier wechselt die Zahl beim Hovern - dann erklaeren, was sie
            // bedeutet, statt nur die Pausendauer zu zeigen. Bewusst derselbe
            // kurze Text wie sonst, damit die Breite beim Hovern nicht springt;
            // dass pausiert ist, zeigt bereits die Farbe.
            CaptionLabel.Text = _hovering
                ? (showElapsed ? "used so far" : "still left")
                : (status.PauseUntil is { } until ? $"paused until {until:HH:mm}" : "paused");
            Colorize(CustomColor ?? Tones.Paused);
            return;
        }

        // Beim Hovern wird der jeweils andere Wert gezeigt - dann sagt die
        // Beschriftung ausdruecklich, was die Zahl darueber bedeutet.
        if (_hovering)
        {
            CaptionLabel.Text = showElapsed ? "used so far" : "still left";
        }
        else
        {
            CaptionLabel.Text = showElapsed
                ? (status.Counting ? "signed in" : "signed in, paused")
                : (status.Counting ? "left" : "left, paused");
        }

        if (CustomColor is { } chosen)
        {
            Colorize(chosen);
            return;
        }

        // Ampel nach Restzeit, wenn keine feste Farbe gewaehlt ist.
        var remainingMinutes = status.BalanceSeconds / 60.0;
        Colorize(remainingMinutes switch
        {
            <= 5 => Tones.Critical,
            <= 15 => Tones.Warning,
            _ => status.Counting ? Tones.Normal : Tones.Idle,
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
            CaptionLabel.Effect = null;
            HoverIcon.Effect = null;
            CaptionLabel.Visibility = Visibility.Visible;
        }
        else
        {
            // Nur die Zahl: Kasten weg, Schatten fuer Lesbarkeit. Die Beschriftung
            // bleibt aus - ausser beim Hovern, wo sie erklaert, was die gerade
            // gezeigte Zahl bedeutet.
            Panel.Background = Brushes.Transparent;
            Panel.BorderThickness = new Thickness(0);
            Panel.Effect = null;
            TimeLabel.Effect = TextGlow;
            CaptionLabel.Effect = TextGlow;
            HoverIcon.Effect = TextGlow;
            CaptionLabel.Visibility = _hovering ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Colorize(Color color) => TimeLabel.Foreground = new SolidColorBrush(color);

    /// <summary>"#RRGGBB" oder "#AARRGGBB" in eine Farbe, sonst null (= automatisch).</summary>
    public static Color? ParseColor(string? value)
    {
        // Beide Automatik-Werte bedeuten "keine feste Farbe" - welcher Farbsatz
        // dann gilt, entscheidet ApplyPreferences.
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals(AutoLight, StringComparison.OrdinalIgnoreCase)
            || value.Equals(AutoDark, StringComparison.OrdinalIgnoreCase))
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
