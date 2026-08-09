using System.Windows;

namespace Monkey.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Stiller Update-Modus: der Dienst startet den (bereits per Signatur und
        // Hash gepruefen) neuen Installer mit diesem Argument. Kein Fenster,
        // kein Passwort - getauscht werden nur die Programmdateien.
        if (e.Args.Any(a => string.Equals(a, "update", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SetupEngine.IsElevated())
            {
                Shutdown(2);
                return;
            }

            var ok = SetupEngine.UpdateInPlace(out var error);
            if (!ok) SetupEngine.TryLog($"Update failed: {error}");
            Shutdown(ok ? 0 : 3);
            return;
        }

        // The manifest asks for administrator rights; without them nothing here works.
        if (!SetupEngine.IsElevated())
        {
            MessageBox.Show(
                "Please start this with right-click > \"Run as administrator\".",
                "Monkey Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        MainWindow = new WizardWindow();
        MainWindow.Show();
    }
}
