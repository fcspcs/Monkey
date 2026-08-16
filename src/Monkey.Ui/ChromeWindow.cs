using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Monkey.Ui;

/// <summary>
/// Fenster mit selbst gezeichneter Titelleiste. Die Leiste liegt <em>ueber</em>
/// dem Inhalt statt darueber zu thronen - so kann eine randlose Grafik bis an
/// die Oberkante laufen. Wer Platz fuer die Leiste braucht, haelt seinen Inhalt
/// mit <c>CaptionInset</c> aus dem Weg.
///
/// Rahmen, Groesse und Anschnappen bleiben Sache von Windows: der Stil in
/// Theme.xaml haengt lediglich ein <see cref="System.Windows.Shell.WindowChrome"/>
/// an. Damit funktionieren Aero-Snap, Doppelklick auf die Leiste, das
/// Systemmenue und das Ziehen aus dem Vollbild heraus unveraendert - nur
/// gezeichnet wird die Leiste von uns.
/// </summary>
public class ChromeWindow : Window
{
    /// <summary>
    /// Abstand des Fenstertitels vom linken Fensterrand. Fenster mit einer
    /// randlosen Seitenspalte schieben den Titel damit neben die Spalte.
    /// </summary>
    public static readonly DependencyProperty CaptionPaddingProperty =
        DependencyProperty.Register(
            nameof(CaptionPadding), typeof(Thickness), typeof(ChromeWindow),
            new PropertyMetadata(new Thickness(16, 0, 0, 0)));

    public Thickness CaptionPadding
    {
        get => (Thickness)GetValue(CaptionPaddingProperty);
        set => SetValue(CaptionPaddingProperty, value);
    }

    private Border? _root;

    protected ChromeWindow()
    {
        StateChanged += (_, _) => ApplyMaximizedInset();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _root = GetTemplateChild("PART_Root") as Border;

        Wire("PART_Minimize", () => WindowState = WindowState.Minimized);
        Wire("PART_Maximize", ToggleMaximised);
        Wire("PART_Close", Close);

        ApplyMaximizedInset();
    }

    private void Wire(string part, Action action)
    {
        if (GetTemplateChild(part) is ButtonBase button)
            button.Click += (_, _) => action();
    }

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// Ein maximiertes Fenster mit eigenem Rahmen wird von Windows um die
    /// unsichtbare Anfasskante groesser gemacht als der Arbeitsbereich - ohne
    /// Ausgleich liefe der Inhalt ueber den Bildschirmrand hinaus.
    /// </summary>
    private void ApplyMaximizedInset()
    {
        if (_root is null) return;

        _root.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }
}
