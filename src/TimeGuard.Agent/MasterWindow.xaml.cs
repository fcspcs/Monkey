using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TimeGuard.Core;

namespace TimeGuard.Agent;

/// <summary>
/// Master-Steuerung. Das Fenster selbst entscheidet nichts - es schickt das
/// Passwort zusammen mit der Anfrage an den Dienst und zeigt dessen Antwort.
/// </summary>
public partial class MasterWindow : Window
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0xDF, 0xF3, 0xE2));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFB, 0xE3, 0xE1));

    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;

    public MasterWindow()
    {
        InitializeComponent();

        // Nie höher als der verfügbare Bildschirm. Passt der Inhalt nicht, greift
        // der Scrollbereich - so wird unten nichts abgeschnitten.
        var workHeight = SystemParameters.WorkArea.Height;
        MaxHeight = workHeight;
        if (Height > workHeight) Height = workHeight;

        Loaded += async (_, _) => await RefreshAsync();
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
            BalanceText.Text = "Guthaben: unbekannt";
            StateText.Text = "Der Dienst antwortet nicht. Läuft TimeGuardSrv?";
            return;
        }

        BalanceText.Text = $"Guthaben: {FormatSpan(status.BalanceSeconds)}";

        StateText.Text = status switch
        {
            { Paused: true, PauseUntil: { } until } => $"Kontrolle pausiert bis {until:HH:mm} Uhr.",
            { Paused: true } => "Kontrolle pausiert.",
            { Counting: true } => $"Die Uhr läuft. {FormatSpan(status.SessionElapsedSeconds)} seit der Anmeldung.",
            _ => "Die Uhr steht gerade.",
        };

        if (status.ClockTamperEvents > 0)
            StateText.Text += $"  ({status.ClockTamperEvents} Zeitsprung/-sprünge erkannt)";

        if (status.Config is { } config)
        {
            GrantBudgetText.Text =
                $"Nachlegen: pro Vorgang höchstens {FormatMinutes(config.MaxManualGrantMinutes)} " +
                "(beim Installieren festgelegt). Abziehen ist unbegrenzt.";

            DailyMinutes.Text = Text(config.DailyGrantMinutes);
            CapMinutes.Text = Text(config.CapMinutes);
            WarnMinutes.Text = Text(config.WarnMinutes);
            GraceSeconds.Text = Text(config.GraceSeconds);
            LoginGraceSeconds.Text = Text(config.LoginGraceSeconds);
            PauseOnLock.IsChecked = config.PauseOnLock;
            PauseOnScreensaver.IsChecked = config.PauseOnScreensaver;
        }

        if (!status.PasswordConfigured)
            Show(false, "Es ist kein Master-Passwort hinterlegt. Bitte 'TimeGuardService.exe init' " +
                        "als Administrator ausführen.");
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (!TryReadInt(PauseMinutes.Text, out var minutes) || minutes <= 0)
        {
            Show(false, "Bitte eine Dauer in ganzen Minuten angeben.");
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
            Show(false, "Bitte in allen Zahlenfeldern ganze Zahlen eintragen.");
            return;
        }

        if (cap < daily)
        {
            Show(false, "Das Höchstguthaben darf nicht kleiner sein als das Tagesbudget.");
            return;
        }

        if (warn <= 0)
        {
            Show(false, "Die Warnung braucht eine Restminutenzahl größer als 0.");
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
            Show(false, "Bitte zuerst oben das Master-Passwort eingeben.");
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
            Show(false, "Die beiden neuen Passwörter stimmen nicht überein.");
            return;
        }

        if (NewPassword.Password.Length < 4)
        {
            Show(false, "Das neue Passwort muss mindestens 4 Zeichen haben.");
            return;
        }

        var ok = await SendAsync(new Request
        {
            Type = RequestType.ChangePassword,
            Password = MasterPassword.Password,
            NewPassword = NewPassword.Password,
        });

        if (ok)
        {
            NewPassword.Clear();
            NewPasswordRepeat.Clear();
            MasterPassword.Clear();
            ChangePasswordGroup.Visibility = Visibility.Collapsed;
            RevealChangePasswordButton.IsEnabled = true;
        }
    }

    private async Task<bool> SendAsync(Request request)
    {
        request.SessionId = _sessionId;

        SetBusy(true);
        var response = await PipeClient.SendAsync(request);
        SetBusy(false);

        if (response is null)
        {
            Show(false, "Der Dienst antwortet nicht. Läuft TimeGuardSrv?");
            return false;
        }

        Show(response.Ok, response.Message ?? (response.Ok ? "Erledigt." : "Abgelehnt."));
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
