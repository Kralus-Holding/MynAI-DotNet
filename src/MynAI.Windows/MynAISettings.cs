namespace MynAI.Windows;

public sealed class MynAISettings
{
    public bool StartAgentWithWindows { get; set; }

    public bool StartDesktopBridgeWithWindows { get; set; }

    public bool WindowsNotifications { get; set; } = true;

    public bool MicrophoneEnabled { get; set; }

    public bool CameraEnabled { get; set; }

    // "black" is the default for new installs; "app" uses the transparent app
    // icon, and "custom" uses a private copy stored under LocalAppData.
    public string LogoPreset { get; set; } = "black";

    public string? CustomLogoPath { get; set; }

    public string BringToFrontHotkey { get; set; } =
        "Ctrl+Shift+Space";
}
