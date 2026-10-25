namespace MynAI.Windows;

public sealed class MynAISettings
{
    public bool StartAgentWithWindows { get; set; }

    public bool StartDesktopBridgeWithWindows { get; set; }

    public bool WindowsNotifications { get; set; } = true;

    public bool MicrophoneEnabled { get; set; }

    public bool CameraEnabled { get; set; }

    // "kr" is the first-install default; "app" means the MynAssistant app mark,
    // and "custom" means a private copy stored under LocalAppData.
    public string LogoPreset { get; set; } = "kr";

    public string? CustomLogoPath { get; set; }

    public string BringToFrontHotkey { get; set; } =
        "Ctrl+Shift+Space";
}
