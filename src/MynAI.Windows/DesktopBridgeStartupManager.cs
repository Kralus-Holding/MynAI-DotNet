using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class DesktopBridgeStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MynAI.DesktopBridge";
    private const string ExecutableName = "MynAssistantDesktopBridge.exe";
    private const string OAuthFileName = "client_secret.json";
    private static readonly object LogLock = new();

    private static string GoogleDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAI",
            "Google");

    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAI",
            "DesktopAgent",
            "bridge.log");

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = TryGetExecutablePath();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"{ExecutableName} bulunamadı. MynAI kurulumunu onar veya yeniden yükle.");
        }

        var oauthPath = GetOAuthConfigPath();
        if (!File.Exists(oauthPath))
        {
            throw new FileNotFoundException(
                $"Telefon-PC bağlantısı için Google OAuth yapılandırması bulunamadı: {oauthPath}");
        }

        key.SetValue(ValueName, $"{(char)34}{executablePath}{(char)34}", RegistryValueKind.String);
    }

    public static string? TryGetExecutablePath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string GetOAuthConfigPath() =>
        Path.Combine(GoogleDirectory, OAuthFileName);

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
                $"{ExecutableName} bulunamadı. MynAI kurulumunu onar veya yeniden yükle.");
        }

        var oauthPath = GetOAuthConfigPath();
        if (!File.Exists(oauthPath))
        {
            throw new FileNotFoundException(
                "Google OAuth yapılandırması bulunamadı. MynAI kurulumunda Google bağlantı ayarını kontrol et.",
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
