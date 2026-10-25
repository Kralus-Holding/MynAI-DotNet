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
AppPublisherURL=https://github.com/Kralus-Holding/MynAI-DotNet
AppSupportURL=https://github.com/Kralus-Holding/MynAI-DotNet/issues
DefaultDirName={localappdata}\Programs\MynAssistant
UsePreviousAppDir=no
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


[Messages]
WizardLicense=Lisans sözleşmesi
LicenseLabel=Lütfen ilerlemeden önce aşağıdaki önemli bilgileri okuyun.
LicenseLabel3=Lütfen aşağıdaki lisans sözleşmesini okuyun. Uygulamayı kurmak için bu sözleşmeyi kabul etmelisiniz.
LicenseAccepted=Sözleşmeyi kabul &ediyorum
LicenseNotAccepted=Sözleşmeyi kabul et&miyorum

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek kısayollar:"; Flags: unchecked

[Files]
Source: "..\build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\agent-publish\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Agent\ is an application-payload-only directory. User settings/logs/custom logos are under %LOCALAPPDATA% and remain untouched.
Type: filesandordirs; Name: "{app}\Agent\*"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MynAssistant.Agent"; ValueData: """{app}\Agent\MynAgent.exe"""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MynAI.Agent"; Flags: deletevalue
; Repoint bridge autostart only when a prior installation had explicitly enabled it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MynAssistant.DesktopBridge"; ValueData: """{app}\MynAssistantDesktopBridge.exe"""; Flags: uninsdeletevalue; Check: ShouldMigrateBridgeStartup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MynAI.DesktopBridge"; Flags: deletevalue

[Icons]
Name: "{autoprograms}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "MynAssistant'ı şimdi başlat"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopProcessByName(const ImageName: String);
var
  ResultCode: Integer;
begin
  // First request a normal close (important for the main WPF window), then
  // terminate as a fallback because the background helpers have no UI to close.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ' + ImageName + ' /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(750);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ' + ImageName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(250);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  // Stop the UI first so it cannot start its child processes again while
  // the installer is preparing to replace files. CloseApplications runs after
  // PrepareToInstall, so Restart Manager sees the target files released.
  StopProcessByName('{#MyAppExeName}');
  StopProcessByName('MynAssistantDesktopBridge.exe');
  StopProcessByName('{#MyAgentExeName}');
  StopProcessByName('MynAI.Agent.exe');
end;

function NormalizeInstallDir(Path: String): String;
begin
  Result := ExpandFileName(Path);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function ShouldMigrateBridgeStartup: Boolean;
var
  ExistingValue: String;
begin
  Result :=
    RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'MynAssistant.DesktopBridge',
      ExistingValue)
    or
    RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'MynAI.DesktopBridge',
      ExistingValue);
end;

function ExistingInstallLocation(var InstallDir, InstallVersion: String;
  var VersionVerified: Boolean): Boolean;
var
  BinaryVersion: String;
  RegisteredVersion: String;
begin
  InstallDir := '';
  InstallVersion := '';
  VersionVerified := False;

  Result := RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E4FD24F0-B4A0-4D4C-875C-70781DC41F20}_is1',
    'InstallLocation',
    InstallDir);

  if not Result then
    Exit;

  // A stale uninstall record must not block a clean install after the old folder was removed.
  if not DirExists(InstallDir) or
     not FileExists(AddBackslash(InstallDir) + '{#MyAppExeName}') then
  begin
    Result := False;
    Exit;
  end;

  VersionVerified := GetVersionNumbersString(
    AddBackslash(InstallDir) + '{#MyAppExeName}',
    BinaryVersion);

  if not VersionVerified then
  begin
    InstallVersion := 'doğrulanamadı';
    Exit;
  end;

  InstallVersion := BinaryVersion;
  if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E4FD24F0-B4A0-4D4C-875C-70781DC41F20}_is1',
      'DisplayVersion',
      RegisteredVersion) and
     (RegisteredVersion <> '') then
    InstallVersion := RegisteredVersion + ' (EXE: ' + BinaryVersion + ')';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ExistingInstallDir: String;
  ExistingInstallVersion: String;
  ExistingVersionVerified: Boolean;
  SelectedInstallDir: String;
begin
  Result := True;

  if (CurPageID = wpSelectDir) and
     ExistingInstallLocation(ExistingInstallDir, ExistingInstallVersion, ExistingVersionVerified) then
  begin
    if not ExistingVersionVerified then
    begin
      MsgBox(
        'MynAssistant kurulum kaydı bulundu ancak mevcut EXE sürümü doğrulanamadı.' + #13#10 +
        'Hiçbir dosya değiştirilmedi. Mevcut uygulamayı Windows Ayarları > Uygulamalar bölümünden kaldırıp tekrar deneyin.',
        mbError,
        MB_OK);
      Result := False;
      Exit;
    end;

    SelectedInstallDir := ExpandConstant('{app}');
    if MsgBox(
      'MynAssistant bilgisayarınızda zaten kurulu. Mevcut kurulumu güncellemek ister misiniz?' + #13#10#13#10 +
      'Konum: ' + ExistingInstallDir + #13#10 +
      'Sürüm: ' + ExistingInstallVersion,
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON1) <> IDYES then
    begin
      // No means cancel this installation attempt; no application files are modified or deleted.
      Result := False;
      Exit;
    end;

    if CompareText(
        NormalizeInstallDir(ExistingInstallDir),
        NormalizeInstallDir(SelectedInstallDir)) <> 0 then
    begin
      MsgBox(
        'MynAssistant mevcut konumda kurulu: ' + ExistingInstallDir + #13#10#13#10 +
        'Bu sürüm tek aktif Agent ve ortak başlangıç kayıtları kullanıyor. ' +
        'İkinci bağımsız kurulum bu kayıtları bozabileceğinden kuruluma devam edilmedi. ' +
        'Güncellemek için yukarıdaki konumu hedef olarak seçin. ' +
        'Eski klasör silinmişse yeni bir hedef seçebilirsiniz.',
        mbInformation,
        MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

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
