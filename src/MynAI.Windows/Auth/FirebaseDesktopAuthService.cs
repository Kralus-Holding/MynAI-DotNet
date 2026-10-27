using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MynAI.Windows.Auth;

public sealed class FirebaseDesktopAuthService
{
    // Public Firebase client configuration also used by the Android app.
    // This is a Firebase client API key, not a developer account credential.
    private const string ApiKey = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM";
    private const string AccountVerificationUrl = "https://mynassistant-server.onrender.com/api/v1/account/email/verification";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<FirebaseDesktopSession> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        email = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("E-posta ve şifre alanlarını doldurun.");

        using var response = await Http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={ApiKey}",
            new SignInRequest(email, password, true),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await TranslateErrorAsync(response, cancellationToken));

        var result = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: cancellationToken);
        return CreateSession(result, email);
    }

    public async Task<FirebaseDesktopSession> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        email = NormalizeEmail(email);
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new InvalidOperationException("Şifre en az 8 karakter olmalıdır.");

        using var response = await Http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={ApiKey}",
            new SignInRequest(email, password, true),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await TranslateErrorAsync(response, cancellationToken));

        var result = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: cancellationToken);
        return CreateSession(result, email);
    }

    public async Task SendPasswordResetAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        email = NormalizeEmail(email);
        using var response = await Http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={ApiKey}",
            new PasswordResetRequest(email, "PASSWORD_RESET"),
            cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        var code = await ReadErrorCodeAsync(response, cancellationToken);
        // Return the same visible result for an unknown account to avoid exposing
        // whether an e-mail address has a MynAssistant account.
        if (string.Equals(code, "EMAIL_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "USER_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(code, "INVALID_EMAIL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geçerli bir e-posta adresi girin.");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
            string.Equals(code, "TOO_MANY_ATTEMPTS_TRY_LATER", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Çok fazla şifre sıfırlama isteği yapıldı. Daha sonra yeniden deneyin.");

        throw new InvalidOperationException(
            "Şifre sıfırlama isteği gönderilemedi. Firebase e-posta ayarlarını ve internet bağlantısını kontrol edin.");
    }

    public async Task SendEmailVerificationAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            throw new InvalidOperationException("E-posta doğrulaması için geçerli bir oturum gerekli.");

        using var request = new HttpRequestMessage(HttpMethod.Post, AccountVerificationUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        request.Content = JsonContent.Create(new Dictionary<string, string>());
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("detail", out var value) &&
                value.ValueKind == JsonValueKind.String)
                detail = value.GetString();
        }
        catch (JsonException)
        {
            // Do not show raw proxy/response bodies to the user.
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Doğrulama e-postası gönderilemedi (HTTP {(int)response.StatusCode})."
                : detail);
    }

    private static string NormalizeEmail(string email)
    {
        email = email?.Trim() ?? string.Empty;
        if (!MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geçerli bir e-posta adresi girin.");
        return address.Address;
    }

    private static FirebaseDesktopSession CreateSession(AuthResponse? result, string fallbackEmail)
    {
        if (result is null ||
            string.IsNullOrWhiteSpace(result.localId) ||
            string.IsNullOrWhiteSpace(result.refreshToken) ||
            string.IsNullOrWhiteSpace(result.idToken))
            throw new InvalidOperationException("Firebase geçerli bir oturum döndürmedi. Yeniden deneyin.");

        if (!long.TryParse(result.expiresIn, out var expiresIn))
            expiresIn = 3600;

        return new FirebaseDesktopSession
        {
            Uid = result.localId,
            Email = string.IsNullOrWhiteSpace(result.email) ? fallbackEmail : result.email,
            DisplayName = string.IsNullOrWhiteSpace(result.displayName)
                ? fallbackEmail.Split('@')[0]
                : result.displayName,
            RefreshToken = result.refreshToken,
            IdToken = result.idToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)).ToUnixTimeSeconds()
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string> TranslateErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var code = await ReadErrorCodeAsync(response, cancellationToken);
        return code?.ToUpperInvariant() switch
        {
            "INVALID_LOGIN_CREDENTIALS" or "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" =>
                "E-posta veya şifre hatalı.",
            "EMAIL_EXISTS" => "Bu e-posta adresi zaten kayıtlı. Giriş yapmayı veya şifre sıfırlamayı deneyin.",
            "WEAK_PASSWORD" => "Şifre en az 8 karakter olmalıdır.",
            "USER_DISABLED" => "Bu hesap devre dışı bırakılmış. Hesap yöneticisiyle iletişime geçin.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" =>
                "Çok fazla deneme yapıldı. Bir süre bekleyip yeniden deneyin.",
            "INVALID_EMAIL" => "Geçerli bir e-posta adresi girin.",
            "OPERATION_NOT_ALLOWED" =>
                "Bu Firebase projesinde e-posta/şifre girişi etkin değil. Firebase Authentication ayarlarını kontrol edin.",
            _ => response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Çok fazla istek yapıldı. Daha sonra yeniden deneyin."
                : "Firebase isteği tamamlanamadı. İnternet bağlantısını ve Firebase Authentication ayarlarını kontrol edin."
        };
    }

    private sealed record SignInRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken);

    private sealed record PasswordResetRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("requestType")] string RequestType);

    private sealed class AuthResponse
    {
        public string localId { get; set; } = "";
        public string email { get; set; } = "";
        public string displayName { get; set; } = "";
        public string idToken { get; set; } = "";
        public string refreshToken { get; set; } = "";
        public string expiresIn { get; set; } = "3600";
    }
}
