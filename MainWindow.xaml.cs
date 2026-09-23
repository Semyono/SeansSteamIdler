using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Threading;
using QRCoder;
using SteamKit2;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SeansSteamIdler.Models;
using SeansSteamIdler.Services;

namespace SeansSteamIdler;

public partial class MainWindow : FluentWindow
{
    private readonly SteamIdleClient _idleClient = new();
    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly ICollectionView _gamesView;
    private readonly DispatcherTimer _autoStopTimer;
    private readonly HashSet<uint> _favoriteAppIds;
    private readonly System.Collections.ObjectModel.ObservableCollection<IdlePreset> _presets = new();
    private bool _awaitingTwoFactor;
    private bool _themeInitialized;
    private bool _favoritesOnly;
    private bool _updatingNavigation;
    private TimeSpan _timeRemaining;
    private string? _selectedPresetName;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        _gamesView = CollectionViewSource.GetDefaultView(_games);
        _gamesView.Filter = FilterGame;
        _gamesView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.IsFavorite), ListSortDirection.Descending));
        _gamesView.SortDescriptions.Add(new SortDescription(nameof(GameEntry.Name), ListSortDirection.Ascending));
        GamesList.ItemsSource = _gamesView;
        _favoriteAppIds = SteamSessionStore.LoadFavoriteAppIds();
        foreach (var preset in SteamSessionStore.LoadPresets())
            _presets.Add(preset);
        PresetSelector.ItemsSource = _presets;
        PresetSelector.DisplayMemberPath = nameof(IdlePreset.PresetName);
        _autoStopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoStopTimer.Tick += AutoStopTimer_Tick;
        AddPressFeedback(LoginButton);
        AddPressFeedback(QrStartButton);
        AddPressFeedback(StartIdleButton);
        AddPressFeedback(StopIdleButton);
        AddPressFeedback(DetectGamesButton);
        AddPressFeedback(LibraryNavButton);
        AddPressFeedback(SettingsNavButton);

        _idleClient.Log += AppendLog;
        _idleClient.AccountLibrary.Log += AppendLog;
        _idleClient.AccountLibrary.LoadingStarted += () => Dispatcher.Invoke(BeginLibraryLoading);
        _idleClient.AccountLibrary.Loaded += games => Dispatcher.Invoke(() =>
        {
            _games.Clear();
            foreach (var game in games)
            {
                game.IsFavorite = _favoriteAppIds.Contains(game.AppId);
                _games.Add(game);
            }
            _gamesView.Refresh();
            ApplySelectedPreset();
            CompleteLibraryLoading(games.Count);
        });
        _idleClient.PersonaNameChanged += name => Dispatcher.Invoke(() =>
            GreetingText.Text = CreateGreeting(name));
        _idleClient.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            StatusText.Text = status;
            UpdateIdlePulse(status.StartsWith("Idling", StringComparison.OrdinalIgnoreCase));
        });
        _idleClient.LoggedOn += () => Dispatcher.Invoke(() =>
        {
            AppendLog("Ready to idle. Loading your owned games...");
            StatusText.Text = "Connected to Steam";
            ShowLoginSuccess();
            GuardCodeBox.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = false;
            QrStartButton.IsEnabled = false;
            UsernameBox.IsEnabled = false;
            PasswordBox.IsEnabled = false;
            AnimateDashboardIn();
        });
        _idleClient.SteamGuardRequired += isTwoFactor => Dispatcher.Invoke(() =>
        {
            _awaitingTwoFactor = isTwoFactor;
            GuardCodeBox.Visibility = Visibility.Visible;
            AppendLog(isTwoFactor
                ? "Enter the code from your Steam Mobile app and click Log In again."
                : "Enter the code emailed to you and click Log In again.");
        });
        _idleClient.LoginFailed += reason => Dispatcher.Invoke(() =>
        {
            ResetLoginLoading();
            StatusText.Text = "Login failed";
            AppendLog($"Login failed: {reason}");
        });
        _idleClient.Disconnected += () => Dispatcher.Invoke(() =>
        {
            LoginButton.IsEnabled = true;
            QrStartButton.IsEnabled = true;
            UsernameBox.IsEnabled = true;
            PasswordBox.IsEnabled = true;
            ResetLoginLoading();
            ResetDashboard();
        });
        _idleClient.QrChallengeUrlChanged += url => Dispatcher.Invoke(() => ShowQrCode(url));

        DetectGamesButton_Click(this, new RoutedEventArgs());
        SelectSavedPersonaStatus();
        BeginLoginLoading();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SelectSavedTheme();
            SelectNavigationTab(0);
            MainTabs.UpdateLayout();
            _idleClient.ConnectWithSavedSession();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string CreateGreeting(string personaName)
    {
        var timeOfDay = DateTime.Now.Hour switch
        {
            < 12 => "morning",
            < 18 => "afternoon",
            _ => "evening"
        };

        return $"Good {timeOfDay}, {personaName}!";
    }

    private void AnimateDashboardIn()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(360));
        var loginFade = new DoubleAnimation(1, 0, duration);
        loginFade.Completed += (_, _) => LoginExpander.Visibility = Visibility.Collapsed;
        LoginExpander.BeginAnimation(UIElement.OpacityProperty, loginFade);

        GamesExpander.Visibility = Visibility.Visible;
        GamesExpander.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        if (GamesExpander.RenderTransform is TranslateTransform translate)
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration));
    }

    private void ResetDashboard()
    {
        LoginExpander.Visibility = Visibility.Visible;
        LoginExpander.Opacity = 1;
        GamesExpander.Opacity = 0;
        if (GamesExpander.RenderTransform is TranslateTransform translate)
            translate.Y = 12;
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            LogBox.ScrollToEnd();
        });
    }

    private bool FilterGame(object item)
    {
        if (item is not GameEntry game)
            return false;

        var query = GameSearchBox?.Text?.Trim();
         var matchesFavorite = !_favoritesOnly || game.IsFavorite;
         return matchesFavorite && (string.IsNullOrWhiteSpace(query) ||
               game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             game.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void GameSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _gamesView.Refresh();
    }

    private void FavoritesOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = FavoritesOnlyButton.IsChecked == true;
        _gamesView.Refresh();
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.DataContext is not GameEntry game)
            return;

        game.IsFavorite = !game.IsFavorite;
        if (game.IsFavorite)
            _favoriteAppIds.Add(game.AppId);
        else
            _favoriteAppIds.Remove(game.AppId);

        SteamSessionStore.SaveFavoriteAppIds(_favoriteAppIds);
        _gamesView.Refresh();
    }

    private void PresetSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PresetSelector.SelectedItem is not IdlePreset preset)
            return;

        _selectedPresetName = preset.PresetName;
        ApplySelectedPreset();
    }

    private void ApplySelectedPreset()
    {
        if (_selectedPresetName is null)
            return;

        var preset = _presets.FirstOrDefault(item => item.PresetName == _selectedPresetName);
        if (preset is null)
            return;

        var appIds = preset.AppIds.ToHashSet();
        foreach (var game in _games)
            game.IsSelected = appIds.Contains(game.AppId);
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        var appIds = _games.Where(game => game.IsSelected).Select(game => game.AppId).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(name) || appIds.Count == 0)
        {
            AppendLog("Enter a preset name and select at least one game first.");
            return;
        }

        var existing = _presets.FirstOrDefault(preset => string.Equals(preset.PresetName, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            existing.AppIds.Clear();
        else
        {
            existing = new IdlePreset(name, new List<uint>());
            _presets.Add(existing);
        }

        existing.AppIds.AddRange(appIds);
        SteamSessionStore.SavePresets(_presets);
        _selectedPresetName = existing.PresetName;
        PresetSelector.SelectedItem = existing;
        PresetNameBox.Text = "";
        AppendLog($"Saved preset '{existing.PresetName}' with {appIds.Count} game(s).");
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelector.SelectedItem is not IdlePreset preset)
            return;

        _presets.Remove(preset);
        SteamSessionStore.SavePresets(_presets);
        _selectedPresetName = null;
        AppendLog($"Deleted preset '{preset.PresetName}'.");
    }

    private void SelectSavedPersonaStatus()
    {
        var savedStatus = SteamSessionStore.LoadPersonaStatus() ?? "Online";
        PersonaStatusSelector.SelectedItem = PersonaStatusSelector.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), savedStatus, StringComparison.OrdinalIgnoreCase))
            ?? PersonaStatusSelector.Items[1];
    }

    private void PersonaStatusSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PersonaStatusSelector.SelectedValue is not string status)
            return;

        SteamSessionStore.SavePersonaStatus(status);
        _idleClient.SetPreferredPersonaState(ParsePersonaState(status));
    }

    private static EPersonaState ParsePersonaState(string status) => status switch
    {
        "Invisible (offline)" => EPersonaState.Invisible,
        "Away" => EPersonaState.Away,
        "Busy" => EPersonaState.Busy,
        _ => EPersonaState.Online
    };

    private void SelectSavedTheme()
    {
        var savedTheme = SteamSessionStore.LoadTheme() ?? "Windows 11";
        var savedMode = SteamSessionStore.LoadThemeMode() ?? "Dark Mode";
        var item = ThemeSelector.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Content?.ToString(), savedTheme, StringComparison.OrdinalIgnoreCase));
        ThemeSelector.SelectedItem = item ?? ThemeSelector.Items[0];
        ThemeModeSelector.SelectedItem = ThemeModeSelector.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Content?.ToString(), savedMode, StringComparison.OrdinalIgnoreCase))
            ?? ThemeModeSelector.Items[0];
        _themeInitialized = true;
        ApplyTheme(ThemeSelector.SelectedValue?.ToString() ?? "Windows 11",
            ThemeModeSelector.SelectedValue?.ToString() ?? "Dark Mode");
    }

    private void ThemeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_themeInitialized || ThemeSelector.SelectedValue is not string theme)
            return;

        SteamSessionStore.SaveTheme(theme);
        ApplyTheme(theme, ThemeModeSelector.SelectedValue?.ToString() ?? "Dark Mode");
    }

    private void ThemeModeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_themeInitialized || ThemeModeSelector.SelectedValue is not string mode)
            return;

        SteamSessionStore.SaveThemeMode(mode);
        ApplyTheme(ThemeSelector.SelectedValue?.ToString() ?? "Windows 11", mode);
    }

    private void ApplyTheme(string theme, string mode)
    {
        var applicationTheme = mode == "Light Mode" ? ApplicationTheme.Light : ApplicationTheme.Dark;
        var backdrop = theme switch
        {
            "Windows 10" => WindowBackdropType.None,
            "Windows 7" => WindowBackdropType.Acrylic,
            _ => WindowBackdropType.Mica
        };

        ApplicationThemeManager.Apply(applicationTheme, backdrop, true);
    }

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == MainTabs && !_updatingNavigation)
        {
            _updatingNavigation = true;
            LibraryNavButton.IsChecked = MainTabs.SelectedIndex == 0;
            SettingsNavButton.IsChecked = MainTabs.SelectedIndex == 1;
            _updatingNavigation = false;
        }

        if (e.OriginalSource != MainTabs || MainTabs.SelectedContent is not FrameworkElement content)
            return;

        content.Opacity = 0;
        content.RenderTransform = new TranslateTransform(0, 10);
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        if (content.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration));
    }

    private void LibraryNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationTab(0);
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationTab(1);
    }

    private void SelectNavigationTab(int index)
    {
        if (_updatingNavigation)
            return;

        _updatingNavigation = true;
        MainTabs.SelectedIndex = index;
        LibraryNavButton.IsChecked = index == 0;
        SettingsNavButton.IsChecked = index == 1;
        _updatingNavigation = false;
    }

    private void AddPressFeedback(System.Windows.Controls.Primitives.ButtonBase button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = new ScaleTransform(1, 1);
        button.PreviewMouseDown += (_, _) => AnimateButtonScale(button, 0.97);
        button.PreviewMouseUp += (_, _) => AnimateButtonScale(button, 1);
        button.MouseLeave += (_, _) => AnimateButtonScale(button, 1);
    }

    private static void AnimateButtonScale(UIElement button, double scale)
    {
        if (button.RenderTransform is not ScaleTransform transform)
            return;

        var animation = new DoubleAnimation(scale, new Duration(TimeSpan.FromMilliseconds(90)));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void UpdateIdlePulse(bool active)
    {
        IdlePulse.BeginAnimation(UIElement.OpacityProperty, null);
        if (!active)
        {
            IdlePulse.Opacity = 0;
            return;
        }

        IdlePulse.Opacity = 1;
        IdlePulse.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1,
            new Duration(TimeSpan.FromMilliseconds(850)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void GameHeaderImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image image)
            image.Visibility = Visibility.Collapsed;
    }

    private void AutoStopTimer_Tick(object? sender, EventArgs e)
    {
        _timeRemaining -= _autoStopTimer.Interval;
        if (_timeRemaining <= TimeSpan.Zero)
        {
            _autoStopTimer.Stop();
            _timeRemaining = TimeSpan.Zero;
            _idleClient.StopAllIdling();
            TimerText.Text = "Auto stop complete";
            AppendLog("Auto stop timer reached zero. Idling stopped.");
            return;
        }

        TimerText.Text = $"Time remaining: {_timeRemaining:hh\\:mm\\:ss}";
    }

    private bool TryGetTimerDuration(out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!int.TryParse(AutoStopHoursBox.Text, out var hours) ||
            !int.TryParse(AutoStopMinutesBox.Text, out var minutes) ||
            hours < 0 || minutes < 0 || minutes > 59)
        {
            AppendLog("Auto stop timer must use a non negative hour value and 0-59 minutes.");
            return false;
        }

        duration = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private void BeginLibraryLoading()
    {
        LibrarySuccessPanel.Visibility = Visibility.Collapsed;
        LibraryLoadingPanel.Visibility = Visibility.Visible;
        GamesList.Opacity = 0.35;
    }

    private void BeginLoginLoading()
    {
        LoginProgress.Visibility = Visibility.Visible;
        LoginCheckmark.Visibility = Visibility.Collapsed;
        StatusText.Text = "Connecting to Steam...";
    }

    private void ResetLoginLoading()
    {
        LoginProgress.Visibility = Visibility.Collapsed;
        LoginCheckmark.Visibility = Visibility.Collapsed;
    }

    private void ShowLoginSuccess()
    {
        LoginProgress.Visibility = Visibility.Collapsed;
        LoginCheckmark.Visibility = Visibility.Visible;
        LoginCheckmark.Opacity = 0;
        LoginCheckmark.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))));
    }

    private async void CompleteLibraryLoading(int gameCount)
    {
        LibraryLoadingPanel.Visibility = Visibility.Collapsed;
        LibrarySuccessText.Text = $"Library loaded: {gameCount} games";
        LibrarySuccessPanel.Visibility = Visibility.Visible;
        LibrarySuccessPanel.Opacity = 0;
        LibrarySuccessPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))));

        await System.Threading.Tasks.Task.Delay(900);
        if (!IsLoaded)
            return;

        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(220)));
        fadeOut.Completed += (_, _) =>
        {
            LibrarySuccessPanel.Visibility = Visibility.Collapsed;
            GamesList.Opacity = 1;
        };
        LibrarySuccessPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            AppendLog("Logs copied to the clipboard.");
        }
        catch (Exception ex)
        {
            AppendLog($"Couldn't copy logs: {ex.Message}");
        }
    }
    private void PasswordModeButton_Click(object sender, RoutedEventArgs e)
    {
        PasswordPanel.Visibility = Visibility.Visible;
        QrPanel.Visibility = Visibility.Collapsed;
        PasswordModeButton.Appearance = ControlAppearance.Primary;
        QrModeButton.Appearance = ControlAppearance.Secondary;
    }

    private void QrModeButton_Click(object sender, RoutedEventArgs e)
    {
        PasswordPanel.Visibility = Visibility.Collapsed;
        QrPanel.Visibility = Visibility.Visible;
        QrModeButton.Appearance = ControlAppearance.Primary;
        PasswordModeButton.Appearance = ControlAppearance.Secondary;
    }

    private void QrStartButton_Click(object sender, RoutedEventArgs e)
    {
        QrStartButton.IsEnabled = false;
        BeginLoginLoading();
        AppendLog("Requesting a QR code...");
        _idleClient.ConnectWithQr();
    }

    private void ShowQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var pngQr = new PngByteQRCode(data);
        var bytes = pngQr.GetGraphic(10);

        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        QrImage.Source = bitmap;
        AppendLog("QR code ready/1 scan it with the Steam app.");
    }
    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuardCodeBox.Visibility == Visibility.Visible)
        {
            BeginLoginLoading();
            _idleClient.SubmitGuardCode(GuardCodeBox.Text.Trim(), _awaitingTwoFactor);
            return;
        }

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            AppendLog("Enter your Steam username and password first.");
            return;
        }

        BeginLoginLoading();
        _idleClient.ConnectWithPassword(username, password);
    }
    private void DetectGamesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var installed = SteamLocalLibrary.GetInstalledGames();
            _games.Clear();
            foreach (var g in installed)
            {
                g.IsFavorite = _favoriteAppIds.Contains(g.AppId);
                _games.Add(g);
            }
            _gamesView.Refresh();

            AppendLog(installed.Count > 0
                ? $"Found {installed.Count} installed game(s)."
                : "No local Steam installation or games found, is Steam installed?!1");
        }
        catch (Exception ex)
        {
            AppendLog($"Couldn't scan local Steam library: {ex.Message}");
        }
    }

    private void StartIdleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_idleClient.IsLoggedOn)
        {
            AppendLog("Log in to Steam first.");
            return;
        }

        var selected = _games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Check at least one game to idle.");
            return;
        }

        if (!TryGetTimerDuration(out var duration))
            return;

        foreach (var game in selected)
            _idleClient.StartIdling(game.AppId);

        _autoStopTimer.Stop();
        if (duration > TimeSpan.Zero)
        {
            _timeRemaining = duration;
            TimerText.Text = $"Time remaining: {_timeRemaining:hh\\:mm\\:ss}";
            _autoStopTimer.Start();
            AppendLog($"Auto stop timer started for {duration:hh\\:mm}.");
        }
        else
        {
            TimerText.Text = "Auto-stop disabled";
        }
    }

    private void StopIdleButton_Click(object sender, RoutedEventArgs e)
    {
        _autoStopTimer.Stop();
        TimerText.Text = "Auto stop disabled";
        _idleClient.StopAllIdling();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoStopTimer.Stop();
        _idleClient.Shutdown();
        base.OnClosed(e);
    }
}
