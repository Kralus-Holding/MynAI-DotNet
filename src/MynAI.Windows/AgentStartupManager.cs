using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Text;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class AgentStartupManager
{
    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName =
        "MynAssistant.Agent";

    private const string LegacyValueName =
        "MynAI.Agent";

    private static readonly string[] AgentProcessNames =
        ["MynAgent", "MynAI.Agent"];

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

        var agentPath = TryGetAgentExecutablePath();
        if (agentPath is null)
        {
            throw new FileNotFoundException(
                "MynAgent.exe bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");
        }

        key.SetValue(ValueName, $"\"{agentPath}\"", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static string? TryGetAgentExecutablePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Agent", "MynAgent.exe"),
            Path.Combine(AppContext.BaseDirectory, "MynAgent.exe"),

            // Compatibility with existing installs made before the rename.
            Path.Combine(AppContext.BaseDirectory, "Agent", "MynAI.Agent.exe"),
            Path.Combine(AppContext.BaseDirectory, "MynAI.Agent.exe")
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var repositoryRoot = directory;

        for (var i = 0; i < 5; i++)
        {
            repositoryRoot = repositoryRoot.Parent ?? repositoryRoot;
        }

        var debugDirectory = Path.Combine(
            repositoryRoot.FullName,
            "src",
            "MynAI.Agent",
            "bin",
            "Debug",
            "net10.0-windows10.0.19041.0");

        candidates.Add(Path.Combine(debugDirectory, "MynAgent.exe"));
        candidates.Add(Path.Combine(debugDirectory, "MynAI.Agent.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static bool IsRunning() =>
        AgentProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);

    public static string GetLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "Logs");

    public static void OpenLogDirectory()
    {
        var directory = GetLogDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    public static void StartAgent()
    {
        if (IsRunning())
            return;

        var agentPath = TryGetAgentExecutablePath();
        if (agentPath is null)
        {
            var missing = new FileNotFoundException(
                "MynAgent.exe bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");
            LogStartupFailure(missing, "Agent dosyası bulunamadı");
            throw missing;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            WorkingDirectory = Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            LogStartupFailure(ex, "Process.Start reddedildi");
            var isPolicyBlock = ex.NativeErrorCode == 4551
                || ex.Message.Contains("4551", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Application Control", StringComparison.OrdinalIgnoreCase);
            var message = isPolicyBlock
                ? "Windows Application Control MynAgent.exe dosyasını engelledi (4551). Güvenlik ilkesini kapatmayın; dağıtım imzasını ve Code Integrity > Operational günlüklerini kontrol edin."
                : $"MynAgent başlatılamadı (Windows hata kodu {ex.NativeErrorCode}). Ayrıntı Agent günlüklerine yazıldı.";
            throw new InvalidOperationException(message, ex);
        }
        catch (Exception ex)
        {
            LogStartupFailure(ex, "Agent başlatılamadı");
            throw;
        }
    }

    public static void EnsureAutomaticStartup()
    {
        try
        {
            // Always write the canonical path so upgrades migrate the legacy
            // MynAI.Agent value and repair a stale registry entry.
            SetEnabled(true);
            if (!IsRunning())
                StartAgent();
        }
        catch (Exception ex)
        {
            LogStartupFailure(ex, "Otomatik başlangıç");
        }
    }

    private static void LogStartupFailure(Exception exception, string operation)
    {
        try
        {
            var directory = GetLogDirectory();
            Directory.CreateDirectory(directory);
            var nativeCode = exception is Win32Exception win32 ? win32.NativeErrorCode.ToString() : "n/a";
            var line = $"[{DateTimeOffset.Now:O}] {operation}; code={nativeCode}; type={exception.GetType().Name}; message={exception.Message}{Environment.NewLine}";
            File.AppendAllText(
                Path.Combine(directory, "agent-startup.log"),
                line,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Startup diagnostics must never crash the main application.
        }
    }

    public static void StopAgent()
    {
        foreach (var processName in AgentProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    public static void RestartAgent()
    {
        StopAgent();
        Thread.Sleep(400);
        StartAgent();
    }
}
