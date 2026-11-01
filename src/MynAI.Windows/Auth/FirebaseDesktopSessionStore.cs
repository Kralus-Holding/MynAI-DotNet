using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MynAI.Windows.Auth;

public sealed class FirebaseDesktopSession
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("photoUrl")] public string? PhotoUrl { get; set; }
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("idToken")] public string IdToken { get; set; } = "";
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
}

public static class FirebaseDesktopSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MynAssistantDesktopAgent-v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string SessionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MynAssistant", "DesktopAgent", "session.bin");

    public static void Save(FirebaseDesktopSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Uid) || string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new InvalidDataException("Firebase oturumunda kullanıcı kimliği veya refresh token eksik.");
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(session);
        var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        var temp = SessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, SessionPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static FirebaseDesktopSession? Load()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<FirebaseDesktopSession>(clear, JsonOptions);
            return session is not null && !string.IsNullOrWhiteSpace(session.Uid)
                && !string.IsNullOrWhiteSpace(session.RefreshToken) ? session : null;
        }
        catch { return null; }
    }

    public static bool HasValidStoredSession() => Load() is not null;

    // Use this on an explicit sign-out path so the UI cannot report logout success
    // while the protected token file still exists. The legacy best-effort Delete
    // method remains for callers that intentionally treat cleanup as optional.
    public static void DeleteOrThrow()
    {
        if (!File.Exists(SessionPath))
            return;

        File.Delete(SessionPath);
        if (File.Exists(SessionPath))
            throw new IOException("Korumalı Firebase oturum dosyası silinemedi.");
    }

    public static void Delete()
    {
        try { DeleteOrThrow(); } catch { }
    }
}
