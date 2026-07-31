#ifndef AppVersion
  #error AppVersion must be defined
#endif
#ifndef StageDir
  #error StageDir must be defined
#endif
#ifndef ArtifactDir
  #error ArtifactDir must be defined
#endif

[Setup]
AppId={{88C77C14-2208-4EAA-9856-44E0AC32A601}
AppName=CensorPlayer
AppVersion={#AppVersion}
AppPublisher=CBEPX
AppPublisherURL=https://github.com/CBEPX/mpvnet-censor-extension
AppSupportURL=https://github.com/CBEPX/mpvnet-censor-extension/issues
AppUpdatesURL=https://github.com/CBEPX/mpvnet-censor-extension/releases
DefaultDirName={localappdata}\Programs\CensorPlayer
DefaultGroupName=CensorPlayer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ArtifactDir}
OutputBaseFilename=CensorPlayer-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\mpvnet.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "portable_config\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\portable_config\*"; DestDir: "{app}\portable_config"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{app}\portable_config"

[Icons]
Name: "{group}\CensorPlayer"; Filename: "{app}\mpvnet.exe"
Name: "{autodesktop}\CensorPlayer"; Filename: "{app}\mpvnet.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"

[Run]
Filename: "{app}\mpvnet.exe"; Description: "Запустить CensorPlayer"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteUserData := SuppressibleMsgBox(
    'Удалить настройки, данные восстановления, журналы и диагностику?',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    DelTree(ExpandConstant('{localappdata}\CensorPlayer'), True, True, True);
  end;
end;
