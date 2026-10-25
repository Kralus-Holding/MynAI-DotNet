using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class DesktopBridgeStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MynAssistant.DesktopBridge";
    private const string LegacyValueName = "MynAI.DesktopBridge";
    private const string ExecutableName = "MynAssistantDesktopBridge.exe";
    private const string OAuthFileName = "client_secret.json";
    private const string ConsentKeyPath = @"Software\MynAssistant\DesktopBridge";
    private const string ConsentValueName = "ConnectionConsentPrompted";
    private const string StatusFileName = "status.json";
    private static readonly object LogLock = new();

    private static string GoogleDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "Google");

    private static string LegacyGoogleDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAI",
            "Google");

    private static string AgentDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "DesktopAgent");

    private static string LogPath => Path.Combine(AgentDataDirectory, "bridge.log");
    private static string StatusPath => Path.Combine(AgentDataDirectory, StatusFileName);

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var legacyValue = key?.GetValue(LegacyValueName) as string;
        return !string.IsNullOrWhiteSpace(value)
            || !string.IsNullOrWhiteSpace(legacyValue);
    }

    public static bool HasConnectionConsentPrompted()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ConsentKeyPath, writable: false);
        return key?.GetValue(ConsentValueName) is int value && value == 1;
    }

    public static void MarkConnectionConsentPrompted()
    {
        using var key = Registry.CurrentUser.CreateSubKey(ConsentKeyPath);
        if (key is null)
            throw new InvalidOperationException("Telefon-PC bağlantısı onayı kaydedilemedi.");

        key.SetValue(ConsentValueName, 1, RegistryValueKind.DWord);
    }

    public static string GetCloudConnectionStatus()
    {
        if (!IsRunning())
            return "Bulut durumu: bağlantı köprüsü çalışmıyor.";

        try
        {
            if (!File.Exists(StatusPath))
                return "Firebase cihaz kaydı henüz doğrulanmadı. İlk Google girişi veya Firestore kaydı bekleniyor; bridge.log dosyasını kontrol et.";

            using var document = JsonDocument.Parse(File.ReadAllText(StatusPath));
            var root = document.RootElement;
            var state = root.TryGetProperty("status", out var stateNode) ? stateNode.GetString() : null;
            var updatedAtText = root.TryGetProperty("updatedAt", out var timeNode) ? timeNode.GetString() : null;
            var deviceName = root.TryGetProperty("deviceName", out var nameNode) ? nameNode.GetString() : null;

            if (!DateTimeOffset.TryParse(updatedAtText, out var updatedAt))
                return "Bulut durumu okunamadı. bridge.log dosyasını kontrol et.";

            var age = DateTimeOffset.UtcNow - updatedAt.ToUniversalTime();
            if (age < TimeSpan.FromSeconds(-10) || age > TimeSpan.FromSeconds(90))
                return $"Son Firebase sinyali eski ({Math.Max(0, (int)age.TotalSeconds)} sn). Google girişi ve ağ bağlantısını kontrol et.";

            if (string.Equals(state, "online", StringComparison.OrdinalIgnoreCase))
                return $"Buluta kayıtlı: {string.IsNullOrWhiteSpace(deviceName) ? "Windows bilgisayar" : deviceName} · son sinyal {(int)Math.Max(0, age.TotalSeconds)} sn önce.";

            if (string.Equals(state, "offline", StringComparison.OrdinalIgnoreCase))
                return "Son Firebase durumu çevrimdışı.";

            return "Firebase cihaz durumu henüz doğrulanmadı.";
        }
        catch (Exception ex)
        {
            AppendLog("Bulut durum dosyası okunamadı: " + ex.Message);
            return "Firebase bağlantı durumu okunamadı. bridge.log dosyasını kontrol et.";
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = TryGetExecutablePath();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"{ExecutableName} bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");
        }

        var oauthPath = GetOAuthConfigPath();
        if (!File.Exists(oauthPath))
        {
            throw new FileNotFoundException(
                $"Telefon-PC bağlantısı için Google OAuth yapılandırması bulunamadı: {oauthPath}");
        }

        key.SetValue(ValueName, $"{(char)34}{executablePath}{(char)34}", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static string? TryGetExecutablePath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string GetOAuthConfigPath()
    {
        var currentPath = Path.Combine(GoogleDirectory, OAuthFileName);
        if (File.Exists(currentPath))
            return currentPath;

        // Preserve OAuth configuration from the previous MynAI-branded install.
        var legacyPath = Path.Combine(LegacyGoogleDirectory, OAuthFileName);
        return File.Exists(legacyPath) ? legacyPath : currentPath;
    }

    public static bool IsRunning() =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)).Length > 0;

    public static void StartAgent()
    {
        if (IsRunning())
            return;

        var executablePath = TryGetExecutablePath();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"{ExecutableName} bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");
        }

        var oauthPath = GetOAuthConfigPath();
        if (!File.Exists(oauthPath))
        {
            throw new FileNotFoundException(
                "Google OAuth yapılandırması bulunamadı. MynAssistant kurulumunda Google bağlantı ayarını kontrol et.",
                oauthPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendLog(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendLog("[stderr] " + eventArgs.Data);
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try { process.Dispose(); } catch { }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Desktop Bridge süreci başlatılamadı.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog($"Bridge başlatıldı. PID={process.Id}");
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static void StopAgent()
    {
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                AppendLog("Bridge durduruldu.");
            }
            catch (Exception ex)
            {
                AppendLog("Bridge durdurulamadı: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static void RestartAgent()
    {
        StopAgent();
        Thread.Sleep(400);
        StartAgent();
    }

    private static void AppendLog(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never prevent the bridge from starting or stopping.
        }
    }
}
