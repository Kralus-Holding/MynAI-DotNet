#define MyAppName "MynAssistant"
#define MyAppVersion "0.1.5"
#define MyAppPublisher "MynKralus"
#define MyAppExeName "MynAssistant.exe"
#define MyAgentExeName "MynAgent.exe"

[Setup]
AppId={{E4FD24F0-B4A0-4D4C-875C-70781DC41F20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/MynKralus/MynAI-DotNet
AppSupportURL=https://github.com/MynKralus/MynAI-DotNet/issues
DefaultDirName={localappdata}\Programs\MynAssistant
UsePreviousAppDir=yes
DefaultGroupName=MynAssistant
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\MynAI.Windows\Resources\myn.ico
LicenseFile=LICENSE-AGREEMENT.rtf
WizardSmallImageFile=..\build\installer\MynAssistantWizardSmall.bmp
WizardSmallImageBackColor=clWhite

OutputDir=..\build\installer
OutputBaseFilename=MynAssistant-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
VersionInfoCompany=MynKralus
VersionInfoDescription=MynAssistant Windows Installer
VersionInfoProductName=MynAssistant
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion=0.1.5.0
VersionInfoCopyright=Copyright (C) MynKralus

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"


[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek kısayollar:"; Flags: unchecked

[Files]
Source: "..\build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\agent-publish\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\config\client_secret.json"; DestDir: "{localappdata}\MynAssistant\Google"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Agent\{#MyAgentExeName}"; Description: "MynAgent arka plan hizmetini başlat"; WorkingDir: "{app}\Agent"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "MynAssistant'ı şimdi başlat"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MynAssistantDesktopBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MynAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MynAI.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAssistant.DesktopBridge');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAssistant.Agent');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAI.DesktopBridge');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAI.Agent');
  end;
end;
