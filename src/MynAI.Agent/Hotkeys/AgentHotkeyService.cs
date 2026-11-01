using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MynAI.Agent.Hotkeys;

/// <summary>
/// Registers the configured global shortcut in the always-on background Agent.
/// The WPF window does not own the hotkey, so it can be closed and reopened.
/// </summary>
public sealed class AgentHotkeyService
{
    private const int HotkeyId = 0x4D59;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const string ActivationEventName = @"Local\MynAssistant.Ui.Activate";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MynAI",
        "settings.json");

    private static readonly string[] UiCandidates =
    [
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "MynAssistant.exe")),
        Path.Combine(AppContext.BaseDirectory, "MynAssistant.exe"),
        Path.Combine(AppContext.BaseDirectory, "MynAI.Windows.exe")
    ];

    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Run(cancellationToken), cancellationToken);

    private static void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var shortcut = ReadConfiguredShortcut();
            if (!TryParseShortcut(shortcut, out var modifiers, out var virtualKey, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Console.Error.WriteLine($"Global hotkey disabled: {error}");
                DelayBeforeRetry(cancellationToken);
                continue;
            }

            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | ModNoRepeat, virtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    $"Global hotkey '{shortcut}' could not be registered (Win32={code}). Retrying in 5 seconds.");
                DelayBeforeRetry(cancellationToken);
                continue;
            }

            var threadId = GetCurrentThreadId();
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            using var cancellationRegistration = cancellationToken.Register(
                () => PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero));

            try
            {
                Console.WriteLine($"Global hotkey active: {shortcut}");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                    if (result <= 0)
                        break;

                    if (message.Message == WmHotkey && message.WParam == (nuint)HotkeyId)
                        BringUpMainWindow();
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }
        }
    }

    private static void BringUpMainWindow()
    {
        try
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The main UI is not running; launch one instance below.
            }

            var executable = UiCandidates.FirstOrDefault(File.Exists);
            if (executable is null)
            {
                Console.Error.WriteLine("MynAssistant.exe was not found; the global shortcut could not open the UI.");
                return;
            }

            var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
            try
            {
                if (existing.Any(process => !process.HasExited))
                    return;
            }
            finally
            {
                foreach (var process in existing)
                    process.Dispose();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not activate MynAssistant UI: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ReadConfiguredShortcut()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return "Ctrl+Shift+Space";

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.TryGetProperty("BringToFrontHotkey", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "Ctrl+Shift+Space"
                : "Ctrl+Shift+Space";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Hotkey settings could not be read: {ex.GetType().Name}");
            return "Ctrl+Shift+Space";
        }
    }

    private static bool TryParseShortcut(
        string value,
        out uint modifiers,
        out uint virtualKey,
        out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "A shortcut needs a modifier and a key.";
            return false;
        }

        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => 0u
            };
            if (modifier == 0)
            {
                error = $"Unsupported modifier: {parts[index]}";
                return false;
            }
            modifiers |= modifier;
        }

        if (parts.Take(parts.Length - 1).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != parts.Length - 1)
        {
            error = "Duplicate shortcut modifier.";
            return false;
        }

        virtualKey = ResolveVirtualKey(parts[^1]);
        if (virtualKey == 0)
        {
            error = $"Unsupported shortcut key: {parts[^1]}";
            return false;
        }

        return true;
    }

    private static uint ResolveVirtualKey(string value)
    {
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return character;
        }

        if (value.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan(1), out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
            return (uint)(0x70 + functionNumber - 1);

        return value.ToLowerInvariant() switch
        {
            "space" or "spacebar" => 0x20,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0
        };
    }

    private static void DelayBeforeRetry(CancellationToken cancellationToken)
    {
        try { Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
