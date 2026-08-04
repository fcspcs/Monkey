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

    private readonly DispatcherTimer _closeTimer;

    public WarningWindow(int minutes)
    {
        InitializeComponent();

        Headline.Text = minutes == 1 ? "Noch 1 Minute" : $"Noch {minutes} Minuten";
        Subline.Text = "Computerzeit für heute";

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
        Top = area.Top + area.Height * 0.12;
    }

    private void OnClick(object sender, MouseButtonEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
