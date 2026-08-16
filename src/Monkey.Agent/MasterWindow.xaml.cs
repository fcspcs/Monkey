using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Monkey.Core;
using Monkey.Ui;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Monkey.Agent;

/// <summary>
/// Master-Steuerung. Das Fenster selbst entscheidet nichts - es schickt das
/// Passwort zusammen mit der Anfrage an den Dienst und zeigt dessen Antwort.
/// </summary>
public partial class MasterWindow : ChromeWindow
{
    /// <summary>Nach dieser Zeit ohne Tippen wird ein eingegebenes Passwort verworfen.</summary>
    private static readonly TimeSpan PasswordLifetime = TimeSpan.FromSeconds(120);

    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private readonly DispatcherTimer _passwordTimer;

    /// <summary>Letzter Stand des Dienstes - die Statistik rechnet damit weiter.</summary>
    private StatusDto? _status;

    private List<DayStatDto> _history = [];
    private int _rangeDays = 30;

    /// <summary>Was die Statistikseite gerade zeigt.</summary>
    private enum Metric { Usage, Banked, Weekday }

    private Metric _metric = Metric.Usage;

    /// <summary>Steht die Anbindung, wurde der Einrichtungsteil ausdruecklich aufgerufen?</summary>
    private bool _telegramSetupPinned;

    public MasterWindow()
    {
        InitializeComponent();

        // Nie höher starten als der verfügbare Bildschirm. Passt der Inhalt nicht,
        // greift der Scrollbereich - so wird unten nichts abgeschnitten. Eine feste
        // MaxHeight wäre hier falsch: sie würde auch das Maximieren beschneiden,
        // das mit eigener Titelleiste etwas über den Arbeitsbereich hinausgeht.
        var workHeight = SystemParameters.WorkArea.Height;
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

        BuildDisplayChoices();
        LoadDisplaySettings();

        GimmickBox.SizeChanged += (_, _) => SizeGimmick();

        Closed += (_, _) => _passwordTimer.Stop();
        Loaded += async (_, _) =>
        {
            UpdateProtectionStatus();
            await RefreshAsync();
        };
    }

    private int _gimmickStage;

    /// <summary>
    /// Haelt das Bild genau so hoch, wie es bei der Breite der Spalte sein muss.
    /// Damit fuellt es waagerecht immer randlos und wird nur senkrecht
    /// beschnitten - und dort faengt es der Verlauf ab.
    /// </summary>
    private void SizeGimmick()
    {
        if (Gimmick.Source is not BitmapSource source || source.PixelWidth <= 0) return;

        var width = GimmickBox.ActualWidth;
        if (width <= 0) return;

        Gimmick.Height = width * source.PixelHeight / source.PixelWidth;
    }

    /// <summary>
    /// Zeigt die Seite, die in der Spalte gewaehlt wurde. Es ist immer genau
    /// eine sichtbar - der Sinn der Spalte ist, dass nicht alles gleichzeitig
    /// auf einem Blatt steht.
    /// </summary>
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string page }) return;

        // Beim Aufbau des Fensters meldet sich der vorgewaehlte Eintrag, bevor
        // es die Seiten ueberhaupt gibt.
        if (PageOverview is null) return;

        PageOverview.Visibility = Visible(page == "Overview");
        PageStatistics.Visibility = Visible(page == "Statistics");
        PageDisplay.Visibility = Visible(page == "Display");
        PageSettings.Visibility = Visible(page == "Settings");
        PageTelegram.Visibility = Visible(page == "Telegram");
        PageProtection.Visibility = Visible(page == "Protection");

        // Wo es nichts zu befugen gibt, steht auch kein Passwortfeld herum.
        AuthBar.Visibility = Visible(page is "Overview" or "Settings" or "Telegram");

        // Die Vorlieben koennen zwischendurch ueber das Tray-Menue geaendert
        // worden sein - beim Aufschlagen der Seite also frisch einlesen.
        if (page == "Display") LoadDisplaySettings();
    }

    private static Visibility Visible(bool show) =>
        show ? Visibility.Visible : Visibility.Collapsed;

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

        // Kleingeschrieben, weil die Ressourcennamen so abgelegt werden. Schlaegt
        // das Laden fehl, bleibt einfach das bisherige Bild stehen - eine fehlende
        // Zierde darf das Fenster nicht stoeren.
        try
        {
            Gimmick.Source = new BitmapImage(
                new Uri($"pack://application:,,,/assets/evolution/stage{stage}.png", UriKind.Absolute));
            SizeGimmick();
        }
        catch (Exception)
        {
            _gimmickStage = 0;
        }
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
            BalanceText.Text = "unknown";
            StateText.Text = "The service is not responding. Is MonkeySrv running?";
            return;
        }

        UpdateEvolution(status.EvolutionStage);

        // Die Ueberschrift der Karte sagt schon "Balance" - hier steht nur noch
        // die Zahl.
        BalanceText.Text = FormatSpan(status.BalanceSeconds);

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
            AutoUpdateBox.IsChecked = config.AutoUpdate;
        }

        VersionText.Text = status.ServiceVersion is { } version
            ? status.SignedUpdatesAvailable
                ? $"Installed version: {version}. Signed automatic updates are available."
                : $"Installed version: {version}. Signed automatic updates are not configured in this build."
            : string.Empty;

        if (status.TelegramEnabled)
        {
            if (string.IsNullOrWhiteSpace(CloudflareAccountIdBox.Text) &&
                status.TelegramCloudflareAccountId is { } accountId)
                CloudflareAccountIdBox.Text = accountId;

            var text = $"Connected — worker: {status.TelegramWorkerHost}.";
            if (status.TelegramWorkerVersion is { } workerVersion)
                text += $" Worker v{workerVersion}.";
            text += status.TelegramWorkerManaged
                ? " Managed by Monkey."
                : " Externally managed Worker.";
            text += status.TelegramLastSyncSecondsAgo is { } ago
                ? $" Last sync {FormatAgo(ago)} ago."
                : " No successful sync yet.";
            if (!string.IsNullOrEmpty(status.TelegramLastError))
                text += $" Last error: {status.TelegramLastError}";
            TelegramStatusText.Text = text;
        }
        else
        {
            TelegramStatusText.Text = "Not connected. Set it up below if you want to check the balance from your phone.";
        }

        ShowTelegramLive(status.TelegramEnabled);

        _status = status;
        await LoadHistoryAsync();

        if (!status.PasswordConfigured)
            Show(false, "No master password is stored. Please reinstall with MonkeySetup.exe.");
    }

    // -------------------------------------------------------------- Anzeige

    /// <summary>
    /// Sagt dem Agenten, dass eine Anzeigevorliebe sich geaendert hat. Das
    /// Fenster schreibt sie nur in die Registrierung; wie sie wirksam wird,
    /// weiss allein <see cref="App.ApplyDisplayPreferences"/>.
    /// </summary>
    public event EventHandler? DisplayPreferencesChanged;

    /// <summary>Waehrend des Einlesens nicht gleich wieder zurueckschreiben.</summary>
    private bool _loadingDisplay;

    /// <summary>
    /// Ecken und Farben stehen in <see cref="App"/>, damit Tray-Menue und diese
    /// Seite dieselbe Auswahl anbieten. Die Knoepfe entstehen deshalb hier und
    /// nicht in XAML.
    /// </summary>
    private void BuildDisplayChoices()
    {
        foreach (var (label, value) in App.OverlayCorners)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("SegmentButton"),
                GroupName = "OverlayCorner",
                Content = label,
                Tag = value,
            };
            button.Checked += OnCornerPicked;
            CornerChoices.Children.Add(button);
        }

        foreach (var (label, value) in App.OverlayColors)
        {
            var button = new RadioButton
            {
                GroupName = "OverlayColor",
                Tag = value,
                ToolTip = label,
            };

            // Eine feste Farbe zeigt sich als Farbfeld; die beiden Automatiken
            // haben keine eigene Farbe und brauchen ihren Namen.
            if (OverlayWindow.ParseColor(value) is { } color)
            {
                button.Style = (Style)FindResource("Swatch");
                button.Background = new SolidColorBrush(color);
                button.Margin = new Thickness(0, 0, 6, 6);
                ColorChoices.Children.Add(button);
            }
            else
            {
                button.Style = (Style)FindResource("SegmentButton");
                button.Content = label;
                AutoColorChoices.Children.Add(button);
            }

            button.Checked += OnColorPicked;
        }
    }

    private void LoadDisplaySettings()
    {
        _loadingDisplay = true;
        try
        {
            ShowOverlayBox.IsChecked = AgentSettings.OverlayVisible;
            ShowBackgroundBox.IsChecked = AgentSettings.OverlayBackground;
            ShowHoverIconBox.IsChecked = AgentSettings.HoverIcon;
            CountUpBox.IsChecked = AgentSettings.CountUp;

            var corner = AgentSettings.OverlayCorner;
            foreach (var button in CornerChoices.Children.OfType<RadioButton>())
                button.IsChecked = button.Tag is OverlayCorner value && value == corner;

            var colour = AgentSettings.OverlayColor;
            foreach (var button in AutoColorChoices.Children.OfType<RadioButton>()
                         .Concat(ColorChoices.Children.OfType<RadioButton>()))
                button.IsChecked =
                    string.Equals(button.Tag as string, colour, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _loadingDisplay = false;
        }
    }

    private void OnDisplayChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingDisplay) return;

        AgentSettings.OverlayVisible = ShowOverlayBox.IsChecked == true;
        AgentSettings.OverlayBackground = ShowBackgroundBox.IsChecked == true;
        AgentSettings.HoverIcon = ShowHoverIconBox.IsChecked == true;
        AgentSettings.CountUp = CountUpBox.IsChecked == true;

        DisplayPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCornerPicked(object sender, RoutedEventArgs e)
    {
        if (_loadingDisplay) return;
        if (sender is RadioButton { Tag: OverlayCorner corner }) AgentSettings.OverlayCorner = corner;
        DisplayPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColorPicked(object sender, RoutedEventArgs e)
    {
        if (_loadingDisplay) return;
        if (sender is RadioButton { Tag: string value }) AgentSettings.OverlayColor = value;
        DisplayPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ Statistik

    /// <summary>Eine Zeile der Zahlenansicht unter den Diagrammen.</summary>
    public sealed record StatRow(string Day, string Used, string Added, string Balance);

    /// <summary>
    /// Der Verlauf kommt ohne Passwort - er gibt nur Auskunft ueber die eigene
    /// Nutzung und raeumt keine Befugnis ein.
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        var response = await PipeClient.SendAsync(new Request
        {
            Type = RequestType.History,
            SessionId = _sessionId,
        });

        _history = response?.History ?? [];
        RenderStats();
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
            _rangeDays = days;

        // Beim Aufbau des Fensters meldet sich der vorgewaehlte Knopf, bevor es
        // die Diagramme ueberhaupt gibt.
        if (UsageChart is null) return;
        RenderStats();
    }

    private void OnMetricChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } &&
            Enum.TryParse<Metric>(tag, out var metric))
            _metric = metric;

        if (UsageChart is null) return;
        RenderStats();
    }

    private void OnToggleStatsTable(object sender, RoutedEventArgs e)
    {
        var show = StatsTableGroup.Visibility != Visibility.Visible;
        StatsTableGroup.Visibility = Visible(show);
        ToggleTableButton.Content = show ? "Hide the numbers" : "Show the numbers";
    }

    /// <summary>
    /// Baut aus dem Verlauf die Reihen fuer alle drei Ansichten und die
    /// Zahlenansicht - gezeigt wird davon immer nur die gewaehlte. Fehlende Tage
    /// werden aufgefuellt: eine Luecke im Balkenfeld hiesse sonst "kein Wert",
    /// obwohl "nichts verbraucht" gemeint ist.
    /// </summary>
    private void RenderStats()
    {
        if (UsageChart is null) return;

        var daily = _status?.Config?.DailyGrantMinutes ?? _status?.DailyGrantMinutes ?? 0;
        var cap = _status?.Config?.CapMinutes ?? _status?.CapMinutes ?? 0;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = today.AddDays(-(_rangeDays - 1));

        var byDate = new Dictionary<DateOnly, DayStatDto>();
        foreach (var entry in _history) byDate[entry.Date] = entry;

        var usage = new List<ChartPoint>();
        var balance = new List<ChartPoint>();
        var rows = new List<StatRow>();

        var weekdayTotal = new double[7];
        var weekdayCount = new int[7];

        double? carried = null;
        var usedSum = 0.0;
        var recorded = 0;
        var within = 0;

        for (var date = first; date <= today; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var day);

            var used = day?.UsedMinutes ?? 0;
            var label = ShortDate(date);

            usage.Add(new ChartPoint
            {
                Label = label,
                Value = used,
                Emphasised = date == today,
                Detail = $"{LongDate(date)} — {Minutes(used)} used",
            });

            if (day is not null)
            {
                recorded++;
                usedSum += used;
                if (daily > 0 && used <= daily) within++;

                // Montag zuerst - so liest sich die Woche wie ein Kalender.
                var index = ((int)date.DayOfWeek + 6) % 7;
                weekdayTotal[index] += used;
                weekdayCount[index]++;

                carried = day.BalanceEndMinutes;

                var net = day.AddedMinutes - day.RemovedMinutes;
                rows.Add(new StatRow(
                    LongDate(date),
                    Minutes(used),
                    Math.Abs(net) < 0.5 ? "—" : (net > 0 ? "+" : "−") + Minutes(Math.Abs(net)),
                    Minutes(day.BalanceEndMinutes)));
            }

            // Vor dem ersten aufgezeichneten Tag gibt es nichts fortzuschreiben;
            // danach traegt der letzte bekannte Stand ueber ausgeschaltete Tage.
            if (carried is { } value)
                balance.Add(new ChartPoint
                {
                    Label = label,
                    Value = value,
                    Emphasised = date == today,
                    Detail = $"{LongDate(date)} — {Minutes(value)} banked",
                });
        }

        // Bewusst invariant: das ganze Programm spricht Englisch, deutsche
        // Monats- und Tagesnamen mitten in englischen Saetzen saehen aus wie ein
        // Fehler.
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        var weekdays = new List<ChartPoint>();
        for (var i = 0; i < 7; i++)
        {
            var dayOfWeek = (int)((DayOfWeek)((i + 1) % 7));
            var average = weekdayCount[i] > 0 ? weekdayTotal[i] / weekdayCount[i] : 0;

            weekdays.Add(new ChartPoint
            {
                Label = names.AbbreviatedDayNames[dayOfWeek],
                Value = average,
                Detail = $"{names.DayNames[dayOfWeek]} — {Minutes(average)} on average",
            });
        }

        // Neueste Zeile oben - dort steht, was gerade interessiert.
        rows.Reverse();
        StatsTable.ItemsSource = rows;

        var allowance = daily > 0 ? daily : double.NaN;
        var allowanceLabel = daily > 0 ? $"{Minutes(daily)} a day" : string.Empty;

        switch (_metric)
        {
            case Metric.Banked:
                ShowBanked(balance, cap);
                break;
            case Metric.Weekday:
                ShowWeekday(weekdays, weekdayCount, daily, allowance, allowanceLabel);
                break;
            default:
                ShowUsage(usage, usedSum, recorded, within, daily, allowance, allowanceLabel);
                break;
        }
    }

    private const string NothingYet =
        "Nothing recorded yet. Monkey keeps a daily total from the day it is installed.";

    /// <summary>
    /// Bildschirmzeit je Tag. Die Ueberschrift traegt die Summe des Zeitraums,
    /// die Zeile darunter das, wofuer vorher vier Kacheln standen.
    /// </summary>
    private void ShowUsage(List<ChartPoint> usage, double usedSum, int recorded, int within,
                           double daily, double allowance, string allowanceLabel)
    {
        UsageChart.ValueFormat = AxisMinutes;
        UsageChart.Maximum = TimeCeiling(Peak(usage, daily));
        UsageChart.ReferenceValue = allowance;
        UsageChart.ReferenceLabel = allowanceLabel;
        UsageChart.EmptyText = "Nothing recorded yet.";
        UsageChart.Points = usage;
        ShowChart(UsageChart);

        if (recorded == 0)
        {
            StatHeadline.Text = "–";
            StatContext.Text = NothingYet;
            return;
        }

        StatHeadline.Text = Minutes(usedSum);
        StatContext.Text = daily > 0
            ? $"of screen time over {Range()} — {Minutes(usedSum / recorded)} a day on average, " +
              $"and {within} of {recorded} recorded days stayed within the {Minutes(daily)} allowance."
            : $"of screen time over {Range()} — {Minutes(usedSum / recorded)} a day on average.";
    }

    /// <summary>
    /// Das Ersparte im Verlauf. Bewusst ohne Deckel-Linie: bei einem hohen Deckel
    /// drueckt sie die Kurve flach, und darum geht es in diesem Bild nicht.
    /// </summary>
    private void ShowBanked(List<ChartPoint> balance, double cap)
    {
        BalanceChart.ValueFormat = AxisMinutes;
        BalanceChart.Maximum = TimeCeiling(Peak(balance, 0));
        BalanceChart.EmptyText = "Nothing recorded yet.";
        BalanceChart.Points = balance;
        ShowChart(BalanceChart);

        if (balance.Count == 0 || _status is null)
        {
            StatHeadline.Text = "–";
            StatContext.Text = NothingYet;
            return;
        }

        StatHeadline.Text = Minutes(_status.BalanceSeconds / 60.0);

        var change = balance[^1].Value - balance[0].Value;
        var trend = change >= 1 ? $"up {Minutes(change)}"
            : change <= -1 ? $"down {Minutes(-change)}"
            : "unchanged";

        StatContext.Text = $"in the bank right now — {trend} over {Range()}" +
                           (cap > 0 ? $", and it stops piling up at {Minutes(cap)}." : ".");
    }

    /// <summary>Der Wochenschnitt - welcher Tag der Woche kostet wirklich.</summary>
    private void ShowWeekday(List<ChartPoint> weekdays, int[] counts,
                             double daily, double allowance, string allowanceLabel)
    {
        WeekdayChart.ValueFormat = AxisMinutes;
        WeekdayChart.Maximum = TimeCeiling(Peak(weekdays, daily));
        WeekdayChart.ReferenceValue = allowance;
        WeekdayChart.ReferenceLabel = allowanceLabel;
        WeekdayChart.EmptyText = "Nothing recorded yet.";
        WeekdayChart.Points = weekdays;
        ShowChart(WeekdayChart);

        // Tage ohne Aufzeichnung stehen auf null und waeren sonst immer die
        // ruhigsten - gemeint sind aber nur die tatsaechlich gemessenen.
        var heaviest = -1;
        var lightest = -1;
        for (var i = 0; i < 7; i++)
        {
            if (counts[i] == 0) continue;
            if (heaviest < 0 || weekdays[i].Value > weekdays[heaviest].Value) heaviest = i;
            if (lightest < 0 || weekdays[i].Value < weekdays[lightest].Value) lightest = i;
        }

        if (heaviest < 0)
        {
            StatHeadline.Text = "–";
            StatContext.Text = NothingYet;
            return;
        }

        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        string Name(int index) => names.DayNames[(index + 1) % 7];

        StatHeadline.Text = Name(heaviest);
        StatContext.Text = heaviest == lightest
            ? $"is the only weekday recorded so far — {Minutes(weekdays[heaviest].Value)} on it."
            : $"is your heaviest weekday at {Minutes(weekdays[heaviest].Value)} on average — " +
              $"{Name(lightest)} is the lightest at {Minutes(weekdays[lightest].Value)}.";
    }

    /// <summary>Genau ein Diagramm ist zu sehen; die anderen warten daneben.</summary>
    private void ShowChart(UIElement chart)
    {
        UsageChart.Visibility = Visible(ReferenceEquals(chart, UsageChart));
        BalanceChart.Visibility = Visible(ReferenceEquals(chart, BalanceChart));
        WeekdayChart.Visibility = Visible(ReferenceEquals(chart, WeekdayChart));
    }

    private string Range() => $"the last {_rangeDays} days";

    private static double Peak(List<ChartPoint> points, double atLeast)
    {
        var peak = atLeast;
        foreach (var point in points) peak = Math.Max(peak, point.Value);
        return peak;
    }

    /// <summary>
    /// Obergrenze der Werteachse. Zeit rundet sich nicht auf Zehnerpotenzen,
    /// sondern auf viertel und halbe Stunden - so, wie man eine Uhr abliest.
    /// </summary>
    private static readonly double[] TimeSteps =
        [15, 30, 45, 60, 90, 120, 150, 180, 240, 300, 360, 480, 600, 720, 900, 1200, 1440];

    private static double TimeCeiling(double minutes)
    {
        foreach (var step in TimeSteps)
            if (minutes <= step) return step;

        return Math.Ceiling(minutes / 60) * 60;
    }

    /// <summary>Kompakt, fuer Achsen und angeschriebene Werte.</summary>
    private static string AxisMinutes(double minutes)
    {
        var rounded = (int)Math.Round(Math.Max(0, minutes));
        if (rounded == 0) return "0";
        if (rounded < 60) return $"{rounded} min";
        return rounded % 60 == 0 ? $"{rounded / 60} h" : $"{rounded / 60} h {rounded % 60:00}";
    }

    /// <summary>Ausgeschrieben, fuer Kacheln, Kurzhinweise und die Zahlenansicht.</summary>
    private static string Minutes(double minutes)
    {
        var rounded = (int)Math.Round(Math.Max(0, minutes));
        if (rounded < 60) return $"{rounded} min";
        return rounded % 60 == 0 ? $"{rounded / 60} h" : $"{rounded / 60} h {rounded % 60} min";
    }

    private string ShortDate(DateOnly date) =>
        _rangeDays <= 7
            ? date.ToString("ddd", CultureInfo.InvariantCulture)
            : date.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string LongDate(DateOnly date) =>
        date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>Uebernimmt eine der Vorgabelaengen in das Minutenfeld.</summary>
    private void OnPausePreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string minutes })
            PauseMinutes.Text = minutes;
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
                AutoUpdate = AutoUpdateBox.IsChecked == true,
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

        if (NewPassword.Password.Length < PasswordHash.MinimumLength)
        {
            Show(false, $"The new password needs at least {PasswordHash.MinimumLength} characters.");
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

    // ------------------------------------------------------------- Telegram

    /// <summary>
    /// Einrichten und Bedienen sind zwei Zustaende, nicht zwei Haelften einer
    /// Seite: solange nichts steht, gibt es nichts zu bedienen - und sobald es
    /// steht, will niemand mehr die Formulare sehen.
    /// </summary>
    private void ShowTelegramLive(bool live)
    {
        TelegramLivePanel.Visibility = Visible(live);

        if (!live) _telegramSetupPinned = false;
        TelegramSetupPanel.Visibility = Visible(!live || _telegramSetupPinned);

        TelegramDot.Fill = (Brush)FindResource(live ? "SuccessTextBrush" : "TextFaintBrush");
    }

    private void OnShowTelegramSetup(object sender, RoutedEventArgs e)
    {
        _telegramSetupPinned = true;
        TelegramSetupPanel.Visibility = Visibility.Visible;
    }

    private void OnOpenBotFather(object sender, RoutedEventArgs e) =>
        OpenExternal("https://t.me/BotFather");

    private void OnOpenCloudflareToken(object sender, RoutedEventArgs e) =>
        OpenExternal(
            "https://dash.cloudflare.com/profile/api-tokens?" +
            "permissionGroupKeys=%5B%7B%22key%22%3A%22workers_scripts%22%2C%22type%22%3A%22edit%22%7D%2C" +
            "%7B%22key%22%3A%22workers_kv_storage%22%2C%22type%22%3A%22edit%22%7D%5D" +
            "&accountId=%2A&zoneId=all&name=Monkey%20Telegram%20Setup");

    private void OnOpenCloudflareDashboard(object sender, RoutedEventArgs e) =>
        OpenExternal("https://dash.cloudflare.com/?to=/:account/workers-and-pages");

    private async void OnTelegramDeploy(object sender, RoutedEventArgs e)
    {
        if (MonkeyTokenBox.Password.Length == 0 || FriendTokenBox.Password.Length == 0)
        {
            Show(false, "Create both bots with @BotFather and paste both tokens first.");
            return;
        }

        var accountId = CloudflareAccountIdBox.Text?.Trim();
        if (string.IsNullOrEmpty(accountId))
        {
            Show(false, "Copy the Account ID from the Cloudflare dashboard.");
            return;
        }

        if (CloudflareApiTokenBox.Password.Length == 0)
        {
            Show(false, "Create and paste an 'Edit Cloudflare Workers' API token.");
            return;
        }

        await SendAsync(new Request
        {
            Type = RequestType.TelegramDeploy,
            Password = MasterPassword.Password,
            CloudflareAccountId = accountId,
            CloudflareApiToken = CloudflareApiTokenBox.Password.Trim(),
            MonkeyToken = MonkeyTokenBox.Password.Trim(),
            FriendToken = FriendTokenBox.Password.Trim(),
        });

        // Der Cloudflare-Schluessel ist eine einmalige Berechtigung und bleibt
        // auch nach einem Fehler nicht im Fenster stehen.
        CloudflareApiTokenBox.Clear();
        // Auch Bot-Tokens bleiben nach keinem Versuch im Fenster liegen.
        MonkeyTokenBox.Clear();
        FriendTokenBox.Clear();
    }

    /// <summary>
    /// Das Sync-Secret entsteht hier im Fenster, damit es vor dem Verbinden nach
    /// Cloudflare kopiert werden kann. Base64url, 256 Bit.
    /// </summary>
    private void OnGenerateSyncSecret(object sender, RoutedEventArgs e)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        SyncSecretBox.Text = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Show(true, "Sync secret generated. Store this value in your Cloudflare worker as the SYNC_SECRET secret, then connect.");
    }

    private async void OnTelegramConnect(object sender, RoutedEventArgs e)
    {
        var url = WorkerUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Show(false, "Please enter the worker address as an https:// URL.");
            return;
        }

        if (string.IsNullOrWhiteSpace(SyncSecretBox.Text))
        {
            Show(false, "Please generate a sync secret first and store it in Cloudflare as SYNC_SECRET.");
            return;
        }

        await SendAsync(new Request
        {
            Type = RequestType.TelegramSetup,
            Password = MasterPassword.Password,
            WorkerUrl = url,
            SyncSecret = SyncSecretBox.Text.Trim(),
            MonkeyToken = MonkeyTokenBox.Password.Trim(),
            FriendToken = FriendTokenBox.Password.Trim(),
        });

        // Die Tokens haben ihr Ziel erreicht (oder der Versuch ist gescheitert) -
        // in beiden Faellen muessen sie nicht im Fenster stehen bleiben.
        MonkeyTokenBox.Clear();
        FriendTokenBox.Clear();
    }

    private async void OnWorkerCheck(object sender, RoutedEventArgs e) =>
        await SendAsync(new Request
        {
            Type = RequestType.TelegramWorkerCheck,
            Password = MasterPassword.Password,
        });

    private async void OnWorkerUpdate(object sender, RoutedEventArgs e)
    {
        if (!TryReadCloudflareCredentials(out var accountId, out var apiToken)) return;

        await SendAsync(new Request
        {
            Type = RequestType.TelegramWorkerUpdate,
            Password = MasterPassword.Password,
            CloudflareAccountId = accountId,
            CloudflareApiToken = apiToken,
        });

        CloudflareApiTokenBox.Clear();
    }

    private async void OnWorkerRemove(object sender, RoutedEventArgs e)
    {
        if (!TryReadCloudflareCredentials(out var accountId, out var apiToken)) return;
        if (MessageBox.Show(
                "This permanently deletes Monkey's managed Worker, its secret bindings, all pairings, state and queued commands from Cloudflare. Continue?",
                "Remove Cloudflare Worker",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            CloudflareApiTokenBox.Clear();
            return;
        }

        await SendAsync(new Request
        {
            Type = RequestType.TelegramWorkerRemove,
            Password = MasterPassword.Password,
            CloudflareAccountId = accountId,
            CloudflareApiToken = apiToken,
        });

        CloudflareApiTokenBox.Clear();
    }

    private bool TryReadCloudflareCredentials(out string accountId, out string apiToken)
    {
        accountId = CloudflareAccountIdBox.Text?.Trim() ?? string.Empty;
        apiToken = CloudflareApiTokenBox.Password.Trim();
        if (accountId.Length == 0)
        {
            Show(false, "Copy the Account ID from the Cloudflare dashboard into step 2.");
            return false;
        }

        if (apiToken.Length == 0)
        {
            Show(false, "Create and paste a fresh 'Edit Cloudflare Workers' API token into step 2.");
            return false;
        }

        return true;
    }

    private async void OnPairMonkey(object sender, RoutedEventArgs e) =>
        await SendAsync(new Request
        {
            Type = RequestType.TelegramPair,
            Password = MasterPassword.Password,
            PairRole = "monkey",
        });

    private async void OnPairFriend(object sender, RoutedEventArgs e) =>
        await SendAsync(new Request
        {
            Type = RequestType.TelegramPair,
            Password = MasterPassword.Password,
            PairRole = "friend",
        });

    private async void OnTelegramOff(object sender, RoutedEventArgs e) =>
        await SendAsync(new Request
        {
            Type = RequestType.TelegramOff,
            Password = MasterPassword.Password,
        });

    private static string FormatAgo(double seconds) =>
        seconds < 90 ? $"{(int)seconds} s" : $"{(int)(seconds / 60)} min";

    // ------------------------------------------------------------- Protection

    private void UpdateProtectionStatus()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var accountIsAdmin = identity.Groups?.Any(group => group.Equals(administrators)) == true;

        AccountProtectionText.Text = accountIsAdmin
            ? "This Windows account belongs to the local Administrators group. Monkey can add friction, but you can deliberately override it with SYSTEM tools."
            : "This Windows account is not a local administrator. Keep the separate administrator credentials away from the person whose time Monkey limits for the strongest boundary.";
    }

    private void OnOpenAccountSettings(object sender, RoutedEventArgs e) =>
        OpenExternal("ms-settings:otherusers");

    private void OnOpenBitLocker(object sender, RoutedEventArgs e) =>
        OpenExternal("control.exe", "/name Microsoft.BitLockerDriveEncryption");

    private void OnOpenSystemInformation(object sender, RoutedEventArgs e) =>
        OpenExternal("msinfo32.exe");

    private void OpenExternal(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Show(false, $"Could not open it: {ex.Message}");
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
        DeployTelegramButton.IsEnabled = !busy;
        ConnectTelegramButton.IsEnabled = !busy;
        CheckWorkerButton.IsEnabled = !busy;
        UpdateWorkerButton.IsEnabled = !busy;
        PairMonkeyButton.IsEnabled = !busy;
        PairFriendButton.IsEnabled = !busy;
        TelegramOffButton.IsEnabled = !busy;
        RemoveWorkerButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>
    /// Die Farben kommen aus Theme.xaml, damit gruen und rot hier nicht ein
    /// zweites Mal festgelegt werden.
    /// </summary>
    private void Show(bool ok, string message)
    {
        MessageBorder.Background = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        MessageText.Foreground = (Brush)FindResource(ok ? "SuccessTextBrush" : "DangerTextBrush");
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
