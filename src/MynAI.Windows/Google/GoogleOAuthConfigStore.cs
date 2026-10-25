using System.IO;
using System.Text.Json;

namespace MynAI.Windows.Google;

public static class GoogleOAuthConfigStore
{
    public const string OAuthFileName = "client_secret.json";

    public static string CurrentDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "Google");

    public static string LegacyDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAI",
            "Google");

    public static string CurrentPath => Path.Combine(CurrentDirectory, OAuthFileName);
    public static string LegacyPath => Path.Combine(LegacyDirectory, OAuthFileName);

    public static string GetPath()
    {
        if (File.Exists(CurrentPath))
            return CurrentPath;

        // Preserve compatibility with existing installs using the old brand folder.
        if (File.Exists(LegacyPath))
            return LegacyPath;

        return CurrentPath;
    }

    public static string ImportFromFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Seçilen Google OAuth JSON dosyası bulunamadı.");

        var info = new FileInfo(sourcePath);
        if (info.Length <= 0 || info.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("OAuth JSON dosyası boş veya 2 MB sınırından büyük.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Bu dosya Google Desktop OAuth istemci yapılandırması değil. JSON içinde 'installed' nesnesi bulunmalı.");
            }

            foreach (var required in new[] { "client_id", "client_secret", "auth_uri", "token_uri" })
            {
                if (!installed.TryGetProperty(required, out var value)
                    || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    throw new InvalidDataException(
                        $"Google Desktop OAuth yapılandırmasında gerekli '{required}' alanı eksik.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("OAuth dosyası geçerli JSON biçiminde değil.", ex);
        }

        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(CurrentPath);
        Directory.CreateDirectory(CurrentDirectory);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return target;

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Temporary-file cleanup is best-effort.
            }
        }

        return target;
    }
}
