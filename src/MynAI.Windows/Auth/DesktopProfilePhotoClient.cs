using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MynAI.Windows.Auth;

public sealed class DesktopProfilePhotoClient
{
    private const int MaximumPhotoBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(70)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly DesktopPairingClient _pairingClient = new();

    public async Task<FirebaseDesktopSession> UploadAsync(
        FirebaseDesktopSession session,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.Uid))
            throw new InvalidOperationException("Profil fotoğrafı yüklemek için önce MynAssistant hesabına giriş yap.");
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Seçilen profil fotoğrafı bulunamadı.", filePath);

        var info = new FileInfo(filePath);
        if (info.Length is <= 0 or > MaximumPhotoBytes)
            throw new InvalidDataException("Profil fotoğrafı boş olamaz ve 5 MB sınırını aşamaz.");

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => throw new InvalidDataException("Yalnızca JPG, PNG veya WebP profil fotoğrafı seçebilirsin.")
        };

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        ValidateImageSignature(bytes, contentType);

        var idToken = await _pairingClient.GetFreshIdTokenAsync(session, cancellationToken);
        var signature = await CreateUploadSignatureAsync(idToken, cancellationToken);
        var secureUrl = await UploadToCloudinaryAsync(
            signature, Path.GetFileName(filePath), bytes, contentType, cancellationToken);

        var updatedSession = await UpdateFirebaseProfileAsync(
            session, idToken, secureUrl, cancellationToken);
        FirebaseDesktopSessionStore.Save(updatedSession);
        return updatedSession;
    }

    private static async Task<CloudinaryUploadSignature> CreateUploadSignatureAsync(
        string idToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{DesktopPairingClient.ServerBaseUrl}/api/v1/account/profile-photo/sign");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemDetailAsync(response, cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Profil fotoğrafı imzası alınamadı (HTTP {(int)response.StatusCode})."
                    : detail);
        }

        var result = await response.Content.ReadFromJsonAsync<CloudinaryUploadSignature>(
            JsonOptions, cancellationToken);
        if (result is null ||
            string.IsNullOrWhiteSpace(result.CloudName) ||
            string.IsNullOrWhiteSpace(result.ApiKey) ||
            string.IsNullOrWhiteSpace(result.PublicId) ||
            string.IsNullOrWhiteSpace(result.Signature) ||
            result.Timestamp <= 0)
            throw new InvalidDataException("Server geçerli Cloudinary profil fotoğrafı imzası döndürmedi.");

        return result;
    }

    private static async Task<string> UploadToCloudinaryAsync(
        CloudinaryUploadSignature signature,
        string fileName,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(signature.ApiKey), "api_key");
        form.Add(new StringContent(signature.Timestamp.ToString(
            System.Globalization.CultureInfo.InvariantCulture)), "timestamp");
        form.Add(new StringContent(signature.PublicId), "public_id");
        form.Add(new StringContent(signature.Overwrite ? "true" : "false"), "overwrite");
        form.Add(new StringContent(signature.Signature), "signature");

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        var cloudName = Uri.EscapeDataString(signature.CloudName);
        using var response = await Http.PostAsync(
            $"https://api.cloudinary.com/v1_1/{cloudName}/image/upload",
            form,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cloudinary profil fotoğrafı yüklemesini kabul etmedi (HTTP {(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var secureUrl = root.TryGetProperty("secure_url", out var urlValue)
            ? urlValue.GetString()
            : null;
        if (!Uri.TryCreate(secureUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(uri.Host.Equals("res.cloudinary.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".res.cloudinary.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Cloudinary güvenli profil fotoğrafı adresi döndürmedi.");

        var uploadedPublicId = root.TryGetProperty("public_id", out var publicIdValue)
            ? publicIdValue.GetString()
            : null;
        if (!string.Equals(uploadedPublicId, signature.PublicId, StringComparison.Ordinal))
            throw new InvalidDataException("Yüklenen fotoğrafın Cloudinary dosya kimliği beklenen hesap kimliğiyle eşleşmiyor.");

        return uri.AbsoluteUri;
    }

    private static async Task<FirebaseDesktopSession> UpdateFirebaseProfileAsync(
        FirebaseDesktopSession session,
        string idToken,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        var endpoint =
            "https://identitytoolkit.googleapis.com/v1/accounts:update?key=" +
            Uri.EscapeDataString(DesktopPairingClient.FirebaseWebApiKey);

        using var response = await Http.PostAsJsonAsync(
            endpoint,
            new FirebaseProfileUpdateRequest(idToken, photoUrl, true),
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemDetailAsync(response, cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Firebase profil fotoğrafı güncellenemedi (HTTP {(int)response.StatusCode})."
                    : detail);
        }

        var result = await response.Content.ReadFromJsonAsync<FirebaseProfileUpdateResponse>(
            JsonOptions, cancellationToken);
        if (result is null ||
            !StringComparer.Ordinal.Equals(result.LocalId, session.Uid) ||
            string.IsNullOrWhiteSpace(result.PhotoUrl) ||
            !Uri.TryCreate(result.PhotoUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Firebase profil güncellemesi aynı kullanıcıya ait güvenli fotoğraf adresini doğrulamadı.");

        session.PhotoUrl = uri.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(result.IdToken))
            session.IdToken = result.IdToken;
        if (!string.IsNullOrWhiteSpace(result.RefreshToken))
            session.RefreshToken = result.RefreshToken;
        if (int.TryParse(result.ExpiresIn, out var expiresIn) && expiresIn > 0)
            session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 90)).ToUnixTimeSeconds();
        if (!string.IsNullOrWhiteSpace(result.DisplayName))
            session.DisplayName = result.DisplayName;

        return session;
    }

    private static void ValidateImageSignature(byte[] bytes, string contentType)
    {
        var isJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var isPng = bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        var isWebp = bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

        var valid = contentType switch
        {
            "image/jpeg" => isJpeg,
            "image/png" => isPng,
            "image/webp" => isWebp,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException("Dosya uzantısı ile gerçek görsel içeriği eşleşmiyor.");
    }

    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
        }
        catch (JsonException)
        {
            // Do not surface HTML proxy bodies or raw upload responses to the user.
        }

        return null;
    }

    private sealed record CloudinaryUploadSignature(
        string CloudName,
        string ApiKey,
        long Timestamp,
        string PublicId,
        string Signature,
        bool Overwrite);

    private sealed record FirebaseProfileUpdateRequest(
        string IdToken,
        string PhotoUrl,
        bool ReturnSecureToken);

    private sealed class FirebaseProfileUpdateResponse
    {
        public string? LocalId { get; set; }
        public string? PhotoUrl { get; set; }
        public string? IdToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? ExpiresIn { get; set; }
        public string? DisplayName { get; set; }
    }
}
