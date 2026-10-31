using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using MynAI.Windows.Auth;

namespace MynAI.Windows;

public partial class SettingsWindow : Window
{
    private readonly MynAISettingsStore _settingsStore;

    private readonly DispatcherTimer _agentTimer;
    private readonly DesktopPairingClient _desktopPairingClient = new();
    private CancellationTokenSource? _pairingCancellation;
    private DateTime _lastBridgeRefreshAttemptUtc = DateTime.MinValue;


    public SettingsWindow()
    {
        InitializeComponent();

        LoadWindowIconSafely();

        _settingsStore =
            new MynAISettingsStore();

        LoadSettings();
        UpdateDesktopLoginStatus();
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
            };

        _agentTimer.Start();

        UpdateAgentStatus();
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
            System.Windows.Clipboard.SetText(DiagnosticLogManager.BuildSupportReport());
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
            AgentStartupManager.TryGetAgentExecutablePath()
            ?? "MynAgent.exe bulunamadı. Kurulumu onar veya yeniden yükle.";
        AgentStartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
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

        UpdateUserLogoGallery(settings);
    }

    private static bool IsOwnedUserLogoPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            var root = Path.GetFullPath(MynAssistantBranding.UserLogoDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var allowedPrefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(root))
                return false;

            // Do not follow a replaced user-content directory or a logo file that
            // became a symlink/junction after upload. Deletion is only for our own
            // regular files under the expected application data directory.
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var fileAttributes = File.GetAttributes(fullPath);
            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
                return false;

            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                return false;

            return File.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> GetUserLogoPaths(MynAISettings settings)
    {
        var candidates = (settings.UserLogoPaths ?? new List<string>())
            .Concat(string.IsNullOrWhiteSpace(settings.CustomLogoPath)
                ? Array.Empty<string>()
                : new[] { settings.CustomLogoPath });

        return candidates
            .Select(path => IsOwnedUserLogoPath(path, out var fullPath) ? fullPath : null)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void UpdateUserLogoGallery(MynAISettings settings)
    {
        UserLogosGalleryPanel.Children.Clear();
        var paths = GetUserLogoPaths(settings);
        if (paths.Count == 0)
        {
            UserLogosGalleryPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Henüz özel logo eklenmedi. Yukarıdaki “Logo ekle” kartını kullan.",
                Foreground = new SolidColorBrush(Color.FromRgb(126, 138, 157)),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 10),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            return;
        }

        foreach (var path in paths)
        {
            var bitmap = MynAssistantBranding.LoadImage(path);
            if (bitmap is null)
                continue;

            var selected = string.Equals(settings.LogoPreset, "custom", StringComparison.OrdinalIgnoreCase)
                && string.Equals(settings.CustomLogoPath, path, StringComparison.OrdinalIgnoreCase);
            var card = new System.Windows.Controls.Border
            {
                Width = 126,
                Height = 116,
                Margin = new Thickness(0, 0, 10, 4),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(selected
                    ? Color.FromRgb(139, 156, 255)
                    : Color.FromRgb(38, 52, 73)),
                Background = new SolidColorBrush(Color.FromRgb(16, 23, 32)),
                ToolTip = Path.GetFileName(path)
            };
            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Width = 66,
                Height = 66,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetRow(image, 0);
            grid.Children.Add(image);

            var name = new System.Windows.Controls.TextBlock
            {
                Text = Path.GetFileName(path),
                Foreground = new SolidColorBrush(Color.FromRgb(223, 232, 243)),
                FontSize = 9,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(1, 4, 1, 0),
                ToolTip = Path.GetFileName(path)
            };
            System.Windows.Controls.Grid.SetRow(name, 1);
            grid.Children.Add(name);

            var options = new System.Windows.Controls.Button
            {
                Content = "⋮",
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(0, -3, -3, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(225, 233, 244)),
                BorderThickness = new Thickness(0),
                FontSize = 18,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Logo seçenekleri"
            };
            var menu = new System.Windows.Controls.ContextMenu();
            var selectItem = new System.Windows.Controls.MenuItem { Header = "Logo olarak seç" };
            selectItem.Click += (_, _) => SelectUserLogo(path);
            var deleteItem = new System.Windows.Controls.MenuItem
            {
                Header = "Sil",
                Foreground = new SolidColorBrush(Color.FromRgb(232, 90, 90))
            };
            deleteItem.Click += (_, _) => DeleteUserLogo(path);
            menu.Items.Add(selectItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(deleteItem);
            options.Click += (_, _) =>
            {
                menu.PlacementTarget = options;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            grid.Children.Add(options);

            card.Child = grid;
            UserLogosGalleryPanel.Children.Add(card);
        }
    }

    private void SelectUserLogo(string path)
    {
        if (!IsOwnedUserLogoPath(path, out var safePath)
            || MynAssistantBranding.LoadImage(safePath) is null)
        {
            MessageBox.Show(
                "Seçilen kullanıcı logosu bulunamadı veya geçerli bir görsel değil.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateLogoPreview(_settingsStore.Load());
            return;
        }

        try
        {
            var settings = _settingsStore.Load();
            settings.UserLogoPaths = GetUserLogoPaths(settings);
            if (!settings.UserLogoPaths.Contains(safePath, StringComparer.OrdinalIgnoreCase))
                settings.UserLogoPaths.Add(safePath);
            settings.LogoPreset = "custom";
            settings.CustomLogoPath = safePath;
            _settingsStore.Save(settings);
            UpdateLogoPreview(settings);
            SetSaveStatus("Yüklediğin logo seçildi.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-LOGO-002",
                "Branding",
                "Kullanıcı logosu seçimi kaydedilemedi.",
                ex);
            SetSaveStatus("Logo seçimi kaydedilemedi. (MYN-LOGO-002)", Colors.IndianRed);
        }
    }

    private void DeleteUserLogo(string path)
    {
        if (!IsOwnedUserLogoPath(path, out var safePath))
        {
            MessageBox.Show(
                "Bu dosya kullanıcı tarafından yüklenen logo klasöründe değil. Varsayılan marka görselleri silinemez.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            $"“{Path.GetFileName(safePath)}” logosunu silmek istediğine emin misin?",
            "Kullanıcı logosunu sil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            var settings = _settingsStore.Load();
            var remaining = GetUserLogoPaths(settings)
                .Where(existing => !string.Equals(existing, safePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Only files below the app's user-content logo directory can be removed.
            File.Delete(safePath);
            settings.UserLogoPaths = remaining;
            if (string.Equals(settings.CustomLogoPath, safePath, StringComparison.OrdinalIgnoreCase))
            {
                settings.CustomLogoPath = remaining.LastOrDefault();
                if (string.Equals(settings.LogoPreset, "custom", StringComparison.OrdinalIgnoreCase))
                    settings.LogoPreset = settings.CustomLogoPath is null ? "kr" : "custom";
            }

            _settingsStore.Save(settings);
            UpdateLogoPreview(settings);
            SetSaveStatus("Yüklediğin logo silindi.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-LOGO-003",
                "Branding",
                "Kullanıcı logosu silinemedi.",
                ex);
            UpdateLogoPreview(_settingsStore.Load());
            SetSaveStatus("Logo silinemedi. (MYN-LOGO-003)", Colors.IndianRed);
            MessageBox.Show(
                "Logo silinemedi. Dosya kullanımda olabilir veya erişim izni olmayabilir.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
            settings.UserLogoPaths = GetUserLogoPaths(settings);
            if (!settings.UserLogoPaths.Contains(copiedPath, StringComparer.OrdinalIgnoreCase))
                settings.UserLogoPaths.Add(copiedPath);
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

    private void UpdateDesktopLoginStatus()
    {
        var session = FirebaseDesktopSessionStore.Load();
        UpdateDesktopProfilePhoto(session?.PhotoUrl);
        if (session is null)
        {
            DesktopLoginStatusText.Text =
                "Giriş yapılmadı. Giriş veya kayıt için devam et.";
            DesktopLoginButton.Content = "Giriş yap";
            DesktopLogoutButton.Visibility = Visibility.Collapsed;
            return;
        }

        DesktopLoginStatusText.Text = string.IsNullOrWhiteSpace(session.Email)
            ? "Oturum açık."
            : $"Oturum açık: {session.Email}";
        DesktopLoginButton.Content = "Hesabı değiştir";
        DesktopLogoutButton.Visibility = Visibility.Visible;
    }

    private void UpdateDesktopProfilePhoto(string? photoUrl)
    {
        DesktopProfilePhoto.Source = LoadAppFallbackPhoto();

        // Only allow HTTPS URLs returned by the Firebase-authenticated profile.
        // Google Contacts OAuth is not used for the MynAssistant account session.
        if (!Uri.TryCreate(photoUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var profileImage = new BitmapImage();
            profileImage.BeginInit();
            profileImage.CacheOption = BitmapCacheOption.OnDemand;
            profileImage.UriSource = uri;
            profileImage.EndInit();
            DesktopProfilePhoto.Source = profileImage;
        }
        catch
        {
            // The app logo remains visible if a profile image cannot be loaded.
            DesktopProfilePhoto.Source = LoadAppFallbackPhoto();
        }
    }

    private void DesktopProfilePhoto_ImageFailed(
        object sender,
        System.Windows.ExceptionRoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image image)
            return;

        if (image.Source is BitmapImage remoteImage &&
            string.Equals(remoteImage.UriSource?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            image.Source = LoadAppFallbackPhoto();
    }

    private static BitmapImage? LoadAppFallbackPhoto()
    {
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, "Resources", "myn.jpg");
        if (!File.Exists(fallbackPath))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(fallbackPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private async void LoginDesktopAccount_Click(object sender, RoutedEventArgs e)
    {
        var loginWindow = new AccountLoginWindow { Owner = this };
        if (loginWindow.ShowDialog() != true)
            return;

        // Keep the existing phone-approved Firebase flow as an explicit alternative.
        // It never silently replaces a stored session; StartDesktopPairingAsync asks first.
        if (loginWindow.PhoneApprovalRequested)
        {
            await StartDesktopPairingAsync();
            return;
        }

        var session = loginWindow.AuthenticatedSession;
        if (session is null)
            return;

        var previousSession = FirebaseDesktopSessionStore.Load();
        if (previousSession is not null &&
            !string.Equals(previousSession.Uid, session.Uid, StringComparison.Ordinal))
        {
            var answer = MessageBox.Show(
                "Farklı bir MynAssistant hesabıyla giriş yapmak üzeresin. Bu bilgisayardaki telefon-PC oturumu değiştirilecek; yerel ayarlar ve dosyalar korunacak. Devam edilsin mi?",
                "Hesabı değiştir",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        try
        {
            // The session store uses Windows DPAPI (CurrentUser) and an atomic replace.
            // Save only after Firebase returned a complete, authenticated session.
            FirebaseDesktopSessionStore.Save(session);
            UpdateDesktopLoginStatus();
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-AUTH-006",
                "FirebaseDesktopAuth",
                "Firebase oturumu Windows kullanıcısının korumalı depolamasına kaydedilemedi.",
                ex);
            SetSaveStatus("Oturum güvenli şekilde kaydedilemedi. Önceki oturum korunmaya çalışıldı. (MYN-AUTH-006)", Colors.IndianRed);
            MessageBox.Show(
                "Firebase oturumu bu Windows kullanıcı profiline güvenli şekilde kaydedilemedi. Önceki oturum silinmedi.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            UpdateDesktopLoginStatus();
            return;
        }

        try
        {
            DesktopBridgeStartupManager.SetEnabled(true);
            if (DesktopBridgeStartupManager.IsRunning())
                DesktopBridgeStartupManager.RestartAgent();
            else
                DesktopBridgeStartupManager.StartAgent();

            UpdateDesktopBridgeStatus();
            var statusMessage = loginWindow.StatusMessage ?? "Firebase hesabına giriş başarılı.";
            var emailWarning = statusMessage.Contains("gönderilemedi", StringComparison.OrdinalIgnoreCase);
            SetSaveStatus(statusMessage, emailWarning ? Colors.IndianRed : Colors.LightGreen);
        }
        catch (Exception bridgeError)
        {
            DiagnosticLogManager.WriteError(
                "MYN-DEVICE-006",
                "DesktopBridge",
                "Firebase oturumu kaydedildi ancak Desktop Bridge yeniden başlatılamadı.",
                bridgeError);
            UpdateDesktopBridgeStatus();
            SetSaveStatus(
                "Oturum açıldı ancak telefon-PC köprüsü yeniden başlatılamadı. Bağlantı durumunu yenile. (MYN-DEVICE-006)",
                Colors.IndianRed);
        }
    }

    private void LogoutDesktopAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            FirebaseDesktopSessionStore.Delete();
            UpdateDesktopLoginStatus();
            UpdateDesktopBridgeStatus();
            return;
        }

        var answer = MessageBox.Show(
            "Bu bilgisayardaki MynAssistant hesabından çıkış yapılsın mı? Yerel uygulama ayarları ve dosyalar korunur; telefon-PC bağlantısı durdurulur.",
            "MynAssistant hesabından çıkış",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            // Cancel an in-flight phone approval first. Otherwise a late approval could
            // save a new session after sign-out and restart the cloud bridge.
            _pairingCancellation?.Cancel();

            // Disable auto-start and stop the bridge before deleting its protected
            // session, so a live process cannot continue using the old account.
            DesktopBridgeStartupManager.SetEnabled(false);
            DesktopBridgeStartupManager.StopAgent();
            FirebaseDesktopSessionStore.DeleteOrThrow();

            UpdateDesktopLoginStatus();
            UpdateDesktopBridgeStatus();
            DesktopPairingStatusText.Text = "Hesaptan çıkış yapıldı. Yerel ayarlar ve uygulama dosyaları korunuyor.";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);
            DesktopPairingCodeText.Visibility = Visibility.Collapsed;
            SetSaveStatus("MynAssistant hesabından çıkış yapıldı.", Colors.LightGreen);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-AUTH-005",
                "FirebaseDesktopAuth",
                "Hesaptan çıkış tamamlanamadı; korumalı oturum silinmedi.",
                ex);
            UpdateDesktopLoginStatus();
            UpdateDesktopBridgeStatus();
            SetSaveStatus(
                "Çıkış güvenli biçimde tamamlanamadı. Korumalı oturum korunuyor; hata günlüklerini kontrol et. (MYN-AUTH-005)",
                Colors.IndianRed);
            MessageBox.Show(
                "Desktop Bridge durdurulamadığı veya başlangıç ayarı değiştirilemediği için çıkış tamamlanmadı. Güvenlik için kaydedilmiş oturum silinmedi.",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void StartDesktopPairing_Click(object sender, RoutedEventArgs e)
    {
        await StartDesktopPairingAsync();
    }

    private async Task StartDesktopPairingAsync()
    {
        if (_pairingCancellation is { IsCancellationRequested: false })
            return;

        var desktopSession = FirebaseDesktopSessionStore.Load();
        if (desktopSession is null || !FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            DesktopPairingStatusText.Text = "Önce Hesap bölümünden MynAssistant / Google hesabınla giriş yap.";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _pairingCancellation = cancellation;
        StartDesktopPairingButton.IsEnabled = false;
        CancelDesktopPairingButton.IsEnabled = true;
        DesktopPairingQrImage.Source = null;
        DesktopPairingQrImage.Visibility = Visibility.Collapsed;
        DesktopPairingCodeText.Text = "";
        DesktopPairingCodeText.Visibility = Visibility.Collapsed;
        DesktopPairingStatusText.Text = "Aynı MynAssistant hesabı doğrulanıyor ve imzalı QR kodu oluşturuluyor...";
        DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);

        try
        {
            var currentSessionIdToken = await _desktopPairingClient.GetFreshIdTokenAsync(
                desktopSession,
                cancellation.Token);
            var pairing = await _desktopPairingClient.StartAsync(
                Environment.MachineName,
                currentSessionIdToken,
                cancellation.Token);

            var qrExpiresAtUnix = pairing.ExpiresAtUtc.ToUnixTimeSeconds();
            var qrPayload = $"MYNASSISTANT-PAIR:{pairing.Code}:{qrExpiresAtUnix}:{pairing.QrSignature}";
            var qrGenerator = new QRCoder.QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(qrPayload, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var qrBytes = new QRCoder.PngByteQRCode(qrData).GetGraphic(8);
            using (var stream = new MemoryStream(qrBytes))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                DesktopPairingQrImage.Source = bitmap;
            }

            DesktopPairingQrImage.Visibility = Visibility.Visible;
            DesktopPairingCodeText.Text = pairing.Code;
            DesktopPairingCodeText.Visibility = Visibility.Visible;
            DesktopPairingStatusText.Text =
                $"Android MynAssistant → Ayarlar → Agent bölümünden QR kodunu tara ve aynı hesapla onayla. " +
                $"Son kullanma: {pairing.ExpiresAtUtc.ToLocalTime():HH:mm:ss}.";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);

            while (DateTimeOffset.UtcNow < pairing.ExpiresAtUtc)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var status = await _desktopPairingClient.PollAsync(
                    pairing.PairingId,
                    pairing.PollSecret,
                    cancellation.Token);

                if (string.Equals(status.State, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    var session = status.Session
                        ?? throw new InvalidDataException("Onaylanan eşleştirme oturumu eksik.");
                    if (!StringComparer.Ordinal.Equals(session.Uid, desktopSession.Uid))
                        throw new InvalidDataException(
                            "Onaylanan telefon hesabı Windows hesabıyla aynı değil. Mevcut oturum korunuyor.");

                    FirebaseDesktopSessionStore.Save(session);
                    UpdateDesktopLoginStatus();

                    try
                    {
                        DesktopBridgeStartupManager.SetEnabled(true);
                        if (DesktopBridgeStartupManager.IsRunning())
                            DesktopBridgeStartupManager.RestartAgent();
                        else
                            DesktopBridgeStartupManager.StartAgent();

                        UpdateDesktopBridgeStatus();
                        DesktopPairingStatusText.Text =
                            $"Eşleştirme tamamlandı. {status.MachineName ?? Environment.MachineName} aynı MynAssistant hesabına bağlandı.";
                        DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                        DesktopPairingQrImage.Visibility = Visibility.Collapsed;
                        DesktopPairingCodeText.Visibility = Visibility.Collapsed;
                        SetSaveStatus("Telefon-PC bağlantısı eşleştirildi.", Colors.LightGreen);
                    }
                    catch (Exception bridgeError)
                    {
                        DiagnosticLogManager.WriteError(
                            "MYN-DEVICE-006",
                            "DesktopBridge",
                            "Telefon eşleştirildi ancak Desktop Bridge başlatılamadı.",
                            bridgeError);
                        UpdateDesktopBridgeStatus();
                        DesktopPairingStatusText.Text =
                            "Hesap onaylandı ancak telefon-PC köprüsü başlatılamadı. Bağlantıyı yenile ve günlükleri kontrol et. (MYN-DEVICE-006)";
                        DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
                    }

                    return;
                }

                DesktopPairingStatusText.Text =
                    $"Telefon onayı bekleniyor. Kalan süre: {Math.Max(0, (int)(pairing.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)} sn.";
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
            }

            DesktopPairingStatusText.Text = "Eşleştirme kodunun süresi doldu. Yeni QR kodu oluştur.";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            DesktopPairingQrImage.Visibility = Visibility.Collapsed;
            DesktopPairingCodeText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DesktopPairingStatusText.Text = "Eşleştirme iptal edildi. Önceki MynAssistant oturumu korunuyor.";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.LightBlue);
            DesktopPairingQrImage.Visibility = Visibility.Collapsed;
            DesktopPairingCodeText.Visibility = Visibility.Collapsed;
        }
        catch (DesktopPairingClientException ex)
        {
            DiagnosticLogManager.WriteError(ex.ErrorCode, "DesktopBridge",
                "Telefon-PC eşleştirme API'si hata yanıtı verdi.", ex);
            DesktopPairingStatusText.Text = $"{ex.Message} ({ex.ErrorCode})";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            DesktopPairingQrImage.Visibility = Visibility.Collapsed;
            DesktopPairingCodeText.Visibility = Visibility.Collapsed;
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLogManager.WriteError("MYN-SERVER-001", "DesktopBridge",
                "Eşleştirme sunucusuna ulaşılamadı.", ex);
            DesktopPairingStatusText.Text =
                "Eşleştirme sunucusuna ulaşılamadı. İnternet bağlantısını veya Render dağıtımını kontrol et. (MYN-SERVER-001)";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            DiagnosticLogManager.WriteError("MYN-SERVER-002", "DesktopBridge",
                "Eşleştirme isteği zaman aşımına uğradı.", ex);
            DesktopPairingStatusText.Text = "Sunucu yanıtı zaman aşımına uğradı. Biraz sonra yeniden dene. (MYN-SERVER-002)";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError("MYN-DEVICE-999", "DesktopBridge",
                "Telefon-PC eşleştirmesi tamamlanamadı.", ex);
            DesktopPairingStatusText.Text = $"{ex.Message} (MYN-DEVICE-999)";
            DesktopPairingStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
        }
        finally
        {
            if (ReferenceEquals(_pairingCancellation, cancellation))
                _pairingCancellation = null;
            cancellation.Dispose();
            StartDesktopPairingButton.IsEnabled = true;
            CancelDesktopPairingButton.IsEnabled = false;
            UpdateDesktopBridgeStatus();
        }
    }

    private void CancelDesktopPairing_Click(object sender, RoutedEventArgs e)
    {
        _pairingCancellation?.Cancel();
    }

    private void RefreshDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBridgeRefreshAttemptUtc < TimeSpan.FromMilliseconds(1200))
            return;

        _lastBridgeRefreshAttemptUtc = now;
        try
        {
            if (!FirebaseDesktopSessionStore.HasValidStoredSession())
            {
                DesktopBridgeStatusText.Text = "Önce MynAssistant hesabıyla giriş yap.";
                DesktopBridgeStatusText.Foreground = new SolidColorBrush(Colors.Goldenrod);
                return;
            }

            DesktopBridgeStartupManager.SetEnabled(true);
            if (DesktopBridgeStartupManager.IsRunning())
                DesktopBridgeStartupManager.RestartAgent();
            else
                DesktopBridgeStartupManager.StartAgent();
            UpdateDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-DEVICE-007", "DesktopBridge", "Telefon-PC bağlantısı yenilenemedi.", ex);
            DesktopBridgeStatusText.Text = "Telefon-PC bağlantısı yenilenemedi.";
            DesktopBridgeStatusText.Foreground = new SolidColorBrush(Colors.IndianRed);
            DesktopBridgeMessageText.Text = ex.Message;
            DesktopBridgeStatusDot.Fill = new SolidColorBrush(Colors.IndianRed);
        }
        finally
        {
            _lastBridgeRefreshAttemptUtc = DateTime.UtcNow;
        }
    }

    private void StartDesktopBridge_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
        {
            UpdateDesktopBridgeStatus();
            MessageBox.Show(
                "Bu bilgisayarda kayıtlı telefon-PC oturumu bulunamadı. Ayarlar → Hesap → Telefonla eşleştir bölümünden güvenli eşleştirmeyi başlat.",
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
        }
        catch (Exception ex)
        {
            DiagnosticLogManager.WriteError(
                "MYN-AGENT-003", "DesktopBridge", "Telefon-PC bağlantısı durdurulamadı.", ex);
            UpdateDesktopBridgeStatus();
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
            DiagnosticLogManager.WriteError(
                "MYN-AGENT-002", "Agent", "Agent durdurma işlemi başarısız oldu.", ex);
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
        var originalSettings = _settingsStore.Load();
        var previousBridgeStartup = DesktopBridgeStartupManager.IsEnabled();
        var requestedBridgeStartup = DesktopBridgeStartupCheckBox.IsChecked == true
            && FirebaseDesktopSessionStore.HasValidStoredSession();

        var updatedSettings = _settingsStore.Load();
        updatedSettings.StartAgentWithWindows = AgentStartupManager.IsEnabled();
        updatedSettings.StartApplicationWithWindows = StartupSettingsManager.IsEnabled();
        updatedSettings.StartDesktopBridgeWithWindows = requestedBridgeStartup;
        updatedSettings.WindowsNotifications = NotificationsCheckBox.IsChecked == true;
        updatedSettings.MicrophoneEnabled = MicrophoneCheckBox.IsChecked == true;
        updatedSettings.CameraEnabled = CameraCheckBox.IsChecked == true;
        updatedSettings.BringToFrontHotkey = HotkeyTextBox.Text.Trim();

        if (!IsValidHotkey(updatedSettings.BringToFrontHotkey, out var hotkeyError))
        {
            HotkeyInfoText.Text = hotkeyError;
            HotkeyInfoText.Foreground = new SolidColorBrush(Colors.IndianRed);
            SetSaveStatus("Ayarlar kaydedilmedi; global kısayol geçersiz.", Colors.IndianRed);
            return;
        }

        var settingsSaved = false;
        try
        {
            _settingsStore.Save(updatedSettings);
            settingsSaved = true;

            DesktopBridgeStartupManager.SetEnabled(requestedBridgeStartup);
            if (AgentStartupManager.IsRunning())
                AgentStartupManager.RestartAgent();
            else
                AgentStartupManager.StartAgent();

            DesktopBridgeStartupCheckBox.IsChecked = requestedBridgeStartup;
            HotkeyInfoText.Text = string.IsNullOrWhiteSpace(updatedSettings.BringToFrontHotkey)
                ? "Global kısayol kapalı."
                : "Global kısayol Agent tarafından izleniyor.";
            HotkeyInfoText.Foreground = new SolidColorBrush(Colors.LightGreen);

            SetSaveStatus("Ayarlar kaydedildi.", Colors.LightGreen);
            UpdateAgentStatus();
            UpdateDesktopBridgeStatus();
            // Close only after the persistent settings and OS startup setting succeed.
            _agentTimer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<string>();

            if (settingsSaved)
            {
                try
                {
                    _settingsStore.Save(originalSettings);
                }
                catch (Exception rollbackSettingsError)
                {
                    rollbackFailures.Add("Önceki ayarlar geri yüklenemedi.");
                    DiagnosticLogManager.WriteError(
                        "MYN-SETTINGS-999",
                        "Settings",
                        "Kaydet hatasından sonra önceki ayarlar geri yüklenemedi.",
                        rollbackSettingsError);
                }
            }

            try
            {
                if (DesktopBridgeStartupManager.IsEnabled() != previousBridgeStartup)
                    DesktopBridgeStartupManager.SetEnabled(previousBridgeStartup);
            }
            catch (Exception rollbackStartupError)
            {
                rollbackFailures.Add("Önceki Bridge başlangıç ayarı geri yüklenemedi.");
                DiagnosticLogManager.WriteError(
                    "MYN-STARTUP-999",
                    "DesktopBridge",
                    "Kaydet hatasından sonra önceki Bridge başlangıç ayarı geri yüklenemedi.",
                    rollbackStartupError);
            }

            DiagnosticLogManager.WriteError(
                "MYN-SETTINGS-001",
                "Settings",
                "Ayarlar kaydedilemedi; geri alma işlemi denendi.",
                ex);

            SetSaveStatus("Kaydedilemedi.", Colors.IndianRed);
            var rollbackText = rollbackFailures.Count == 0
                ? "Önceki ayarlar korunmaya çalışıldı."
                : string.Join(Environment.NewLine, rollbackFailures);
            MessageBox.Show(
                $"Ayarlar kaydedilemedi.\n\n{ex.Message}\n\n{rollbackText}",
                "MynAssistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool IsValidHotkey(string value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "Kısayol için Ctrl / Alt / Shift / Win ve bir normal tuş gerekir.";
            return false;
        }

        var validModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ctrl", "control", "alt", "shift", "win", "windows" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!validModifiers.Contains(parts[index]) || !seen.Add(parts[index]))
            {
                error = $"Kısayol değiştiricisi geçersiz veya tekrar edilmiş: {parts[index]}";
                return false;
            }
        }

        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None ||
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            error = "Kısayolun son tuşu geçerli bir normal tuş olmalı.";
            return false;
        }

        return true;
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
        _pairingCancellation?.Cancel();
        _agentTimer.Stop();

        base.OnClosed(e);
    }
}


