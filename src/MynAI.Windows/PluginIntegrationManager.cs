using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class PluginIntegrationManager
{
    public static string? FindChromeExecutable()
    {
        var registered = FindRegisteredExecutable("chrome.exe");
        if (registered is not null)
            return registered;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindLocalSendExecutable()
    {
        var registered = FindRegisteredExecutable("localsend_app.exe")
            ?? FindRegisteredExecutable("LocalSend.exe");
        if (registered is not null)
            return registered;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        var fileNames = new[] { "localsend_app.exe", "LocalSend.exe" };
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (var folder in new[] { "LocalSend", "localsend" })
            {
                foreach (var fileName in fileNames)
                {
                    var candidate = Path.Combine(root, folder, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    public static void OpenApplication(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("Uygulamanın yürütülebilir dosyası artık bulunamıyor.");

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }

    private static string? FindRegisteredExecutable(string executableName)
    {
        var subKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(subKeyPath, writable: false);
            var value = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                return Path.GetFullPath(value);
        }

        return null;
    }
}
