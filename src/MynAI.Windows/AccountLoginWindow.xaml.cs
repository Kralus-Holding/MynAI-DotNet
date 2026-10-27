using System.Windows;
using System.Windows.Media;
using MynAI.Windows.Auth;

namespace MynAI.Windows;

public partial class AccountLoginWindow : Window
{
    private readonly FirebaseDesktopAuthService _authService = new();
    private bool _registerMode;
    private bool _busy;

    public FirebaseDesktopSession? AuthenticatedSession { get; private set; }
    public bool PhoneApprovalRequested { get; private set; }
    public string? StatusMessage { get; private set; }

    public AccountLoginWindow()
    {
        InitializeComponent();
        UpdateMode();
    }

    private void RegisterMode_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _registerMode = true;
        UpdateMode();
    }

    private void LoginMode_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _registerMode = false;
        UpdateMode();
    }

    private void UpdateMode()
    {
        ModeTitleText.Text = _registerMode ? "Yeni hesap oluştur" : "Giriş yap";
        ModeDescriptionText.Text = _registerMode
            ? "Yeni MynAssistant hesabın için bir e-posta adresi ve en az 8 karakterli şifre belirle."
            : "MynAssistant hesabınla Windows uygulamasına giriş yap.";
        SubmitButton.Content = _registerMode ? "Hesap oluştur" : "Giriş yap";
        ConfirmPasswordPanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        RegisterModeButton.Visibility = _registerMode ? Visibility.Collapsed : Visibility.Visible;
        LoginModeButton.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        ForgotPasswordButton.Visibility = _registerMode ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_registerMode && !string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ShowStatus("Girilen şifreler birbiriyle eşleşmiyor.", isError: true);
            return;
        }

        FirebaseDesktopSession? session = null;
        try
        {
            SetBusy(true);
            session = _registerMode
                ? await _authService.RegisterAsync(EmailTextBox.Text, PasswordBox.Password)
                : await _authService.SignInAsync(EmailTextBox.Text, PasswordBox.Password);

            if (_registerMode)
            {
                StatusMessage = "Hesap oluşturuldu.";
                try
                {
                    await _authService.SendEmailVerificationAsync(session.IdToken);
                    StatusMessage += " E-posta doğrulama bağlantısı gönderildi.";
                }
                catch (Exception ex)
                {
                    // Account creation succeeded; surface the separate mail failure rather
                    // than discarding the session or pretending that verification was sent.
                    StatusMessage += $" Ancak doğrulama e-postası gönderilemedi: {ex.Message}";
                }
            }
            else
            {
                StatusMessage = "Firebase hesabına giriş başarılı.";
            }

            AuthenticatedSession = session;
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }

        if (AuthenticatedSession is not null)
            DialogResult = true;
    }

    private async void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            SetBusy(true);
            await _authService.SendPasswordResetAsync(EmailTextBox.Text);
            ShowStatus(
                "Bu e-posta adresi bir hesaba bağlıysa şifre sıfırlama bağlantısı gönderildi.",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        FirebaseDesktopSession? session = null;
        try
        {
            SetBusy(true);
            session = await new FirebaseGoogleSignInService().SignInAsync();
            StatusMessage = "Google hesabı Firebase ile doğrulandı.";
            AuthenticatedSession = session;
        }
        catch (FirebaseGoogleSignInException ex)
        {
            // Keep OAuth diagnostics actionable without persisting raw provider
            // responses, authorization codes, ID tokens, or refresh tokens.
            DiagnosticLogManager.WriteError(
                ex.ErrorCode,
                "GoogleOAuth",
                ex.Message,
                new InvalidOperationException(ex.ErrorCode));
            ShowStatus($"{ex.Message} ({ex.ErrorCode})", isError: true);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Google giriş işlemi iptal edildi.", isError: false);
        }
        catch (Exception ex)
        {
            var details = ex.ToString();
            var accessDenied = details.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
                               details.Contains("test users", StringComparison.OrdinalIgnoreCase) ||
                               details.Contains("test kullanıcı", StringComparison.OrdinalIgnoreCase) ||
                               details.Contains("403", StringComparison.OrdinalIgnoreCase);
            var errorCode = accessDenied ? "MYN-OAUTH-403" : "MYN-OAUTH-999";
            var safeMessage = accessDenied
                ? "Google OAuth erişimi 403 access_denied ile reddedildi. Google Cloud Console → Google Auth Platform → Audience bölümünde Testing/Production ve Test users ayarlarını kontrol et. Bu değişiklik Google Cloud Console'da yapılmalıdır."
                : "Google girişi tamamlanamadı. OAuth yapılandırmasını ve Firebase Authentication Google sağlayıcısını kontrol et.";
            DiagnosticLogManager.WriteError(
                errorCode,
                "GoogleOAuth",
                safeMessage,
                new InvalidOperationException(ex.GetType().Name));
            ShowStatus($"{safeMessage} ({errorCode})", isError: true);
        }
        finally
        {
            SetBusy(false);
        }

        if (AuthenticatedSession is not null)
            DialogResult = true;
    }

    private void PhoneApproval_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PhoneApprovalRequested = true;
        StatusMessage = "Android uygulamasındaki mevcut MynAssistant oturumu üzerinden bilgisayar onayı başlatılacak.";
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DialogResult = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SubmitButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "İşleniyor…" : (_registerMode ? "Hesap oluştur" : "Giriş yap");
        RegisterModeButton.IsEnabled = !busy;
        LoginModeButton.IsEnabled = !busy;
        ForgotPasswordButton.IsEnabled = !busy;
        PhoneApprovalButton.IsEnabled = !busy;
        GoogleSignInButton.IsEnabled = !busy;
        EmailTextBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmPasswordBox.IsEnabled = !busy;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(
            isError ? System.Windows.Media.Color.FromRgb(255, 145, 145) : System.Windows.Media.Color.FromRgb(151, 226, 190));
        StatusText.Visibility = Visibility.Visible;
    }
}
