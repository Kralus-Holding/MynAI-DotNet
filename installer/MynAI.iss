#define MyAppName "MynAI"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "MynAI"
#define MyAppExeName "MynAI.Windows.exe"

[Setup]
AppId={{E4FD24F0-B4A0-4D4C-875C-70781DC41F20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MynAI
DefaultGroupName=MynAI
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\MynAI.Windows\Resources\myn.ico
OutputDir=..\build\installer
OutputBaseFilename=MynAI-Setup
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek kısayollar:"; Flags: unchecked

[Files]
Source: "..\build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\config\client_secret.json"; DestDir: "{localappdata}\MynAI\Google"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\MynAI"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MynAI"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "MynAI'yi şimdi başlat"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MynAssistantDesktopBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MynAI.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAI.DesktopBridge');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MynAI.Agent');
  end;
end;
