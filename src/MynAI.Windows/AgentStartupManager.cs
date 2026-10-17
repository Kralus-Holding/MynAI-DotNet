using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MynAI.Windows;

public static class AgentStartupManager
{
    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName =
        "MynAI.Agent";

    public static bool IsEnabled()
    {
        using var key =
            Registry.CurrentUser.OpenSubKey(
                RunKey,
                writable: false);

        var value =
            key?.GetValue(
                ValueName) as string;

        return !string.IsNullOrWhiteSpace(
            value);
    }

    public static void SetEnabled(
        bool enabled)
    {
        using var key =
            Registry.CurrentUser.OpenSubKey(
                RunKey,
                writable: true)
            ?? Registry.CurrentUser.CreateSubKey(
                RunKey);

        if (!enabled)
        {
            key.DeleteValue(
                ValueName,
                throwOnMissingValue: false);

            return;
        }

        var agentPath =
            TryGetAgentExecutablePath();

        if (agentPath is null)
        {
            throw new FileNotFoundException(
                "MynAI.Agent.exe bulunamadı. " +
                "Önce Agent'i build edin.");
        }

        key.SetValue(
            ValueName,
            $"\"{agentPath}\"",
            RegistryValueKind.String);
    }

    public static string? TryGetAgentExecutablePath()
    {
        var candidates =
            new List<string>();

        candidates.Add(
            Path.Combine(
                AppContext.BaseDirectory,
                "MynAI.Agent.exe"));

        var directory =
            new DirectoryInfo(
                AppContext.BaseDirectory);

        var repositoryRoot =
            directory;

        for (var i = 0; i < 5; i++)
        {
            repositoryRoot =
                repositoryRoot.Parent
                ?? repositoryRoot;
        }

        candidates.Add(
            Path.Combine(
                repositoryRoot.FullName,
                "src",
                "MynAI.Agent",
                "bin",
                "Debug",
                "net10.0-windows10.0.19041.0",
                "MynAI.Agent.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(
                    candidate);
        }

        return null;
    }

    public static bool IsRunning()
    {
        return Process
            .GetProcessesByName("MynAI.Agent")
            .Length > 0;
    }

    public static void StartAgent()
    {
        if (IsRunning())
            return;

        var agentPath =
            TryGetAgentExecutablePath();

        if (agentPath is null)
        {
            throw new FileNotFoundException(
                "MynAI.Agent.exe bulunamadı.");
        }

        var startInfo =
            new ProcessStartInfo
            {
                FileName = agentPath,
                WorkingDirectory =
                    Path.GetDirectoryName(
                        agentPath)
                    ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        Process.Start(
            startInfo);
    }

    public static void StopAgent()
    {
        foreach (var process in
                 Process.GetProcessesByName(
                     "MynAI.Agent"))
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);

                process.WaitForExit(
                    3000);
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

    public static void RestartAgent()
    {
        StopAgent();

        Thread.Sleep(400);

        StartAgent();
    }
}
