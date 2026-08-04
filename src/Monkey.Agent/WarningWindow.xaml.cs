using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Monkey.Agent;

/// <summary>
/// Die eigentliche Warnung. Ein eigenes Fenster statt einer Sprechblase, weil
/// Sprechblasen unter Windows 10 je nach Benachrichtigungseinstellungen
/// stillschweigend verschluckt werden.
///
/// Es nimmt bewusst nie den Fokus - eine Warnung darf nicht mitten im Tippen
/// oder im Spiel die Eingabe abfangen.
/// </summary>
public partial class WarningWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Durchsichtiger Rand zwischen Karte und Fensterkante, damit der Schlagschatten
    /// ringsum Platz hat - er zeichnet ausserhalb des Layouts, das Fenster ist aber
    /// genau so gross wie sein Inhalt. Reposition rechnet ihn wieder heraus.
    /// </summary>
    private const double ShadowGutter = 20;

    private readonly DispatcherTimer _closeTimer;

    public WarningWindow(int minutes)
    {
        InitializeComponent();
        Card.Margin = new Thickness(ShadowGutter);

        Headline.Text = minutes == 1 ? "1 minute left" : $"{minutes} minutes left";
        Subline.Text = "of screen time today";

        if (minutes <= 1)
            Headline.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E));

        _closeTimer = new DispatcherTimer { Interval = Lifetime };
        _closeTimer.Tick += (_, _) => Close();
        _closeTimer.Start();

        Loaded += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExtendedStyle(handle,
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + area.Height * 0.12 - ShadowGutter;
    }

    private void OnClick(object sender, MouseButtonEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
