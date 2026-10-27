using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;

namespace MynAI.Windows.Auth;

/// <summary>
/// Obtains a Google OpenID token with sign-in-only scopes, then exchanges it for
/// a Firebase session. Contacts/Gmail/Drive scopes are deliberately not requested.
/// </summary>
public sealed class FirebaseGoogleSignInService
{
    private const string ApiKey = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM";
    private const string SignInUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp";
    private static readonly string[] Scopes = ["openid", "email", "profile"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // This is application/operator-provisioned OAuth configuration, not the
    // optional Google Contacts configuration and not a file each user must import.
    public static string ApplicationOAuthConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MynAssistant", "Auth", "google-signin-desktop.json");

    public async Task<FirebaseGoogleSignInResult> SignInAsync(
        bool allowAccountCreation,
        CancellationToken cancellationToken = default)
    {
        var configPath = ApplicationOAuthConfigPath;
        if (!File.Exists(configPath))
        {
            throw new FirebaseGoogleSignInException(
                "MYN-OAUTH-001",
                "Google ile giriş henüz bu kurulum için yapılandırılmamış (MYN-OAUTH-001). MynAssistant dağıtım sorumlusu, Google Cloud Console'da uygun Desktop app OAuth istemcisi ve Firebase Google sağlayıcısını yapılandırıp istemci JSON'unu bu bilgisayarda ProgramData\MynAssistant\Auth\google-signin-desktop.json konumuna kurmalıdır. Bu kurulum kullanıcının Google Kişiler bağlantısını kullanmaz ve her kullanıcıdan OAuth JSON dosyası istemez.");
        }

        Google.Apis.Auth.OAuth2.UserCredential credential;
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Use a fresh in-memory store for each explicit sign-in. Google OAuth
            // refresh/access tokens are never persisted to disk by this flow.
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                "mynassistant-firebase-signin",
                cancellationToken,
                new InMemoryOAuthDataStore());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            throw new FirebaseGoogleSignInException(
                "MYN-OAUTH-403",
                "Google OAuth erişimi 403 access_denied ile reddedildi. Google Cloud Console → Google Auth Platform → Audience bölümünde uygulamanın Testing/Production durumunu ve Test users listesini kontrol et. Tüm kullanıcılar için yayın/doğrulama gerekiyorsa bunu Google Cloud Console'da tamamlamak gerekir; yeniden derlemek tek başına yetmez.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            throw new FirebaseGoogleSignInException(
                "MYN-OAUTH-001",
                "Google masaüstü OAuth yapılandırması okunamadı. Desktop app OAuth JSON dosyasını doğrula ve tekrar dene.");
        }

        var googleIdToken = credential.Token.IdToken;
        if (string.IsNullOrWhiteSpace(googleIdToken))
        {
            throw new FirebaseGoogleSignInException(
                "MYN-OAUTH-002",
                "Google OAuth yanıtında OpenID id_token yok. OAuth istemcisinin openid scope'u desteklediğini ve yapılandırmanın Desktop app türünde olduğunu kontrol et.");
        }

        var postBody = "id_token=" + Uri.EscapeDataString(googleIdToken) +
                       "&providerId=" + Uri.EscapeDataString("google.com");
        using var response = await Http.PostAsJsonAsync(
            $"{SignInUrl}?key={ApiKey}",
            new FirebaseIdpRequest(postBody, "http://localhost", true, true, allowAccountCreation),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await CreateFirebaseErrorAsync(response, cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<FirebaseIdpResponse>(
            cancellationToken: cancellationToken);
        if (result is null ||
            string.IsNullOrWhiteSpace(result.localId) ||
            string.IsNullOrWhiteSpace(result.idToken) ||
            string.IsNullOrWhiteSpace(result.refreshToken))
        {
            throw new FirebaseGoogleSignInException(
                "MYN-AUTH-006",
                "Firebase, Google girişini tamamlayamadı veya geçerli bir oturum döndürmedi.");
        }

        if (result.needConfirmation)
        {
            throw new FirebaseGoogleSignInException(
                "MYN-AUTH-007",
                "Bu Google e-postası başka bir giriş yöntemiyle kayıtlı. Mevcut giriş yöntemiyle oturum açıp hesapları güvenli şekilde bağlamak gerekiyor.");
        }

        if (!long.TryParse(result.expiresIn, out var expiresIn))
            expiresIn = 3600;

        var session = new FirebaseDesktopSession
        {
            Uid = result.localId,
            Email = result.email ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(result.displayName)
                ? (result.email?.Split('@').FirstOrDefault() ?? "Google Kullanıcısı")
                : result.displayName,
            PhotoUrl = GetSafePhotoUrl(result.photoUrl),
            IdToken = result.idToken,
            RefreshToken = result.refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)).ToUnixTimeSeconds()
        };
        return new FirebaseGoogleSignInResult(session, result.isNewUser);
    }

    private static bool IsAccessDenied(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("test users", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("test kullanıcı", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("403", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSafePhotoUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;

    private static async Task<FirebaseGoogleSignInException> CreateFirebaseErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? errorCode = null;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                errorCode = message.GetString();
        }
        catch (JsonException)
        {
            // Do not expose raw Firebase/proxy response bodies or credentials.
        }

        return errorCode?.ToUpperInvariant() switch
        {
            "OPERATION_NOT_ALLOWED" => new(
                "MYN-OAUTH-004",
                "Bu Firebase projesinde Google sağlayıcısı etkin değil. Firebase Console → Authentication → Sign-in method bölümünde Google sağlayıcısını yapılandırmak gerekiyor."),
            "INVALID_IDP_RESPONSE" or "INVALID_CREDENTIAL" => new(
                "MYN-OAUTH-005",
                "Google kimlik bilgisi Firebase tarafından kabul edilmedi. Firebase Google sağlayıcı ayarını ve OAuth istemci kimliğini kontrol et."),
            "FEDERATED_USER_ID_NOT_FOUND" or "EMAIL_NOT_FOUND" or "USER_NOT_FOUND" => new(
                "MYN-AUTH-008",
                "Bu Google hesabıyla kayıtlı bir MynAssistant hesabı bulunamadı. Yeni hesap oluşturmak için kayıt modunu seç."),
            "EMAIL_EXISTS" or "FEDERATED_USER_ID_ALREADY_LINKED" => new(
                "MYN-AUTH-007",
                "Bu Google e-postası farklı bir giriş yöntemiyle kayıtlı. Mevcut giriş yöntemiyle oturum açıp hesapları güvenli şekilde bağlamak gerekiyor."),
            "TOO_MANY_ATTEMPTS_TRY_LATER" => new(
                "MYN-OAUTH-006",
                "Çok fazla Google giriş denemesi yapıldı. Bir süre bekleyip yeniden dene."),
            _ => new(
                "MYN-OAUTH-999",
                $"Firebase Google girişi tamamlanamadı (HTTP {(int)response.StatusCode}). Firebase Authentication ve Google sağlayıcı ayarlarını kontrol et.")
        };
    }

    private sealed class InMemoryOAuthDataStore : IDataStore
    {
        private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);

        public Task StoreAsync<T>(string key, T value)
        {
            _values[key] = value!;
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is T typed)
                return Task.FromResult(typed);
            return Task.FromResult(default(T)!);
        }

        public Task ClearAsync()
        {
            _values.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed record FirebaseIdpRequest(
        [property: JsonPropertyName("postBody")] string PostBody,
        [property: JsonPropertyName("requestUri")] string RequestUri,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken,
        [property: JsonPropertyName("returnIdpCredential")] bool ReturnIdpCredential,
        [property: JsonPropertyName("autoCreate")] bool AutoCreate);

    private sealed class FirebaseIdpResponse
    {
        public string localId { get; set; } = "";
        public string? email { get; set; }
        public string? displayName { get; set; }
        public string? photoUrl { get; set; }
        public string idToken { get; set; } = "";
        public string refreshToken { get; set; } = "";
        public string expiresIn { get; set; } = "3600";
        public bool isNewUser { get; set; }
        public bool needConfirmation { get; set; }
    }
}

public sealed record FirebaseGoogleSignInResult(FirebaseDesktopSession Session, bool IsNewUser);

public sealed class FirebaseGoogleSignInException : InvalidOperationException
{
    public FirebaseGoogleSignInException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
