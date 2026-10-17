using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace MynAI.Windows;

public static class GlobalHotkeyService
{
    private const int HotkeyId = 7331;
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private const int SW_RESTORE = 9;

    private static MainWindow? _window;
    private static HwndSource? _source;
    private static IntPtr _handle;

    public static void Attach(
        MainWindow window)
    {
        _window = window;

        window.SourceInitialized +=
            (_, _) =>
            {
                Initialize(window);
            };
    }

    public static bool Apply(
        string hotkey,
        out string error)
    {
        error = string.Empty;

        if (_handle == IntPtr.Zero)
        {
            error =
                "MynAI penceresi henüz hazır değil.";

            return false;
        }

        Unregister();

        if (string.IsNullOrWhiteSpace(
                hotkey))
        {
            return true;
        }

        if (!TryParse(
                hotkey,
                out var modifiers,
                out var key))
        {
            error =
                "Kısayol biçimi geçersiz.";

            return false;
        }

        var registered =
            RegisterHotKey(
                _handle,
                HotkeyId,
                modifiers,
                (uint)KeyInterop.VirtualKeyFromKey(
                    key));

        if (!registered)
        {
            error =
                "Bu kısayol Windows tarafından " +
                "başka bir uygulama tarafından kullanılıyor olabilir.";

            return false;
        }

        return true;
    }

    private static void Initialize(
        MainWindow window)
    {
        _handle =
            new WindowInteropHelper(
                window).Handle;

        _source =
            HwndSource.FromHwnd(
                _handle);

        _source?.AddHook(
            WndProc);

        var settings =
            new MynAISettingsStore()
                .Load();

        Apply(
            settings.BringToFrontHotkey,
            out _);
    }

    private static IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WM_HOTKEY &&
            wParam.ToInt32() == HotkeyId)
        {
            ShowMynAI();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ShowMynAI()
    {
        var window = _window;

        if (window is null)
            return;

        if (window.WindowState ==
            WindowState.Minimized)
        {
            ShowWindow(
                _handle,
                SW_RESTORE);
        }

        window.ShowInTaskbar = true;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;

        SetForegroundWindow(
            _handle);
    }

    private static void Unregister()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(
                _handle,
                HotkeyId);
        }
    }

    private static bool TryParse(
        string value,
        out uint modifiers,
        out Key key)
    {
        modifiers = 0;
        key = Key.None;

        var parts =
            value.Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
            return false;

        for (var i = 0;
             i < parts.Length - 1;
             i++)
        {
            switch (
                parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;

                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;

                case "alt":
                    modifiers |= MOD_ALT;
                    break;

                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;

                default:
                    return false;
            }
        }

        if (!Enum.TryParse(
                parts[^1],
                ignoreCase: true,
                out key))
        {
            return false;
        }

        return key != Key.None;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    [DllImport(
        "user32.dll")]
    private static extern bool SetForegroundWindow(
        IntPtr hWnd);

    [DllImport(
        "user32.dll")]
    private static extern bool ShowWindow(
        IntPtr hWnd,
        int nCmdShow);
}



