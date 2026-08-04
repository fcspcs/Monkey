using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Monkey.Core;

namespace Monkey.Agent;

/// <summary>
/// Master-Steuerung. Das Fenster selbst entscheidet nichts - es schickt das
/// Passwort zusammen mit der Anfrage an den Dienst und zeigt dessen Antwort.
/// </summary>
public partial class MasterWindow : Window
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0xDF, 0xF3, 0xE2));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFB, 0xE3, 0xE1));

    /// <summary>Nach dieser Zeit ohne Tippen wird ein eingegebenes Passwort verworfen.</summary>
    private static readonly TimeSpan PasswordLifetime = TimeSpan.FromSeconds(120);

    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private readonly DispatcherTimer _passwordTimer;

    public MasterWindow()
    {
        InitializeComponent();

        // Nie höher als der verfügbare Bildschirm. Passt der Inhalt nicht, greift
        // der Scrollbereich - so wird unten nichts abgeschnitten.
        var workHeight = SystemParameters.WorkArea.Height;
        MaxHeight = workHeight;
        if (Height > workHeight) Height = workHeight;

        // Das Master-Passwort soll nicht offen im Fenster stehen bleiben: Es wird
        // nach jeder Aktion geleert und zusätzlich, wenn es eine Weile unbenutzt
        // herumliegt.
        _passwordTimer = new DispatcherTimer { Interval = PasswordLifetime };
        _passwordTimer.Tick += (_, _) =>
        {
            _passwordTimer.Stop();
            if (MasterPassword.Password.Length == 0) return;
            ClearMasterPassword();
            Show(false, "The master password was cleared for safety. Please type it again.");
        };

        MasterPassword.PasswordChanged += (_, _) =>
        {
            _passwordTimer.Stop();
            if (MasterPassword.Password.Length > 0) _passwordTimer.Start();
        };

        Closed += (_, _) => _passwordTimer.Stop();
        SizeChanged += (_, _) => UpdateGimmick();
        Loaded += async (_, _) => { UpdateGimmick(); await RefreshAsync(); };
    }

    /// <summary>Seitenverhaeltnis der Evolutionsbilder (158 x 819).</summary>
    private const double GimmickAspect = 158.0 / 819.0;

    private int _gimmickStage;

    /// <summary>
    /// Haelt die Seitenspalte auf genau der Breite, die zur Fensterhoehe passt -
    /// so fuellt das Bild die Spalte randlos aus, ohne verzerrt oder beschnitten
    /// zu werden. Bei schmalem Fenster weicht es den Bedienelementen.
    /// </summary>
    private void UpdateGimmick()
    {
        const double needed = 560;
        var show = ActualWidth >= needed;

        Gimmick.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            GimmickColumn.Width = new GridLength(0);
            return;
        }

        var height = RootGrid.ActualHeight;
        if (height <= 0) return;

        Gimmick.Width = Math.Round(height * GimmickAspect);
        GimmickColumn.Width = GridLength.Auto;
    }

    /// <summary>
    /// Wechselt das Bild, wenn eine andere Evolutionsstufe erreicht ist. Der Wechsel
    /// passiert nur bei echter Aenderung, damit die Anzeige nicht bei jedem
    /// Statusabruf neu laedt.
    /// </summary>
    private void UpdateEvolution(int stage)
    {
        stage = Math.Clamp(stage, 1, 5);
        if (stage == _gimmickStage) return;

        _gimmickStage = stage;
        Gimmick.Source = new BitmapImage(
            new Uri($"pack://application:,,,/Assets/Evolution/stage{stage}.png", UriKind.Absolute));
    }

    /// <summary>
    /// Leert das Passwortfeld und klappt den Bereich zum Passwortwechsel wieder
    /// zu - der setzt ja voraus, dass das Master-Passwort eingegeben ist.
    /// </summary>
    private void ClearMasterPassword()
    {
        _passwordTimer.Stop();
        MasterPassword.Clear();

        NewPassword.Clear();
        NewPasswordRepeat.Clear();
        ChangePasswordGroup.Visibility = Visibility.Collapsed;
        RevealChangePasswordButton.IsEnabled = true;
    }

    private async Task RefreshAsync()
    {
        var response = await PipeClient.SendAsync(new Request
        {
            Type = RequestType.Status,
            SessionId = _sessionId,
        });

        var status = response?.Status;

        if (status is null)
        {
            BalanceText.Text = "Balance: unknown";
            StateText.Text = "The service is not responding. Is MonkeySrv running?";
            return;
        }

        UpdateEvolution(status.EvolutionStage);

        BalanceText.Text = $"Balance: {FormatSpan(status.BalanceSeconds)}";

        StateText.Text = status switch
        {
            { Paused: true, PauseUntil: { } until } => $"Paused until {until:HH:mm}.",
            { Paused: true } => "Paused.",
            { Counting: true } => $"The clock is running. {FormatSpan(status.SessionElapsedSeconds)} since sign-in.",
            _ => "The clock is paused right now.",
        };

        if (status.ClockTamperEvents > 0)
            StateText.Text += $"  ({status.ClockTamperEvents} clock jump(s) detected)";

        if (status.Config is { } config)
        {
            GrantBudgetText.Text =
                $"Top-ups: at most {FormatMinutes(config.MaxManualGrantMinutes)} per go " +
                "(fixed at install time). Taking time away is unlimited.";

            DailyMinutes.Text = Text(config.DailyGrantMinutes);
            CapMinutes.Text = Text(config.CapMinutes);
            WarnMinutes.Text = Text(config.WarnMinutes);
            GraceSeconds.Text = Text(config.GraceSeconds);
            LoginGraceSeconds.Text = Text(config.LoginGraceSeconds);
            PauseOnLock.IsChecked = config.PauseOnLock;
            PauseOnScreensaver.IsChecked = config.PauseOnScreensaver;
        }

        if (!status.PasswordConfigured)
            Show(false, "No master password is stored. Please reinstall with MonkeySetup.exe.");
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (!TryReadInt(PauseMinutes.Text, out var minutes) || minutes <= 0)
        {
            Show(false, "Please give the duration in whole minutes.");
            return;
        }

        await SendAsync(new Request
        {
            Type = RequestType.Pause,
            Password = MasterPassword.Password,
            Minutes = minutes,
        });
    }

    private async void OnResume(object sender, RoutedEventArgs e) =>
        await SendAsync(new Request { Type = RequestType.Resume, Password = MasterPassword.Password });

    private async void OnPlus30(object sender, RoutedEventArgs e) => await AddMinutesAsync(30);

    private async void OnMinus30(object sender, RoutedEventArgs e) => await AddMinutesAsync(-30);

    private async Task AddMinutesAsync(int minutes) =>
        await SendAsync(new Request
        {
            Type = RequestType.AddTime,
            Password = MasterPassword.Password,
            Minutes = minutes,
        });

    private async void OnSaveConfig(object sender, RoutedEventArgs e)
    {
        if (!TryReadInt(DailyMinutes.Text, out var daily) ||
            !TryReadInt(CapMinutes.Text, out var cap) ||
            !TryReadInt(WarnMinutes.Text, out var warn) ||
            !TryReadInt(GraceSeconds.Text, out var grace) ||
            !TryReadInt(LoginGraceSeconds.Text, out var loginGrace))
        {
            Show(false, "Please enter whole numbers in all number fields.");
            return;
        }

        if (cap < daily)
        {
            Show(false, "The balance cap can't be smaller than the daily allowance.");
            return;
        }

        if (warn <= 0)
        {
            Show(false, "The warning needs a number of minutes greater than 0.");
            return;
        }

        await SendAsync(new Request
        {
            Type = RequestType.SetConfig,
            Password = MasterPassword.Password,
            Config = new GuardConfig
            {
                DailyGrantMinutes = daily,
                CapMinutes = cap,
                WarnMinutes = warn,
                GraceSeconds = grace,
                LoginGraceSeconds = loginGrace,
                PauseOnLock = PauseOnLock.IsChecked == true,
                PauseOnScreensaver = PauseOnScreensaver.IsChecked == true,
            },
        });
    }

    /// <summary>
    /// Blendet den Bereich zum Passwortwechsel ein - aber nur, wenn oben das
    /// Master-Passwort eingegeben ist. Ob es stimmt, prüft der Dienst beim
    /// eigentlichen Wechsel.
    /// </summary>
    private void OnRevealChangePassword(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(MasterPassword.Password))
        {
            Show(false, "Enter the master password above first.");
            return;
        }

        ChangePasswordGroup.Visibility = Visibility.Visible;
        RevealChangePasswordButton.IsEnabled = false;
        NewPassword.Focus();
    }

    private async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        if (NewPassword.Password != NewPasswordRepeat.Password)
        {
            Show(false, "The two new passwords don't match.");
            return;
        }

        if (NewPassword.Password.Length < 4)
        {
            Show(false, "The new password needs at least 4 characters.");
            return;
        }

        // SendAsync leert anschliessend alle Passwortfelder und klappt den Bereich
        // wieder zu - egal ob der Wechsel geklappt hat oder nicht.
        await SendAsync(new Request
        {
            Type = RequestType.ChangePassword,
            Password = MasterPassword.Password,
            NewPassword = NewPassword.Password,
        });
    }

    private async Task<bool> SendAsync(Request request)
    {
        request.SessionId = _sessionId;

        SetBusy(true);
        var response = await PipeClient.SendAsync(request);
        SetBusy(false);

        if (response is null)
        {
            Show(false, "The service is not responding. Is MonkeySrv running?");
            return false;
        }

        Show(response.Ok, response.Message ?? (response.Ok ? "Done." : "Rejected."));

        // Nach jeder Aktion - auch nach einer abgelehnten - verschwindet das
        // Passwort wieder aus dem Fenster.
        ClearMasterPassword();

        await RefreshAsync();
        return response.Ok;
    }

    private void SetBusy(bool busy)
    {
        PauseButton.IsEnabled = !busy;
        ResumeButton.IsEnabled = !busy;
        Plus30Button.IsEnabled = !busy;
        Minus30Button.IsEnabled = !busy;
        SaveConfigButton.IsEnabled = !busy;
        ChangePasswordButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void Show(bool ok, string message)
    {
        MessageBorder.Background = ok ? Good : Bad;
        MessageText.Text = message;
        MessageBorder.Visibility = Visibility.Visible;
    }

    private static string FormatSpan(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : $"{span.Minutes} min";
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatMinutes(int minutes) =>
        minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h"
        : minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min"
        : $"{minutes} min";

    private static bool TryReadInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
