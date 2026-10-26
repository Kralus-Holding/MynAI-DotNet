using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using MynAI.Windows.Auth;
using MynAI.Windows.Google;

namespace MynAI.Windows;

public static class DesktopBridgeStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MynAssistant.DesktopBridge";
    private const string LegacyValueName = "MynAI.DesktopBridge";
    private const string ExecutableName = "MynAssistantDesktopBridge.exe";
    private static readonly object LogLock = new();

    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "DesktopAgent",
            "bridge.log");

    private static string StatusPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "DesktopAgent",
            "status.json");

    public static DesktopBridgeRuntimeStatus? ReadRuntimeStatus()
    {
        try
        {
            if (!File.Exists(StatusPath))
                return null;

            return JsonSerializer.Deserialize<DesktopBridgeRuntimeStatus>(
                File.ReadAllText(StatusPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // Runtime status is diagnostic only; malformed data must not break Settings.
            return null;
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var legacyValue = key?.GetValue(LegacyValueName) as string;
        return !string.IsNullOrWhiteSpace(value)
            || !string.IsNullOrWhiteSpace(legacyValue);
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

        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
            throw new InvalidOperationException("Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın.");

        key.SetValue(ValueName, $"{(char)34}{executablePath}{(char)34}", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static string? TryGetExecutablePath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string GetOAuthConfigPath() =>
        GoogleOAuthConfigStore.GetPath();

    public static bool IsRunning()
    {
        var isRunning = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                isRunning |= !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return isRunning;
    }

    public static void StartAgent()
    {
        if (IsRunning())
            return;

        var executablePath = TryGetExecutablePath();
        if (executablePath is null)
            throw new FileNotFoundException(
                $"{ExecutableName} bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");

        if (!FirebaseDesktopSessionStore.HasValidStoredSession())
            throw new InvalidOperationException("Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın.");

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
            try { process.Dispose(); } catch (ObjectDisposedException) { }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Desktop Bridge süreci başlatılamadı.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog($"Bridge başlatıldı. PID={process.Id}");

            // Verify that a real process remains alive instead of reporting success
            // merely because Process.Start returned without throwing.
            if (!WaitForProcessToAppear(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException(
                    "Desktop Bridge başlatıldıktan sonra çalışan süreç doğrulanamadı. bridge.log dosyasını kontrol edin.");
        }
        catch (Exception ex)
        {
            AppendLog($"Bridge başlatma hatası: {ex.GetType().Name}: {ex.Message}");
            try { process.Dispose(); } catch (ObjectDisposedException) { }
            throw;
        }
    }

    private static bool WaitForProcessToAppear(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (IsRunning())
                return true;

            Thread.Sleep(100);
        }
        while (timer.Elapsed < timeout);

        return false;
    }

    public static void StopAgent()
    {
        var failures = new List<string>();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            var processId = 0;
            try
            {
                processId = process.Id;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        throw new TimeoutException($"Bridge PID {processId} 5 saniye içinde kapanmadı.");
                }

                if (!process.HasExited)
                    throw new InvalidOperationException($"Bridge PID {processId} durdurma sonrası hâlâ çalışıyor.");

                AppendLog($"Bridge süreci doğrulanarak durduruldu. PID={processId}");
            }
            catch (Exception ex)
            {
                AppendLog($"Bridge durdurma hatası PID={processId}: {ex.GetType().Name}: {ex.Message}");
                failures.Add($"PID {processId}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (IsRunning())
            failures.Add("Desktop Bridge durdurma denemesinden sonra hâlâ çalışıyor.");

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Desktop Bridge tamamen durdurulamadı. bridge.log dosyasını kontrol edin. " +
                string.Join(" ", failures));
    }

    public static void RestartAgent()
    {
        StopAgent();

        if (IsRunning())
            throw new InvalidOperationException(
                "Desktop Bridge kapanmadığı için yeniden başlatma iptal edildi; ikinci bir süreç açılmadı.");

        StartAgent();

        if (!IsRunning())
            throw new InvalidOperationException(
                "Desktop Bridge başlatma isteğinden sonra çalışır durumda doğrulanamadı.");
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


public sealed record DesktopBridgeRuntimeStatus(
    string? State,
    string? Message,
    string? Email,
    string? DeviceId,
    string? MachineName,
    DateTimeOffset UpdatedAt);
