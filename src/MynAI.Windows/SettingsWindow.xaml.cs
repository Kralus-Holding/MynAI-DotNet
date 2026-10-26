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
    private DateTime _lastBridgeRefreshAttemptUtc = DateTime.MinValue;


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
                UpdateAccountDesktopBridgeStatus();
            };

        _agentTimer.Start();

        UpdateAgentStatus();
        UpdateDesktopBridgeStatus();
        UpdateAccountDesktopBridgeStatus();
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

        AgentStartupCheckBox.IsChecked =
            AgentStartupManager.IsEnabled();

        StartupAppCheckBox.IsChecked =
            StartupSettingsManager.IsEnabled();

        MicrophoneCheckBox.IsChecked =
            settings.MicrophoneEnabled;

        CameraCheckBox.IsChecked =
            settings.CameraEnabled;

        HotkeyTextBox.Text =
            settings.BringToFrontHotkey;

        UpdateLogoPreview(settings);
        UpdateStartupStatus();
        UpdatePluginStatus();
        RefreshErrorLogViewer();
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
        PluginsPanel.Visibility = Visibility.Collapsed;
        StartupPanel.Visibility = Visibility.Collapsed;
        ErrorLogsPanel.Visibility = Visibility.Collapsed;
        ShortcutPanel.Visibility = Visibility.Collapsed;

        GeneralNavButton.Tag = "Normal";
        AgentNavButton.Tag = "Normal";
        AccountNavButton.Tag = "Normal";
        PrivacyNavButton.Tag = "Normal";
        PluginsNavButton.Tag = "Normal";
        StartupNavButton.Tag = "Normal";
        ErrorLogsNavButton.Tag = "Normal";
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

    private void PluginsNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(PluginsPanel, PluginsNavButton);
        UpdatePluginStatus();
    }

    private void StartupNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(StartupPanel, StartupNavButton);
        UpdateStartupStatus();
    }

    private void ErrorLogsNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(ErrorLogsPanel, ErrorLogsNavButton);
        RefreshErrorLogViewer();
    }

    private void ShortcutNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(ShortcutPanel, ShortcutNavButton);
    }

    private void UpdateStartupStatus()
    {
        var enabled = StartupSettingsManager.IsEnabled();
        StartupAppCheckBox.IsChecked = enabled;
        StartupStatusText.Text = enabled
            ? "MynAssistant, Windows kullanıcı oturumu açıldığında başlatılacak."
            : "MynAssistant Windows oturum açılışında otomatik başlatılmayacak.";
        StartupStatusText.Foreground = new SolidColorBrush(
            enabled ? Colors.LightGreen : Colors.LightBlue);
    }

    private void UpdatePluginStatus()
    {
        var chromePath = PluginIntegrationManager.FindChromeExecutable();
        var localSendPath = PluginIntegrationManager.FindLocalSendExecutable();

        PluginChromeButton.IsEnabled = chromePath is not null;
        PluginChromeStatusText.Text = chromePath is not null
            ? "Chrome bulundu. Düğme yalnızca tarayıcıyı açar."
            : "Chrome bulunamadı. Kurulu değilse uygulamayı yükledikten sonra Yeniden kontrol et.";
        PluginChromeStatusText.Foreground = new SolidColorBrush(
            chromePath is not null ? Colors.LightGreen : Colors.Goldenrod);

        PluginLocalSendButton.IsEnabled = localSendPath is not null;
        PluginLocalSendStatusText.Text = localSendPath is not null
            ? "LocalSend bulundu. Düğme yalnızca uygulamayı açar; dosya göndermez."
            : "LocalSend bulunamadı. Kurulu değilse bu entegrasyon şimdilik kullanılamaz.";
        PluginLocalSendStatusText.Foreground = new SolidColorBrush(
            localSendPath is not null ? Colors.LightGreen : Colors.Goldenrod);
    }

    private void AgentStartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var previous = AgentStartupManager.IsEnabled();
        var requested = AgentStartupCheckBox.IsChecked == true;
        try
        {
            AgentStartupManager.SetEnabled(requested);
            var settings = _settingsStore.Load();
            settings.StartAgentWithWindows = requested;
            settings.StartApplicationWithWindows = StartupSettingsManager.IsEnabled();
            _settingsStore.Save(settings);
            UpdateAgentStatus();
            UpdateAgentDiagnostics();
            SetSaveStatus(
                requested ? "Agent için Windows başlangıcı etkinleştirildi." : "Agent için Windows başlangıcı kapatıldı.",
                Colors.LightGreen);
        }
        catch (Exception ex)
        {
            try
            {
                if (AgentStartupManager.IsEnabled() != previous)
                    AgentStartupManager.SetEnabled(previous);
            }
            catch (Exception rollbackError)
            {
                DiagnosticLogManager.WriteError(
                    "MYN-STARTUP-999", "Agent", "Agent başlangıç ayarı geri alınamadı.", rollbackError);
            }

            AgentStartupCheckBox.IsChecked = previous;
            DiagnosticLogManager.WriteError(
                "MYN-STARTUP-001", "Agent", "Agent başlangıç ayarı değiştirilemedi.", ex);
            MessageBox.Show(
                $"Agent başlangıç ayarı değiştirilemedi. Önceki ayar korunmaya çalışıldı.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartupAppCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var previous = StartupSettingsManager.IsEnabled();
        var requested = StartupAppCheckBox.IsChecked == true;
        try
        {
            StartupSettingsManager.SetEnabled(requested);
            var settings = _settingsStore.Load();
            settings.StartApplicationWithWindows = requested;
            settings.StartAgentWithWindows = AgentStartupManager.IsEnabled();
            _settingsStore.Save(settings);
            UpdateStartupStatus();
            SetSaveStatus(
                requested ? "MynAssistant uygulama başlangıcı etkinleştirildi." : "MynAssistant uygulama başlangıcı kapatıldı.",
                Colors.LightGreen);
        }
        catch (Exception ex)
        {
            try
            {
                if (StartupSettingsManager.IsEnabled() != previous)
                    StartupSettingsManager.SetEnabled(previous);
            }
            catch (Exception rollbackError)
            {
                DiagnosticLogManager.WriteError(
                    "MYN-STARTUP-999", "Windows", "Uygulama başlangıç ayarı geri alınamadı.", rollbackError);
            }

            StartupAppCheckBox.IsChecked = previous;
            UpdateStartupStatus();
            DiagnosticLogManager.WriteError(
                "MYN-STARTUP-002", "Windows", "MynAssistant başlangıç ayarı değiştirilemedi.", ex);
            MessageBox.Show(
                $"MynAssistant başlangıç ayarı değiştirilemedi. Önceki ayar korunmaya çalışıldı.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenStartupSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupSettingsManager.OpenStartupSettings();
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-STARTUP-003", "Windows", "Windows başlangıç ayarları açılamadı.", ex);
            MessageBox.Show(
                $"Windows başlangıç ayarları açılamadı.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenChrome_Click(object sender, RoutedEventArgs e)
    {
        OpenPlugin("Google Chrome", "MYN-PLUGIN-001", PluginIntegrationManager.FindChromeExecutable);
    }

    private void OpenLocalSend_Click(object sender, RoutedEventArgs e)
    {
        OpenPlugin("LocalSend", "MYN-PLUGIN-002", PluginIntegrationManager.FindLocalSendExecutable);
    }

    private void OpenPlugin(string displayName, string errorCode, Func<string?> findExecutable)
    {
        try
        {
            var executablePath = findExecutable();
            if (executablePath is null)
            {
                UpdatePluginStatus();
                MessageBox.Show(
                    $"{displayName} bu bilgisayarda bulunamadı. Uygulama açılmadı.",
                    "MynAssistant — Eklentiler", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PluginIntegrationManager.OpenApplication(executablePath);
            SetSaveStatus($"{displayName} başlatma isteği gönderildi.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(errorCode, displayName, "Entegrasyon uygulaması açılamadı.", ex);
            MessageBox.Show(
                $"{displayName} açılamadı.\n\n{ex.Message}",
                "MynAssistant — Eklentiler", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdatePluginStatus();
        }
    }

    private void RefreshErrorLogs_Click(object sender, RoutedEventArgs e) =>
        RefreshErrorLogViewer();

    private void RefreshErrorLogViewer()
    {
        try
        {
            var lines = DiagnosticLogManager.ReadRecentLines();
            ErrorLogsTextBox.Text = lines.Count == 0
                ? "Henüz tanılama kaydı bulunamadı."
                : string.Join(Environment.NewLine, lines);
            ErrorLogsStatusText.Text = $"{lines.Count} kayıt gösteriliyor. Günlük klasörü: {DiagnosticLogManager.LogDirectory}";
            ErrorLogsStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);
        }
        catch (Exception ex)
        {
            ErrorLogsTextBox.Text = "Hata günlükleri okunamadı. Günlük klasörünü açıp erişim izinlerini kontrol edin.";
            ErrorLogsStatusText.Text = $"MYN-UI-999 — Günlükler okunamadı: {ex.Message}";
            ErrorLogsStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            DiagnosticLogManager.WriteError(
                "MYN-UI-999", "Settings", "Hata günlükleri ekranı güncellenemedi.", ex);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticLogManager.BuildSupportReport());
            ErrorLogsStatusText.Text = "Tanı raporu panoya kopyalandı. Paylaşmadan önce içeriği inceleyebilirsin.";
            ErrorLogsStatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-UI-005", "Diagnostics", "Tanı raporu panoya kopyalanamadı.", ex);
            MessageBox.Show(
                $"Tanı raporu panoya kopyalanamadı.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportSupportReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "MynAssistant destek raporunu kaydet",
            FileName = "MynAssistant-Destek-Raporu.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            Filter = "Metin raporu (*.txt)|*.txt|JSON Lines günlükler (*.jsonl)|*.jsonl"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var report = DiagnosticLogManager.BuildSupportReport();
            File.WriteAllText(dialog.FileName, report, new System.Text.UTF8Encoding(false));
            ErrorLogsStatusText.Text = "Destek raporu bu bilgisayara kaydedildi; otomatik olarak gönderilmedi.";
            ErrorLogsStatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-UI-006", "Diagnostics", "Destek raporu dışa aktarılamadı.", ex);
            MessageBox.Show(
                $"Destek raporu kaydedilemedi.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDiagnosticLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticLogManager.OpenLogDirectory();
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-UI-007", "Diagnostics", "Günlük klasörü açılamadı.", ex);
            MessageBox.Show(
                $"Günlük klasörü açılamadı.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDiagnosticLogs_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Tanılama günlükleri silinecek. Bu işlem uygulama ayarlarını veya Firestore cihaz kayıtlarını silmez. Devam edilsin mi?",
            "Günlükleri temizle", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            DiagnosticLogManager.ClearKnownLogs();
            RefreshErrorLogViewer();
            ErrorLogsStatusText.Text = "Tanılama günlükleri temizlendi.";
            ErrorLogsStatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-UI-008", "Diagnostics", "Tanılama günlükleri temizlenemedi.", ex);
            MessageBox.Show(
                $"Tanılama günlükleri temizlenemedi.\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

        DesktopBridgeStartupCheckBox.IsEnabled = hasSession;
        if (!hasSession)
            DesktopBridgeStartupCheckBox.IsChecked = false;

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
                ? "Telefon-PC bağlantısı için eşleştirme gerekiyor"
                : hasError
                    ? "Bağlantı hatası"
                    : connecting
                        ? "Telefon-PC bağlantısı kuruluyor"
                        : running
                            ? "Köprü çalışıyor — cihaz kaydı bekleniyor"
                            : executablePath is null
                                ? "Bridge bileşeni bulunamadı"
                                : "Hazır — telefon-PC bağlantısı bekleniyor";

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
                    ? "Bu bilgisayarda kullanılabilir telefon-PC oturumu bulunamadı. Güvenli eşleştirme tamamlanana kadar bağlantı çevrimdışı kalır."
                    : hasError
                        ? "Köprü başlatılamadı. Agent günlüklerini ve Windows güvenlik günlüklerini kontrol edin."
                        : "Yerel Agent bu hesaptan bağımsız çalışır.";
    }

    private void UpdateAccountDesktopBridgeStatus()
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

        var statusText = connected
            ? "Telefon-PC bağlantısı hazır"
            : authRequired
                ? "Eşleştirme gerekli"
                : hasError
                    ? "Bağlantı kurulamadı"
                    : connecting
                        ? "Bağlanıyor"
                        : running
                            ? "Cihaz kaydı bekleniyor"
                            : executablePath is null
                                ? "Bağlantı bileşeni bulunamadı"
                                : "Bağlantı henüz başlamadı";

        var color = connected
            ? Colors.LightGreen
            : hasError
                ? Colors.IndianRed
                : authRequired
                    ? Colors.Goldenrod
                    : Colors.LightBlue;

        var messageText = connected
            ? "Android uygulamasıyla bilgisayar bağlantısı kuruldu."
            : authRequired
                ? "Bu bilgisayarda güvenli telefon-PC oturumu yok. İlk eşleştirme sunucu akışı bu sürümde henüz bulunmadığından bağlantı başlatılamaz."
                : hasError
                    ? "Bağlantı kurulamadı. Bağlantıyı yenileyerek tekrar dene."
                    : connecting
                        ? "Bağlantı kuruluyor; durum otomatik güncellenecek."
                        : running
                            ? "Köprü çalışıyor ancak cihaz henüz çevrimiçi değil."
                            : executablePath is null
                                ? "Kurulum dosyaları eksik görünüyor. MynAssistant kurulumunu onar veya yeniden yükle."
                                : "Bağlantıyı başlatmak veya yeniden denemek için Bağlantıyı yenile'ye bas.";

        AccountDesktopBridgeStatusText.Text = statusText;
        AccountDesktopBridgeStatusText.Foreground = new SolidColorBrush(color);
        AccountDesktopBridgeStatusDot.Fill = new SolidColorBrush(color);
        AccountDesktopBridgeMessageText.Text = messageText;
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

    private void RefreshDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBridgeRefreshAttemptUtc < TimeSpan.FromMilliseconds(1200))
            return;

        _lastBridgeRefreshAttemptUtc = now;
        AccountBridgeRefreshButton.IsEnabled = false;
        try
        {
            if (FirebaseDesktopSessionStore.HasValidStoredSession())
            {
                if (DesktopBridgeStartupManager.IsRunning())
                    DesktopBridgeStartupManager.RestartAgent();
                else
                    DesktopBridgeStartupManager.StartAgent();
            }

            UpdateDesktopBridgeStatus();
            UpdateAccountDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            UpdateDesktopBridgeStatus();
            AccountDesktopBridgeStatusText.Text = "Telefon-PC bağlantısı yenilenemedi.";
            AccountDesktopBridgeStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            AccountDesktopBridgeMessageText.Text = ex.Message;
            AccountDesktopBridgeStatusDot.Fill = new SolidColorBrush(Colors.IndianRed);
        }
        finally
        {
            AccountBridgeRefreshButton.IsEnabled = true;
            // Start the debounce window after the operation too, so queued double-clicks are ignored.
            _lastBridgeRefreshAttemptUtc = DateTime.UtcNow;
        }
    }

    private void StartDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            UpdateDesktopBridgeStatus();
            MessageBox.Show(
                "Bu bilgisayarda kayıtlı telefon-PC oturumu bulunamadı. Bağlantı başlatılmadı. " +
                "Bu sürümde ilk eşleştirmeyi güvenli biçimde tamamlayan bir sunucu akışı mevcut değil.",
                "MynAssistant — Telefon-PC bağlantısı",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
            UpdateDesktopBridgeStatus();
        }
    }

    private void StopDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopBridgeStartupManager.StopAgent();
            UpdateDesktopBridgeStatus();
            UpdateAccountDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            UpdateDesktopBridgeStatus();
            UpdateAccountDesktopBridgeStatus();
            MessageBox.Show(
                $"Telefon-PC bağlantısı durdurulamadı:\n\n{ex.Message}",
                "MynAssistant — Telefon-PC bağlantısı",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RestartDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            UpdateDesktopBridgeStatus();
            MessageBox.Show(
                "Bu bilgisayarda kayıtlı telefon-PC oturumu bulunamadı. Bağlantı yeniden başlatılmadı.",
                "MynAssistant — Telefon-PC bağlantısı",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
            UpdateDesktopBridgeStatus();
        }
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
        try
        {
            AgentStartupManager.StopAgent();
            UpdateAgentStatus();
            UpdateAgentDiagnostics();
            SetSaveStatus("Agent durduruldu ve süreç sonlanması doğrulandı.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            UpdateAgentStatus();
            UpdateAgentDiagnostics();
            SetSaveStatus("Agent durdurulamadı.", Colors.IndianRed);
            MessageBox.Show(
                $"Agent durdurulamadı:\n\n{ex.Message}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
            var settings = _settingsStore.Load();
            var startDesktopBridge = DesktopBridgeStartupCheckBox.IsChecked == true
                && FirebaseDesktopSessionStore.HasValidStoredSession();

            DesktopBridgeStartupCheckBox.IsChecked = startDesktopBridge;
            DesktopBridgeStartupManager.SetEnabled(startDesktopBridge);

            settings.StartAgentWithWindows = AgentStartupManager.IsEnabled();
            settings.StartApplicationWithWindows = StartupSettingsManager.IsEnabled();
            settings.StartDesktopBridgeWithWindows = startDesktopBridge;
            settings.WindowsNotifications = NotificationsCheckBox.IsChecked == true;
            settings.MicrophoneEnabled = MicrophoneCheckBox.IsChecked == true;
            settings.CameraEnabled = CameraCheckBox.IsChecked == true;
            settings.BringToFrontHotkey = HotkeyTextBox.Text.Trim();
            _settingsStore.Save(settings);

            if (!GlobalHotkeyService.Apply(settings.BringToFrontHotkey, out var hotkeyError))
            {
                HotkeyInfoText.Text = hotkeyError;
                HotkeyInfoText.Foreground = new SolidColorBrush(Colors.IndianRed);
            }
            else
            {
                HotkeyInfoText.Text = string.IsNullOrWhiteSpace(settings.BringToFrontHotkey)
                    ? "Global kısayol kapalı."
                    : "Global kısayol aktif.";
                HotkeyInfoText.Foreground = new SolidColorBrush(Colors.LightGreen);
            }

            SetSaveStatus("Ayarlar kaydedildi.", Colors.LightGreen);
            UpdateDesktopBridgeStatus();
            UpdateAccountDesktopBridgeStatus();

            // Close only after every persistent settings write above completed successfully.
            _agentTimer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            SetSaveStatus("Kaydedilemedi.", Colors.IndianRed);
            MessageBox.Show($"Ayarlar kaydedilemedi:\n\n{ex.Message}",
                "MynAssistant", MessageBoxButton.OK, MessageBoxImage.Error);
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


