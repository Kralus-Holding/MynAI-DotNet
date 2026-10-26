using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Json;
using System.Text.Json;

namespace MynAI.Windows.Auth;

public sealed class DesktopPairingClient
{
    private const string ServerBaseUrl = "https://mynassistant-server.onrender.com";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DesktopPairingStartResponse> StartAsync(
        string machineName,
        CancellationToken cancellationToken = default)
    {
        var request = new DesktopPairingStartRequest(
            string.IsNullOrWhiteSpace(machineName) ? "Windows bilgisayarı" : machineName.Trim());

        using var response = await Http.PostAsJsonAsync(
            $"{ServerBaseUrl}/api/v1/desktop/pairing/start",
            request,
            JsonOptions,
            cancellationToken);

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
                (result.State.Equals("approved", StringComparison.OrdinalIgnoreCase) &&
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

    private static async Task<InvalidOperationException> CreateSafeExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? detail = null;
        string? code = null;
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
                code = codeValue.GetString();
            if (string.IsNullOrWhiteSpace(code) &&
                root.TryGetProperty("extensions", out var extensions) &&
                extensions.ValueKind == JsonValueKind.Object &&
                extensions.TryGetProperty("code", out var nestedCode) &&
                nestedCode.ValueKind == JsonValueKind.String)
                code = nestedCode.GetString();
        }
        catch (JsonException)
        {
            // Do not expose raw server response bodies.
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                "Çok fazla eşleştirme isteği yapıldı. Biraz bekleyip tekrar dene.",
            HttpStatusCode.Unauthorized =>
                "Eşleştirme oturumu doğrulanamadı. Yeni bir kod alıp tekrar dene.",
            HttpStatusCode.Gone =>
                "Eşleştirme kodunun süresi doldu. Yeniden eşleştirme başlat.",
            HttpStatusCode.ServiceUnavailable =>
                string.IsNullOrWhiteSpace(detail)
                    ? "Telefon-PC eşleştirmesi sunucuda henüz yapılandırılmamış."
                    : detail,
            _ => string.IsNullOrWhiteSpace(detail)
                ? $"Sunucu eşleştirmeyi tamamlayamadı (HTTP {(int)response.StatusCode})."
                : detail
        };

        if (!string.IsNullOrWhiteSpace(code) && code.StartsWith("MYN-", StringComparison.Ordinal))
            message += $" ({code})";

        return new InvalidOperationException(message);
    }
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
}

public sealed class DesktopPairingPollResponse
{
    public string State { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public FirebaseDesktopSession? Session { get; set; }
}
