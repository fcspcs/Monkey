using System.Windows;

namespace Monkey.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
