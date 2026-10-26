using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class StartupSettingsManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MynAssistant";
    private const string LegacyValueName = "MynAI";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string)
            || !string.IsNullOrWhiteSpace(key?.GetValue(LegacyValueName) as string);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("Windows başlangıç ayarları açılamadı.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = FindApplicationExecutable();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                "MynAssistant.exe bulunamadı. Başlangıç ayarı mevcut uygulama kurulumu doğrulanmadan kaydedilmedi.");
        }

        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static void OpenStartupSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:startupapps",
            UseShellExecute = true
        });
    }

    private static string? FindApplicationExecutable()
    {
        var candidates = new[]
        {
            Environment.ProcessPath,
            Path.Combine(AppContext.BaseDirectory, "MynAssistant.exe"),
            Path.Combine(AppContext.BaseDirectory, "MynAI.Windows.exe")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;

            var name = Path.GetFileName(candidate);
            if (name.Equals("MynAssistant.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MynAI.Windows.exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(candidate);
        }

        return null;
    }
}
