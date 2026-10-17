using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MynAI.Windows.Google;

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

        _googleOAuthService =
            new GoogleOAuthService();

        _googleAccountStore =
            new GoogleAccountStore();

        _settingsStore =
            new MynAISettingsStore();

        LoadSettings();
        LoadGoogleAccount();

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

    private void LoadSettings()
    {
        var settings =
            _settingsStore.Load();

        NotificationsCheckBox.IsChecked =
            settings.WindowsNotifications;

        AgentStartupCheckBox.IsChecked =
            AgentStartupManager.IsEnabled();

        AgentStartupGeneralCheckBox.IsChecked =
            AgentStartupManager.IsEnabled();

        DesktopBridgeStartupCheckBox.IsChecked =
            DesktopBridgeStartupManager.IsEnabled();

        MicrophoneCheckBox.IsChecked =
            settings.MicrophoneEnabled;

        CameraCheckBox.IsChecked =
            settings.CameraEnabled;

        HotkeyTextBox.Text =
            settings.BringToFrontHotkey;
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
            "Bağlı";
    }

    private void ShowPage(
        UIElement page)
    {
        GeneralPanel.Visibility =
            Visibility.Collapsed;

        AgentPanel.Visibility =
            Visibility.Collapsed;

        AccountPanel.Visibility =
            Visibility.Collapsed;

        PrivacyPanel.Visibility =
            Visibility.Collapsed;

        ShortcutPanel.Visibility =
            Visibility.Collapsed;

        page.Visibility =
            Visibility.Visible;
    }

    private void GeneralNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(
            GeneralPanel);
    }

    private void AgentNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(
            AgentPanel);

        UpdateAgentStatus();
    }

    private void AccountNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(
            AccountPanel);
    }

    private void PrivacyNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(
            PrivacyPanel);
    }

    private void ShortcutNav_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowPage(
            ShortcutPanel);
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

    private void UpdateDesktopBridgeStatus()
    {
        var running = DesktopBridgeStartupManager.IsRunning();
        var executablePath = DesktopBridgeStartupManager.TryGetExecutablePath();
        var oauthPath = DesktopBridgeStartupManager.GetOAuthConfigPath();
        var hasOAuthConfig = File.Exists(oauthPath);

        DesktopBridgeStatusText.Text = running
            ? "Çalışıyor"
            : executablePath is null
                ? "Bridge bileşeni bulunamadı"
                : !hasOAuthConfig
                    ? "Google OAuth yapılandırması eksik"
                    : "Hazır — bağlantı başlatılmadı";

        DesktopBridgeStatusText.Foreground =
            new SolidColorBrush(
                running
                    ? Colors.LightGreen
                    : executablePath is not null && hasOAuthConfig
                        ? Colors.Goldenrod
                        : Colors.IndianRed);

        DesktopBridgeStatusDot.Fill =
            new SolidColorBrush(
                running
                    ? Colors.LimeGreen
                    : executablePath is not null && hasOAuthConfig
                        ? Colors.Goldenrod
                        : Colors.DimGray);

        DesktopBridgeExecutablePathText.Text =
            executablePath ?? "MynAssistantDesktopBridge.exe bulunamadı.";

        DesktopBridgeOAuthPathText.Text = hasOAuthConfig
            ? "Google OAuth yapılandırması bulundu."
            : $"OAuth dosyası bulunamadı: {oauthPath}";
    }

    private void StartDesktopBridge_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            DesktopBridgeStartupManager.StartAgent();
            UpdateDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Telefon-PC bağlantısı başlatılamadı:\\n\\n{ex.Message}",
                "MynAI",
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
        try
        {
            DesktopBridgeStartupManager.RestartAgent();
            UpdateDesktopBridgeStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Telefon-PC bağlantısı yeniden başlatılamadı:\\n\\n{ex.Message}",
                "MynAI",
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
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent başlatılamadı:\n\n{ex.Message}",
                "MynAI",
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
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Agent yeniden başlatılamadı:\n\n{ex.Message}",
                "MynAI",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void GoogleConnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
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
                "Google ile bağlan";

            MessageBox.Show(
                $"Google bağlantısı başarısız:\n\n{ex.Message}",
                "MynAI",
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
            var startAgent =
                AgentStartupCheckBox.IsChecked == true;

            AgentStartupCheckBox.IsChecked =
                startAgent;

            AgentStartupGeneralCheckBox.IsChecked =
                startAgent;

            AgentStartupManager.SetEnabled(
                startAgent);

            var startDesktopBridge =
                DesktopBridgeStartupCheckBox.IsChecked == true;

            DesktopBridgeStartupManager.SetEnabled(
                startDesktopBridge);

            var settings =
                _settingsStore.Load();

            settings.StartAgentWithWindows =
                startAgent;

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
                "MynAI",
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
                "MynAI",
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


