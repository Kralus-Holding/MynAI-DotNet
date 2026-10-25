using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using MynAI.Windows.Google;
using MynAI.Windows.Auth;

namespace MynAI.Windows;

public partial class SettingsWindow : Window
{
    private readonly GoogleOAuthService _googleOAuthService;
    private readonly GoogleAccountStore _googleAccountStore;
    private readonly MynAISettingsStore _settingsStore;

    private readonly DispatcherTimer _agentTimer;

    public SettingsWindow()
    {
        InitializeComponent();

        LoadWindowIconSafely();

        _googleOAuthService =
            new GoogleOAuthService();

        _googleAccountStore =
            new GoogleAccountStore();

        _settingsStore =
            new MynAISettingsStore();

        LoadSettings();
        LoadGoogleAccount();
        ShowPage(GeneralPanel, GeneralNavButton);
        UpdateFirebaseSessionStatus();

        _agentTimer =
            new DispatcherTimer
            {
                Interval =
                    TimeSpan.FromSeconds(1)
            };

        _agentTimer.Tick +=
            (_, _) =>
            {
                UpdateAgentStatus();
                UpdateDesktopBridgeStatus();
            };

        _agentTimer.Start();

        UpdateAgentStatus();
        UpdateFirebaseSessionStatus();
        UpdateDesktopBridgeStatus();
    }

    private void LoadWindowIconSafely()
    {
        var image = MynAssistantBranding.LoadImage(
            MynAssistantBranding.GetAppIconPath());

        if (image is null)
        {
            Debug.WriteLine("MynAssistant settings icon could not be loaded.");
            return;
        }

        Icon = image;
        SettingsBrandingImage.Source = image;
    }

    private void LoadSettings()
    {
        var settings =
            _settingsStore.Load();

        NotificationsCheckBox.IsChecked =
            settings.WindowsNotifications;

        DesktopBridgeStartupCheckBox.IsChecked =
            DesktopBridgeStartupManager.IsEnabled();

        MicrophoneCheckBox.IsChecked =
            settings.MicrophoneEnabled;

        CameraCheckBox.IsChecked =
            settings.CameraEnabled;

        HotkeyTextBox.Text =
            settings.BringToFrontHotkey;

        UpdateLogoPreview(settings);
        UpdateFirebaseSessionStatus();
    }

    private void LoadGoogleAccount()
    {
        var account =
            _googleAccountStore.Load();

        if (account is not null)
        {
            ApplyGoogleAccount(
                account);
        }
    }

    private void ApplyGoogleAccount(
        GoogleAccount account)
    {
        GoogleNameText.Text =
            account.Name;

        GoogleEmailText.Text =
            account.Email;

        if (!string.IsNullOrWhiteSpace(
                account.PhotoUrl))
        {
            try
            {
                GooglePhoto.Source =
                    new BitmapImage(
                        new Uri(
                            account.PhotoUrl));
            }
            catch
            {
            }
        }

        GoogleContactsCheckBox.IsEnabled =
            true;

        GoogleContactsCheckBox.IsChecked =
            true;

        GoogleConnectButton.Content =
            "Kişiler bağlı";
    }

    private void ShowPage(
        UIElement page,
        System.Windows.Controls.Button selectedButton)
    {
        GeneralPanel.Visibility = Visibility.Collapsed;
        AgentPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Collapsed;
        PrivacyPanel.Visibility = Visibility.Collapsed;
        ShortcutPanel.Visibility = Visibility.Collapsed;

        GeneralNavButton.Tag = "Normal";
        AgentNavButton.Tag = "Normal";
        AccountNavButton.Tag = "Normal";
        PrivacyNavButton.Tag = "Normal";
        ShortcutNavButton.Tag = "Normal";

        selectedButton.Tag = "Active";
        page.Visibility = Visibility.Visible;
    }

    private void GeneralNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(GeneralPanel, GeneralNavButton);
    }

    private void AgentNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(AgentPanel, AgentNavButton);

        UpdateAgentStatus();
        UpdateAgentDiagnostics();
    }

    private void AccountNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(AccountPanel, AccountNavButton);
    }

    private void PrivacyNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(PrivacyPanel, PrivacyNavButton);
    }

    private void ShortcutNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(ShortcutPanel, ShortcutNavButton);
    }

    private void UpdateAgentStatus()
    {
        var running =
            AgentStartupManager.IsRunning();

        AgentStatusText.Text =
            running
                ? "Çalışıyor"
                : "Çalışmıyor";

        AgentStatusText.Foreground =
            new SolidColorBrush(
                running
                    ? Colors.LightGreen
                    : Colors.IndianRed);

        AgentStatusDot.Fill =
            new SolidColorBrush(
                running
                    ? Colors.LimeGreen
                    : Colors.DimGray);

        AgentPathText.Text =
            AgentStartupManager
                .TryGetAgentExecutablePath()
            ?? "Agent executable bulunamadı.";
    }

    private void UpdateAgentDiagnostics()
    {
        try
        {
            var logDirectory = AgentStartupManager.GetLogDirectory();
            if (!Directory.Exists(logDirectory))
            {
                AgentDiagnosticsText.Text =
                    "Son başlatma: kayıt bulunamadı. Son hata kodu: yok.";
                return;
            }

            var logPaths = Directory.GetFiles(logDirectory, "MynAgent-*.log")
                .Concat(new[] { Path.Combine(logDirectory, "agent-startup.log") }
                    .Where(File.Exists))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray();

            if (logPaths.Length == 0)
            {
                AgentDiagnosticsText.Text =
                    "Son başlatma: kayıt bulunamadı. Son hata kodu: yok.";
                return;
            }

            string? lastStart = null;
            string? lastError = null;
            string? lastErrorCode = null;
            const string startMarker = "MynAgent started at ";

            // Include Agent runtime logs and the separate CreateProcess/4551 startup log.
            // Oldest-to-newest ordering keeps the final diagnostic focused on the latest error.
            foreach (var logPath in logPaths)
            {
                using var stream = new FileStream(
                    logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    var startIndex = line.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                    if (startIndex >= 0)
                        lastStart = line[(startIndex + startMarker.Length)..].Trim();

                    var looksLikeError =
                        line.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("hata", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("başarısız", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("MYN-", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("code=4551", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Application Control", StringComparison.OrdinalIgnoreCase);

                    if (looksLikeError)
                    {
                        lastError = line.Trim();
                        var match = Regex.Match(
                            line,
                            @"MYN-[A-Z]+-\d+",
                            RegexOptions.IgnoreCase);
                        lastErrorCode = match.Success ? match.Value : null;
                    }
                }
            }

            var errorSummary = lastError is null
                ? "yok"
                : lastErrorCode is null
                    ? "kod bulunamadı; " + lastError
                    : lastErrorCode + "; " + lastError;

            if (errorSummary.Length > 220)
                errorSummary = errorSummary[..220] + "...";

            AgentDiagnosticsText.Text =
                $"Son başlatma: {lastStart ?? "kayıt bulunamadı"}{Environment.NewLine}Son hata: {errorSummary}";
            AgentDiagnosticsText.Foreground = new SolidColorBrush(
                lastError is null ? Colors.LightGreen : Colors.IndianRed);
        }
        catch
        {
            AgentDiagnosticsText.Text =
                "Agent tanılama kaydı okunamadı. Günlük klasörünü açıp inceleyebilirsin.";
            AgentDiagnosticsText.Foreground = new SolidColorBrush(Colors.Goldenrod);
        }
    }

    private void UpdateDesktopBridgeStatus()
    {
        var running = DesktopBridgeStartupManager.IsRunning();
        var executablePath = DesktopBridgeStartupManager.TryGetExecutablePath();
        var hasSession = FirebaseDesktopSessionStore.HasValidStoredSession();
        var runtime = DesktopBridgeStartupManager.ReadRuntimeStatus();
        var isFresh = runtime is not null
            && DateTimeOffset.Now - runtime.UpdatedAt < TimeSpan.FromSeconds(90);
        var state = isFresh ? runtime?.State?.ToLowerInvariant() : null;

        var connected = running && state == "online";
        var hasError = state == "error";
        var authRequired = !hasSession || state == "auth_required";
        var connecting = running && (state == "starting" || state == "connecting");

        DesktopBridgeStatusText.Text = connected
            ? "Bağlandı — PC Android uygulaması için çevrimiçi"
            : authRequired
                ? "Telefon işlemleri için giriş gerekli"
                : hasError
                    ? "Bağlantı hatası"
                    : connecting
                        ? "Telefon-PC bağlantısı kuruluyor"
                        : running
                            ? "Köprü çalışıyor — cihaz kaydı bekleniyor"
                            : executablePath is null
                                ? "Bridge bileşeni bulunamadı"
                                : "Hesap hazır — telefon-PC bağlantısı başlatılabilir";

        var color = connected
            ? Colors.LightGreen
            : hasError
                ? Colors.IndianRed
                : authRequired
                    ? Colors.Goldenrod
                    : Colors.LightBlue;

        DesktopBridgeStatusText.Foreground = new SolidColorBrush(color);
        DesktopBridgeStatusDot.Fill = new SolidColorBrush(color);
        DesktopBridgeExecutablePathText.Text =
            executablePath ?? "MynAssistantDesktopBridge.exe bulunamadı.";

        DesktopBridgeMessageText.Text =
            isFresh && !string.IsNullOrWhiteSpace(runtime?.Message)
                ? runtime.Message
                : !hasSession
                    ? "Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın."
                    : hasError
                        ? "Köprü başlatılamadı. Agent günlüklerini ve Windows güvenlik günlüklerini kontrol edin."
                        : "Yerel Agent bu hesaptan bağımsız çalışır.";
    }

    private void UpdateFirebaseSessionStatus()
    {
        var session = FirebaseDesktopSessionStore.Load();
        var hasSession = session is not null
            && !string.IsNullOrWhiteSpace(session.Uid)
            && !string.IsNullOrWhiteSpace(session.RefreshToken);

        FirebaseSessionStatusText.Text = hasSession
            ? $"Telefon-PC hesabı açık: {session!.Email}"
            : "Oturum açık değil. Telefon işlemleri için önce giriş yapın.";

        FirebaseSessionStatusText.Foreground = new SolidColorBrush(
            hasSession ? Colors.LightGreen : Colors.Goldenrod);
        FirebaseSignOutButton.IsEnabled = hasSession;
        DesktopBridgeStartupCheckBox.IsEnabled = hasSession;

        if (!hasSession)
            DesktopBridgeStartupCheckBox.IsChecked = false;
    }

    private void UpdateLogoPreview(MynAISettings settings)
    {
        LogoPreviewImage.Source = MynAssistantBranding.LoadSelectedLogo(settings);
        KrLogoOptionImage.Source = MynAssistantBranding.LoadImage(MynAssistantBranding.GetKrLogoPath());
        AppLogoOptionImage.Source = MynAssistantBranding.LoadImage(MynAssistantBranding.GetAppIconPath());

        var hasCustomLogo = !string.IsNullOrWhiteSpace(settings.CustomLogoPath)
            && File.Exists(settings.CustomLogoPath);
        CustomLogoOptionImage.Source = hasCustomLogo
            ? MynAssistantBranding.LoadImage(settings.CustomLogoPath)
            : null;
        CustomLogoPlaceholderText.Visibility = hasCustomLogo
            ? Visibility.Collapsed
            : Visibility.Visible;

        var preset = string.IsNullOrWhiteSpace(settings.LogoPreset)
            ? "kr"
            : settings.LogoPreset.ToLowerInvariant();
        if (preset == "black") preset = "kr";

        KrLogoOptionButton.IsChecked = preset == "kr";
        AppLogoOptionButton.IsChecked = preset == "app";
        CustomLogoOptionButton.IsChecked = preset == "custom";

        LogoPresetStatusText.Text = preset switch
        {
            "app" => "MynAssistant uygulama simgesi seçili.",
            "custom" when hasCustomLogo => $"Özel görsel seçili: {Path.GetFileName(settings.CustomLogoPath)}",
            "custom" => "Özel görsel kaydı bulunamadı; KR / Kralus logosu kullanılacak.",
            _ => "KR / Kralus logosu seçili."
        };
    }

    private void SaveLogoPreset(string preset, string message)
    {
        var settings = _settingsStore.Load();
        settings.LogoPreset = preset;
        _settingsStore.Save(settings);
        UpdateLogoPreview(settings);
        SetSaveStatus(message, Colors.LightGreen);
    }

    private void SelectKrLogo_Click(object sender, RoutedEventArgs e) =>
        SaveLogoPreset("kr", "KR / Kralus logosu seçildi.");

    private void SelectAppIconLogo_Click(object sender, RoutedEventArgs e) =>
        SaveLogoPreset("app", "MynAssistant uygulama simgesi seçildi.");

    private void SelectCustomLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Ana pencerede kullanılacak özel görseli seç",
            Filter = "Görseller (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            UpdateLogoPreview(_settingsStore.Load());
            return;
        }

        try
        {
            var copiedPath = MynAssistantBranding.CopyCustomLogo(dialog.FileName);
            var settings = _settingsStore.Load();
            settings.LogoPreset = "custom";
            settings.CustomLogoPath = copiedPath;
            _settingsStore.Save(settings);
            UpdateLogoPreview(settings);
            SetSaveStatus("Özel görsel uygulama verilerine kopyalandı ve kaydedildi.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            UpdateLogoPreview(_settingsStore.Load());
            MessageBox.Show(
                $"Görsel seçilemedi:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void FirebaseSignIn_Click(object sender, RoutedEventArgs e)
    {
        var email = FirebaseEmailTextBox.Text.Trim();
        var password = FirebasePasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            FirebaseSessionStatusText.Text = "E-posta ve şifre alanlarını doldurun.";
            FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.Goldenrod);
            return;
        }

        FirebaseSignInButton.IsEnabled = false;
        FirebaseSignInButton.Content = "Giriş yapılıyor...";
        FirebaseSessionStatusText.Text = "Firebase hesabı doğrulanıyor...";
        FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);

        try
        {
            var session = await new FirebaseDesktopAuthService().SignInAsync(email, password);
            FirebaseDesktopSessionStore.Save(session);
            FirebasePasswordBox.Clear();
            UpdateFirebaseSessionStatus();

            try
            {
                DesktopBridgeStartupManager.StartAgent();
                if (DesktopBridgeStartupCheckBox.IsChecked == true)
                    DesktopBridgeStartupManager.SetEnabled(true);
                UpdateDesktopBridgeStatus();
                FirebaseSessionStatusText.Text = $"Giriş başarılı: {session.Email}. Telefon-PC bağlantısı başlatıldı.";
            }
            catch (Exception bridgeError)
            {
                UpdateDesktopBridgeStatus();
                FirebaseSessionStatusText.Text =
                    $"Hesap girişi başarılı; telefon-PC köprüsü başlatılamadı. {bridgeError.Message}";
                FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.Goldenrod);
            }
        }
        catch (Exception ex)
        {
            FirebaseSessionStatusText.Text = ex.Message;
            FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
        }
        finally
        {
            FirebaseSignInButton.IsEnabled = true;
            FirebaseSignInButton.Content = "Giriş yap";
        }
    }

    private void FirebaseSignOut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopBridgeStartupManager.SetEnabled(false);
            DesktopBridgeStartupManager.StopAgent();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Telefon-PC köprüsü durdurulurken hata: {ex.GetType().Name}");
        }

        FirebaseDesktopSessionStore.Delete();
        FirebasePasswordBox.Clear();
        UpdateFirebaseSessionStatus();
        UpdateDesktopBridgeStatus();
        SetSaveStatus("Telefon-PC hesabından çıkış yapıldı.", Colors.LightGreen);
    }

    private void OpenAgentLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentStartupManager.OpenLogDirectory();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent günlük klasörü açılamadı:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StartDesktopBridge_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            ShowPage(AccountPanel, AccountNavButton);
            UpdateFirebaseSessionStatus();
            FirebaseSessionStatusText.Text = "Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın.";
            FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.Goldenrod);
            return;
        }

        try
        {
            DesktopBridgeStartupManager.StartAgent();
            UpdateDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Telefon-PC bağlantısı başlatılamadı:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopDesktopBridge_Click(
        object sender,
        RoutedEventArgs e)
    {
        DesktopBridgeStartupManager.StopAgent();
        UpdateDesktopBridgeStatus();
    }

    private void RestartDesktopBridge_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            ShowPage(AccountPanel, AccountNavButton);
            UpdateFirebaseSessionStatus();
            FirebaseSessionStatusText.Text = "Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın.";
            FirebaseSessionStatusText.Foreground = new SolidColorBrush(Colors.Goldenrod);
            return;
        }

        try
        {
            DesktopBridgeStartupManager.RestartAgent();
            UpdateDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Telefon-PC bağlantısı yeniden başlatılamadı:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StartAgent_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AgentStartupManager.StartAgent();

            SetSaveStatus(
                "Agent başlatıldı.",
                Colors.LightGreen);

            UpdateAgentStatus();
            UpdateAgentDiagnostics();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent başlatılamadı:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopAgent_Click(
        object sender,
        RoutedEventArgs e)
    {
        AgentStartupManager.StopAgent();

        SetSaveStatus(
            "Agent durduruldu.",
            Colors.LightGreen);

        UpdateAgentStatus();
        UpdateAgentDiagnostics();
    }

    private void RestartAgent_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AgentStartupManager.RestartAgent();

            SetSaveStatus(
                "Agent yeniden başlatıldı.",
                Colors.LightGreen);

            UpdateAgentStatus();
            UpdateAgentDiagnostics();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent yeniden başlatılamadı:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void GoogleConnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!File.Exists(GoogleOAuthConfigStore.GetPath()))
        {
            MessageBox.Show(
                "Google Kişiler masaüstü yetkilendirmesi bu kurulumda yapılandırılmamış. Müşteriden OAuth JSON dosyası istenmez. Telefon-PC bağlantısı için Firebase hesabıyla giriş yapmanız yeterlidir.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        GoogleConnectButton.IsEnabled =
            false;

        GoogleConnectButton.Content =
            "Bağlanıyor...";

        try
        {
            var account =
                await _googleOAuthService
                    .SignInAsync();

            _googleAccountStore.Save(
                account);

            ApplyGoogleAccount(
                account);
        }
        catch (Exception ex)
        {
            GoogleConnectButton.Content =
                "Kişileri bağla";

            MessageBox.Show(
                $"Google bağlantısı başarısız:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            GoogleConnectButton.IsEnabled =
                true;
        }
    }

    private void GoogleContactsCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
    }

    private void GoogleContactsCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
    }

    private void HotkeyTextBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        e.Handled = true;

        var key =
            e.Key == Key.System
                ? e.SystemKey
                : e.Key;

        if (key is Key.LeftCtrl or
            Key.RightCtrl or
            Key.LeftAlt or
            Key.RightAlt or
            Key.LeftShift or
            Key.RightShift or
            Key.LWin or
            Key.RWin)
        {
            return;
        }

        var modifiers =
            e.KeyboardDevice.Modifiers;

        if (modifiers == ModifierKeys.None)
        {
            HotkeyInfoText.Text =
                "En az bir Ctrl, Alt, Shift veya Win tuşu kullan.";
            return;
        }

        var parts =
            new List<string>();

        if ((modifiers &
             ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers &
             ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers &
             ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers &
             ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(
            key.ToString());

        HotkeyTextBox.Text =
            string.Join(
                "+",
                parts);

        HotkeyInfoText.Text =
            "Kısayol hazır. Kaydet'e bas.";
    }

    private void ClearHotkey_Click(
        object sender,
        RoutedEventArgs e)
    {
        HotkeyTextBox.Clear();

        HotkeyInfoText.Text =
            "Global kısayol kapatılacak.";
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            // The local Agent is mandatory and independent from account login.
            AgentStartupManager.EnsureAutomaticStartup();

            var startDesktopBridge =
                DesktopBridgeStartupCheckBox.IsChecked == true
                && FirebaseDesktopSessionStore.HasValidStoredSession();

            DesktopBridgeStartupCheckBox.IsChecked = startDesktopBridge;
            DesktopBridgeStartupManager.SetEnabled(startDesktopBridge);

            var settings =
                _settingsStore.Load();

            settings.StartAgentWithWindows = true;

            settings.StartDesktopBridgeWithWindows =
                startDesktopBridge;

            settings.WindowsNotifications =
                NotificationsCheckBox.IsChecked == true;

            settings.MicrophoneEnabled =
                MicrophoneCheckBox.IsChecked == true;

            settings.CameraEnabled =
                CameraCheckBox.IsChecked == true;

            settings.BringToFrontHotkey =
                HotkeyTextBox.Text.Trim();

            _settingsStore.Save(
                settings);

            if (!GlobalHotkeyService.Apply(
                    settings.BringToFrontHotkey,
                    out var hotkeyError))
            {
                HotkeyInfoText.Text =
                    hotkeyError;

                HotkeyInfoText.Foreground =
                    new SolidColorBrush(
                        Colors.IndianRed);
            }
            else
            {
                HotkeyInfoText.Text =
                    string.IsNullOrWhiteSpace(
                        settings.BringToFrontHotkey)
                        ? "Global kısayol kapalı."
                        : "Global kısayol aktif.";

                HotkeyInfoText.Foreground =
                    new SolidColorBrush(
                        Colors.LightGreen);
            }

            SetSaveStatus(
                "Ayarlar kaydedildi.",
                Colors.LightGreen);
        }
        catch (Exception ex)
        {
            SetSaveStatus(
                "Kaydedilemedi.",
                Colors.IndianRed);

            MessageBox.Show(
                $"Ayarlar kaydedilemedi:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetSaveStatus(
        string text,
        Color color)
    {
        SaveStatusText.Text =
            text;

        SaveStatusText.Foreground =
            new SolidColorBrush(
                color);
    }

    private void OpenMicrophoneSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenWindowsSettings(
            "ms-settings:privacy-microphone");
    }

    private void OpenCameraSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenWindowsSettings(
            "ms-settings:privacy-webcam");
    }

    private static void OpenWindowsSettings(
        string uri)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Windows ayarları açılamadı:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _agentTimer.Stop();
        Close();
    }

    protected override void OnClosed(
        EventArgs e)
    {
        _agentTimer.Stop();

        base.OnClosed(e);
    }
}


