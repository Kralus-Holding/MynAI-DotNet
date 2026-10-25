using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace MynAI.Windows;

public static class MynAssistantBranding
{
    private static string ResourceDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Resources");

    public static string UserLogoDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "UserContent",
            "Logos");

    public static string GetAppIconPath()
    {
        var transparentPng = Path.Combine(ResourceDirectory, "myn.png");
        return File.Exists(transparentPng)
            ? transparentPng
            : Path.Combine(ResourceDirectory, "myn.jpg");
    }

    private static string GetSelectedLogoPath(MynAISettings settings)
    {
        var preferred = settings.LogoPreset switch
        {
            "app" => GetAppIconPath(),
            "custom" when !string.IsNullOrWhiteSpace(settings.CustomLogoPath)
                         && File.Exists(settings.CustomLogoPath) =>
                settings.CustomLogoPath,
            _ => Path.Combine(ResourceDirectory, "myn-dark.png")
        };

        if (File.Exists(preferred))
            return Path.GetFullPath(preferred);

        var fallback = new[]
        {
            Path.Combine(ResourceDirectory, "myn-dark.png"),
            Path.Combine(ResourceDirectory, "myn.png"),
            Path.Combine(ResourceDirectory, "myn.jpg")
        }.FirstOrDefault(File.Exists);

        return Path.GetFullPath(fallback ?? GetAppIconPath());
    }

    public static BitmapImage? LoadSelectedLogo(MynAISettings settings)
    {
        var image = LoadImage(GetSelectedLogoPath(settings));
        if (image is not null)
            return image;

        foreach (var fallback in new[]
        {
            Path.Combine(ResourceDirectory, "myn-dark.png"),
            Path.Combine(ResourceDirectory, "myn.png"),
            Path.Combine(ResourceDirectory, "myn.jpg")
        })
        {
            image = LoadImage(fallback);
            if (image is not null)
                return image;
        }

        return null;
    }

    public static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static string CopyCustomLogo(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Seçilen görsel dosyası bulunamadı.");

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            throw new InvalidDataException("Yalnızca PNG, JPG, JPEG veya BMP görselleri destekleniyor.");

        var info = new FileInfo(sourcePath);
        if (info.Length <= 0 || info.Length > 20 * 1024 * 1024)
            throw new InvalidDataException("Görsel boş veya 20 MB sınırından büyük.");

        try
        {
            using var stream = File.OpenRead(sourcePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0
                || decoder.Frames[0].PixelWidth <= 0
                || decoder.Frames[0].PixelHeight <= 0
                || decoder.Frames[0].PixelWidth > 8192
                || decoder.Frames[0].PixelHeight > 8192)
                throw new InvalidDataException("Görsel boyutları desteklenmiyor.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("Seçilen dosya geçerli bir görsel olarak okunamadı.", ex);
        }

        if (LoadImage(sourcePath) is null)
            throw new InvalidDataException("Seçilen görsel çözümlenemedi.");

        Directory.CreateDirectory(UserLogoDirectory);
        var destination = Path.Combine(
            UserLogoDirectory,
            $"custom-logo-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}");

        File.Copy(sourcePath, destination, overwrite: false);
        return destination;
    }
}
