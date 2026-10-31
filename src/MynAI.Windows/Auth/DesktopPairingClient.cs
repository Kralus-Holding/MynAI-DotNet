using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MynAI.Windows.Auth;

public sealed class DesktopPairingClient
{
    private const string ServerBaseUrl = "https://mynassistant-server.onrender.com";
    // Firebase Web API key is a public project identifier already used by the shipped bridge;
    // it is not a service-account secret. Server/service credentials remain server-side.
    private const string FirebaseWebApiKey = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM";
    private const string FirebaseSecureTokenUrl = "https://securetoken.googleapis.com/v1/token";
    private const string FirebaseAccountsLookupUrl = "https://identitytoolkit.googleapis.com/v1/accounts:lookup";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string> GetFreshIdTokenAsync(
        FirebaseDesktopSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.Uid) || string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new DesktopPairingClientException(
                "Kaydedilmiş Windows oturumu yenilenemiyor. Android uygulamasında oturumu yeniden eşleştir.",
                HttpStatusCode.Unauthorized,
                "MYN-AUTH-004");

        // The Desktop Bridge stores the DPAPI-protected refresh token but may leave the
        // short-lived ID token empty/stale. Refresh it before binding a new pairing to this UID.
        using var refreshRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FirebaseSecureTokenUrl}?key={Uri.EscapeDataString(FirebaseWebApiKey)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken
            })
        };
        using var refreshResponse = await Http.SendAsync(refreshRequest, cancellationToken);
        if (!refreshResponse.IsSuccessStatusCode)
        {
            var code = refreshResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                ? "MYN-AUTH-004"
                : "MYN-SERVER-001";
            throw new DesktopPairingClientException(
                "Windows oturumu doğrulanamadı veya yenilenemedi. İnternet bağlantısını kontrol et; oturum geçersizse 'Hesabı değiştir' akışını kullan.",
                refreshResponse.StatusCode,
                code);
        }

        using var refreshDocument = await JsonDocument.ParseAsync(
            await refreshResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var refreshRoot = refreshDocument.RootElement;
        var idToken = GetJsonString(refreshRoot, "id_token");
        var refreshedToken = GetJsonString(refreshRoot, "refresh_token");
        var expiresIn = GetJsonString(refreshRoot, "expires_in");
        if (string.IsNullOrWhiteSpace(idToken) ||
            string.IsNullOrWhiteSpace(refreshedToken) ||
            !long.TryParse(expiresIn, out var lifetimeSeconds) ||
            lifetimeSeconds <= 0)
        {
            throw new InvalidDataException("Firebase oturum yenileme yanıtı beklenen alanları içermiyor.");
        }

        using var lookupRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FirebaseAccountsLookupUrl}?key={Uri.EscapeDataString(FirebaseWebApiKey)}")
        {
            Content = JsonContent.Create(new { idToken })
        };
        using var lookupResponse = await Http.SendAsync(lookupRequest, cancellationToken);
        if (!lookupResponse.IsSuccessStatusCode)
            throw new DesktopPairingClientException(
                "Yenilenen Windows oturumu doğrulanamadı. Oturum değiştirilmedi.",
                lookupResponse.StatusCode,
                "MYN-AUTH-004");

        using var lookupDocument = await JsonDocument.ParseAsync(
            await lookupResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var users = lookupDocument.RootElement.TryGetProperty("users", out var userList) &&
                    userList.ValueKind == JsonValueKind.Array
            ? userList
            : default;
        var resolvedUid = users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0
            ? GetJsonString(users[0], "localId")
            : null;
        if (!StringComparer.Ordinal.Equals(resolvedUid, session.Uid))
            throw new DesktopPairingClientException(
                "Yenilenen Windows oturumu farklı bir kullanıcıya ait. Güvenlik nedeniyle eşleştirme başlatılmadı.",
                HttpStatusCode.Unauthorized,
                "MYN-AUTH-005");

        session.IdToken = idToken;
        session.RefreshToken = refreshedToken;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds - Math.Min(60, lifetimeSeconds / 10)).ToUnixTimeSeconds();
        FirebaseDesktopSessionStore.Save(session);
        return idToken;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    public async Task<DesktopPairingStartResponse> StartAsync(
        string machineName,
        string? currentSessionIdToken,
        CancellationToken cancellationToken = default)
    {
        var payload = new DesktopPairingStartRequest(
            string.IsNullOrWhiteSpace(machineName) ? "Windows bilgisayarı" : machineName.Trim());
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ServerBaseUrl}/api/v1/desktop/pairing/start")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(currentSessionIdToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentSessionIdToken);

        using var response = await Http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await CreateSafeExceptionAsync(response, cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<DesktopPairingStartResponse>(
            JsonOptions,
            cancellationToken);
        if (result is null ||
            string.IsNullOrWhiteSpace(result.PairingId) ||
            string.IsNullOrWhiteSpace(result.PollSecret) ||
            string.IsNullOrWhiteSpace(result.Code) ||
            result.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException(
                "Sunucu geçerli bir eşleştirme kodu döndürmedi. Daha sonra tekrar dene.");
        }

        return result;
    }

    public async Task<DesktopPairingPollResponse> PollAsync(
        string pairingId,
        string pollSecret,
        CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync(
            $"{ServerBaseUrl}/api/v1/desktop/pairing/poll",
            new DesktopPairingPollRequest(pairingId, pollSecret),
            JsonOptions,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<DesktopPairingPollResponse>(
                JsonOptions,
                cancellationToken);
            if (result is null ||
                (string.Equals(result.State, "approved", StringComparison.OrdinalIgnoreCase) &&
                 (result.Session is null ||
                  string.IsNullOrWhiteSpace(result.Session.Uid) ||
                  string.IsNullOrWhiteSpace(result.Session.RefreshToken) ||
                  string.IsNullOrWhiteSpace(result.Session.IdToken))))
            {
                throw new InvalidDataException(
                    "Sunucudan beklenen eşleştirme durumu alınamadı. İşlem güvenlik nedeniyle tamamlanmadı.");
            }

            return result;
        }

        throw await CreateSafeExceptionAsync(response, cancellationToken);
    }

    private static async Task<DesktopPairingClientException> CreateSafeExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? detail = null;
        string? serverCode = null;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detailValue) &&
                detailValue.ValueKind == JsonValueKind.String)
                detail = detailValue.GetString();
            if (root.TryGetProperty("code", out var codeValue) &&
                codeValue.ValueKind == JsonValueKind.String)
                serverCode = codeValue.GetString();
            if (string.IsNullOrWhiteSpace(serverCode) &&
                root.TryGetProperty("extensions", out var extensions) &&
                extensions.ValueKind == JsonValueKind.Object &&
                extensions.TryGetProperty("code", out var nestedCode) &&
                nestedCode.ValueKind == JsonValueKind.String)
                serverCode = nestedCode.GetString();
        }
        catch (JsonException)
        {
            // Some reverse proxies return an HTML 404 page; never show its raw body.
        }

        var (message, fallbackCode) = response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                ("Sunucuda telefon-PC eşleştirme API'si henüz yayımlanmamış (HTTP 404). Sunucu dağıtımı tamamlanmalı.", "MYN-SERVER-404"),
            HttpStatusCode.MethodNotAllowed =>
                ("Sunucudaki telefon-PC eşleştirme uç noktası bu isteği kabul etmiyor (HTTP 405). Sunucu sürümünü kontrol et.", "MYN-SERVER-405"),
            HttpStatusCode.TooManyRequests =>
                ("Çok fazla eşleştirme isteği yapıldı. Biraz bekleyip tekrar dene.", "MYN-SERVER-429"),
            HttpStatusCode.Unauthorized =>
                ("Eşleştirme oturumu doğrulanamadı. Yeni bir kod alıp tekrar dene.", "MYN-SERVER-401"),
            HttpStatusCode.Gone =>
                ("Eşleştirme kodunun süresi doldu. Yeniden eşleştirme başlat.", "MYN-DEVICE-003"),
            HttpStatusCode.ServiceUnavailable =>
                (string.IsNullOrWhiteSpace(detail)
                    ? "Telefon-PC eşleştirmesi sunucuda henüz yapılandırılmamış."
                    : detail, "MYN-SERVER-503"),
            _ =>
                (string.IsNullOrWhiteSpace(detail)
                    ? $"Sunucu eşleştirmeyi tamamlayamadı (HTTP {(int)response.StatusCode})."
                    : detail, "MYN-SERVER-999")
        };

        // Prefer the server's stable error code except for 404/405, where the
        // actionable diagnosis is that this endpoint isn't deployed/available.
        var errorCode = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            ? fallbackCode
            : !string.IsNullOrWhiteSpace(serverCode) &&
              serverCode.StartsWith("MYN-", StringComparison.Ordinal)
                ? serverCode
                : fallbackCode;

        return new DesktopPairingClientException(message, response.StatusCode, errorCode);
    }
}

public sealed class DesktopPairingClientException : InvalidOperationException
{
    public DesktopPairingClientException(
        string message,
        HttpStatusCode statusCode,
        string errorCode) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
}

public sealed record DesktopPairingStartRequest(string MachineName);
public sealed record DesktopPairingPollRequest(string PairingId, string PollSecret);

public sealed class DesktopPairingStartResponse
{
    public string PairingId { get; set; } = "";
    public string PollSecret { get; set; } = "";
    public string Code { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string QrSignature { get; set; } = "";
}

public sealed class DesktopPairingPollResponse
{
    public string State { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public FirebaseDesktopSession? Session { get; set; }
}
