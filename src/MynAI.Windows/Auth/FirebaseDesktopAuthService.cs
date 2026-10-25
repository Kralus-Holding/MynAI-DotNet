using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MynAI.Windows.Auth;

public sealed class FirebaseDesktopAuthService
{
    // Public Firebase client configuration also used by the Android app.
    private const string ApiKey = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<FirebaseDesktopSession> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("E-posta ve şifre alanlarını doldurun.");

        using var response = await Http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={ApiKey}",
            new SignInRequest(email, password, true),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await TranslateErrorAsync(response, cancellationToken));

        var result = await response.Content.ReadFromJsonAsync<SignInResponse>(cancellationToken: cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.localId)
            || string.IsNullOrWhiteSpace(result.refreshToken) || string.IsNullOrWhiteSpace(result.idToken))
            throw new InvalidOperationException("Firebase geçerli bir oturum döndürmedi. Yeniden deneyin.");

        if (!long.TryParse(result.expiresIn, out var expiresIn)) expiresIn = 3600;
        return new FirebaseDesktopSession
        {
            Uid = result.localId,
            Email = string.IsNullOrWhiteSpace(result.email) ? email : result.email,
            DisplayName = string.IsNullOrWhiteSpace(result.displayName) ? email.Split('@')[0] : result.displayName,
            RefreshToken = result.refreshToken,
            IdToken = result.idToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)).ToUnixTimeSeconds()
        };
    }

    private static async Task<string> TranslateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            code = document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch
        {
            // Never display raw authentication responses or tokens.
        }

        return code?.ToUpperInvariant() switch
        {
            "INVALID_LOGIN_CREDENTIALS" or "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" => "E-posta veya şifre hatalı.",
            "USER_DISABLED" => "Bu hesap devre dışı bırakılmış. Hesap yöneticisiyle iletişime geçin.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Çok fazla deneme yapıldı. Bir süre bekleyip yeniden deneyin.",
            "INVALID_EMAIL" => "Geçerli bir e-posta adresi girin.",
            "OPERATION_NOT_ALLOWED" => "Bu Firebase projesinde e-posta/şifre girişi etkin değil. Firebase Authentication ayarlarını kontrol edin.",
            _ => response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Çok fazla giriş denemesi yapıldı. Daha sonra yeniden deneyin."
                : "Firebase hesabına giriş yapılamadı. İnternet bağlantınızı ve hesap bilgilerinizi kontrol edin."
        };
    }

    private sealed record SignInRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken);

    private sealed class SignInResponse
    {
        public string localId { get; set; } = "";
        public string email { get; set; } = "";
        public string displayName { get; set; } = "";
        public string idToken { get; set; } = "";
        public string refreshToken { get; set; } = "";
        public string expiresIn { get; set; } = "3600";
    }
}
