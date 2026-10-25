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

[Icons]
Name: "{autoprograms}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MynAssistant"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "MynAssistant'ı şimdi başlat"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function NormalizeInstallDir(Path: String): String;
begin
  Result := ExpandFileName(Path);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ExistingInstallDir: String;
  SelectedInstallDir: String;
begin
  Result := True;

  if CurPageID = wpSelectDir then
  begin
    if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E4FD24F0-B4A0-4D4C-875C-70781DC41F20}_is1',
      'InstallLocation',
      ExistingInstallDir) and DirExists(ExistingInstallDir) then
    begin
      SelectedInstallDir := ExpandConstant('{app}');
      if CompareText(
        NormalizeInstallDir(ExistingInstallDir),
        NormalizeInstallDir(SelectedInstallDir)) <> 0 then
      begin
        MsgBox(
          'MynAssistant şu klasörde zaten kurulu:' + #13#10 +
          ExistingInstallDir + #13#10#13#10 +
          'Aynı anda birden fazla bağımsız kurulum desteklenmiyor. ' +
          'Güncelleme için aynı klasörü seçin. Farklı bir konuma geçmek için ' +
          'önce mevcut kurulumu Windows Ayarları > Uygulamalar bölümünden kaldırın. ' +
          'Önceki kurulum klasörü silinmişse yeni konum seçebilirsiniz.',
          mbInformation,
          MB_OK);
        Result := False;
      end;
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
