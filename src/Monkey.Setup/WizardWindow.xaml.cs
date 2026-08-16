using System.Globalization;
using System.IO;
using System.Windows;
using Monkey.Core;
using Monkey.Ui;

namespace Monkey.Setup;

/// <summary>
/// The setup wizard. It only collects input and shows progress - the actual work
/// happens in <see cref="SetupEngine"/>.
/// </summary>
public partial class WizardWindow : ChromeWindow
{
    private enum Page { Start, Install, Uninstall, Progress }

    private Page _page = Page.Start;
    private bool _replacementRequired;
    private bool _busy;

    public WizardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowStart();
    }

    // ------------------------------------------------------------------- Pages

    private void ShowStart()
    {
        _page = Page.Start;
        SetVisible(PageStart);

        BackButton.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Quit";
        FooterMessage.Text = string.Empty;

        if (SetupEngine.LegacyInstalled())
        {
            StatusLine.Text = "Heads up: the older version \"TimeGuard\" is still installed. " +
                              "Remove it first with the old TimeGuardSetup.exe (option 2, old master " +
                              "password) — otherwise its service keeps signing you out.";
            StartInstallButton.IsEnabled = false;
            return;
        }

        if (!SetupEngine.HasPayload())
        {
            StatusLine.Text = "This setup file has no program files inside. Please rebuild it.";
            StartInstallButton.IsEnabled = false;
            StartUninstallButton.IsEnabled = false;
            return;
        }

        var installed = SetupEngine.ServiceInstalled();
        StatusLine.Text = installed
            ? "Monkey is installed on this machine."
            : "Monkey is not installed on this machine yet.";

        StartUninstallButton.IsEnabled = installed || Directory.Exists(SetupEngine.TargetDir);
    }

    private void OnGoToInstall(object sender, RoutedEventArgs e)
    {
        _page = Page.Install;
        SetVisible(PageInstall);

        HeadSub.Text = "Setting things up";
        BackButton.Visibility = Visibility.Visible;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.Content = "Install";
        CloseButton.Content = "Cancel";
        FooterMessage.Text = string.Empty;

        // If a protected installation already exists, the old password is required
        // too - otherwise reinstalling would be a way around the protection.
        _replacementRequired = SetupEngine.InstallationPresent();
        ExistingPanel.Visibility = _replacementRequired ? Visibility.Visible : Visibility.Collapsed;

        PasswordBox1.Focus();
    }

    private void OnGoToUninstall(object sender, RoutedEventArgs e)
    {
        _page = Page.Uninstall;
        SetVisible(PageUninstall);

        HeadSub.Text = "Removing";
        BackButton.Visibility = Visibility.Visible;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.Content = "Remove";
        CloseButton.Content = "Cancel";
        FooterMessage.Text = string.Empty;

        UninstallPasswordBox.Focus();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        HeadSub.Text = "Daily screen time that rolls over";
        ShowStart();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private void SetVisible(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { PageStart, PageInstall, PageUninstall, PageProgress })
            p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------------- Actions

    /// <summary>
    /// Beide Felder bekommen denselben Vorschlag und werden enttarnt: Das
    /// Passwort soll ja gerade nicht auf diesem Rechner bleiben, sondern
    /// notiert oder einer Vertrauensperson uebergeben werden.
    /// </summary>
    private void OnGeneratePassword(object sender, RoutedEventArgs e)
    {
        var generated = PasswordGenerator.Create();
        PasswordBox1.Password = generated;
        PasswordBox2.Password = generated;
        PasswordBox1.RevealSecret();
        PasswordBox2.RevealSecret();
    }

    private void OnCopyGeneratedPassword(object sender, RoutedEventArgs e)
    {
        if (PasswordBox1.Password.Length == 0)
        {
            Fail("There is no password to copy yet - type or generate one first.");
            return;
        }

        try { Clipboard.SetText(PasswordBox1.Password); }
        catch (Exception ex) { Fail($"Could not copy to the clipboard: {ex.Message}"); }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_page == Page.Install) await RunInstallAsync();
        else if (_page == Page.Uninstall) await RunUninstallAsync();
    }

    private async Task RunInstallAsync()
    {
        if (!TryReadInt(DailyBox.Text, out var daily) ||
            !TryReadInt(CapBox.Text, out var cap) ||
            !TryReadInt(GrantBox.Text, out var grant))
        {
            Fail("Please enter whole numbers in all number fields.");
            return;
        }

        if (daily < 0 || cap < daily || grant < 0)
        {
            Fail("The balance cap can't be smaller than the daily allowance.");
            return;
        }

        var password = PasswordBox1.Password;
        if (password.Length < PasswordHash.MinimumLength)
        {
            Fail($"The master password needs at least {PasswordHash.MinimumLength} characters.");
            return;
        }
        if (password != PasswordBox2.Password)
        {
            Fail("The two passwords don't match.");
            return;
        }

        var currentPassword = ExistingPasswordBox.Password;
        if (_replacementRequired && currentPassword.Length == 0)
        {
            Fail("Please enter the current master password.");
            return;
        }

        BeginProgress("Setting up Monkey …");

        var options = new SetupEngine.InstallOptions(password, daily, cap, grant);
        var ok = false;
        var error = string.Empty;

        await Task.Run(() =>
        {
            try { ok = SetupEngine.Install(options, currentPassword, Report, out error); }
            catch (Exception ex) { error = ex.Message; }
        });

        if (!ok)
        {
            EndProgress(false, error.Length > 0 ? error : "Setup failed.");
            return;
        }

        EndProgress(true,
            $"All set. {daily} minutes a day, up to {cap} minutes saved up. " +
            "Your remaining time sits in the top right — click it to open the control panel.");
    }

    private async Task RunUninstallAsync()
    {
        var password = UninstallPasswordBox.Password;
        if (password.Length == 0)
        {
            Fail("Please enter the master password.");
            return;
        }

        BeginProgress("Removing Monkey …");

        var ok = false;
        var error = string.Empty;

        await Task.Run(() =>
        {
            try { ok = SetupEngine.Uninstall(password, Report, out error); }
            catch (Exception ex) { error = ex.Message; }
        });

        if (!ok)
        {
            EndProgress(false, error.Length > 0 ? error : "Removal failed.");
            return;
        }

        EndProgress(true, "Monkey is gone.");
    }

    // ---------------------------------------------------------------- Progress

    private void BeginProgress(string title)
    {
        _busy = true;
        _page = Page.Progress;
        SetVisible(PageProgress);

        ProgressTitle.Text = title;
        LogText.Text = string.Empty;
        Progress.IsIndeterminate = true;

        BackButton.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = false;
        FooterMessage.Text = string.Empty;
    }

    private void Report(string line) => Dispatcher.Invoke(() =>
    {
        LogText.Text += (LogText.Text.Length > 0 ? "\n" : string.Empty) + line;
        LogScroller.ScrollToEnd();
    });

    private void EndProgress(bool success, string message)
    {
        _busy = false;
        Progress.IsIndeterminate = false;
        Progress.Value = success ? 100 : 0;

        ProgressTitle.Text = success ? "Done" : "Didn't work";
        FooterMessage.Text = message;

        CloseButton.IsEnabled = true;
        CloseButton.Content = "Close";

        // After a failure, offer the way back so the attempt can be repeated;
        // after success there is nothing left to do but close.
        BackButton.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Fail(string message) => FooterMessage.Text = message;

    private static bool TryReadInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
