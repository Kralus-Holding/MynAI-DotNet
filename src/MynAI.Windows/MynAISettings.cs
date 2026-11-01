namespace MynAI.Windows;

public sealed class MynAISettings
{
    public bool StartAgentWithWindows { get; set; }

    public bool StartApplicationWithWindows { get; set; }

    public bool StartDesktopBridgeWithWindows { get; set; }

    public bool WindowsNotifications { get; set; } = true;

    // Diagnostic email reporting is opt-in and disabled on first install.
    public bool SendDiagnosticsAutomatically { get; set; }

    public bool MicrophoneEnabled { get; set; }

    public bool CameraEnabled { get; set; }

    // "kr" is the first-install default; "app" means the MynAssistant app mark,
    // and "custom" means a private copy stored under LocalAppData.
    public string LogoPreset { get; set; } = "kr";

    public string? CustomLogoPath { get; set; }

    // Custom logos are stored separately from built-in brand assets. The legacy
    // CustomLogoPath remains for settings files created before the gallery existed.
    public List<string> UserLogoPaths { get; set; } = new();

    public string BringToFrontHotkey { get; set; } =
        "Ctrl+Shift+Space";
}
