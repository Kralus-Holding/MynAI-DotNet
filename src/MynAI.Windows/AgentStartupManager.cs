using System.Diagnostics;
using System.IO;
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

    private static readonly object LogLock = new();

    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant",
            "Logs",
            "mynagent.log");

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

            // Compatibility with older installations.
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

    public static void StartAgent()
    {
        if (IsRunning())
        {
            AppendLog("MynAgent start skipped: a compatible Agent process is already running.");
            return;
        }

        var agentPath = TryGetAgentExecutablePath();
        if (agentPath is null)
        {
            var error = new FileNotFoundException(
                "MynAgent.exe bulunamadı. MynAssistant kurulumunu onar veya yeniden yükle.");
            AppendLog("MynAgent start failed: " + error);
            throw error;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            WorkingDirectory = Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

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

        process.Exited += (_, _) =>
        {
            try
            {
                AppendLog($"MynAgent exited with code {process.ExitCode}.");
            }
            catch (Exception ex)
            {
                AppendLog("Could not read MynAgent exit code: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("MynAgent process could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog($"MynAgent launched. PID={process.Id}; path={agentPath}");
        }
        catch (Exception ex)
        {
            AppendLog("MynAgent launch failed: " + ex);
            process.Dispose();
            throw;
        }
    }

    public static void LogStartupFailure(string context, Exception exception) =>
        AppendLog($"{context}: {exception}");

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
                    AppendLog($"Stopped Agent process {processName}, PID={process.Id}.");
                }
                catch (Exception ex)
                {
                    AppendLog($"Could not stop Agent process {processName}: {ex.Message}");
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
            // Diagnostic logging must never stop the Agent from starting.
        }
    }
}
