namespace MynAI.Windows;

public sealed class MynAISettings
{
    public bool StartAgentWithWindows { get; set; }

    public bool StartDesktopBridgeWithWindows { get; set; }

    public bool WindowsNotifications { get; set; } = true;

    public bool MicrophoneEnabled { get; set; }

    public bool CameraEnabled { get; set; }

    public string BringToFrontHotkey { get; set; } =
        "Ctrl+Shift+Space";
}
