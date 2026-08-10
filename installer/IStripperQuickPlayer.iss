#define AppName "iStripper QuickPlayer"
#define AppExeName "IstripperQuickPlayer.exe"

#ifndef AppVersion
  #define AppVersion "0.36.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef SceneSourceDir
  #define SceneSourceDir "..\scenes\Midnight Gallery Club"
#endif

[Setup]
AppId={{BBDE0DB6-A7D5-4E5E-8A66-780A6D372CCC}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=KittyPingu
AppPublisherURL=https://github.com/KittyPingu/IStripperQuickPlayer
AppSupportURL=https://github.com/KittyPingu/IStripperQuickPlayer/issues
AppUpdatesURL=https://github.com/KittyPingu/IStripperQuickPlayer/releases
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
Compression=lzma2
DefaultDirName={localappdata}\Programs\IStripperQuickPlayer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=IStripperQuickPlayer-{#AppVersion}-Setup
OutputDir={#OutputDir}
PrivilegesRequired=lowest
RestartApplications=no
SetupIconFile=..\IstripperQuickPlayer\df2284943cc77e7e1a5fa6a0da8ca265.ico
SolidCompression=yes
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "BridgeProbe.exe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SceneSourceDir}\*"; DestDir: "{localappdata}\vghd\data\scenes\Midnight Gallery Club"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
Type: filesandordirs; Name: "{app}\ShaderVideoStreamer"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\ShaderVideoStreamer"

[Code]
function CreateHardLinkW(
  NewFileName, ExistingFileName: String;
  SecurityAttributes: Integer): Boolean;
  external 'CreateHardLinkW@kernel32.dll stdcall';

procedure CreatePlayerHardLinks;
var
  ManifestPath: String;
  RelativePaths: TArrayOfString;
  RelativePath: String;
  LinkPath: String;
  TargetPath: String;
  I: Integer;
begin
  ManifestPath := ExpandConstant(
    '{app}\ShaderVideoStreamer\hardlinks.manifest');
  if not LoadStringsFromFile(ManifestPath, RelativePaths) then
    RaiseException('The ShaderVideoStreamer hard-link manifest is missing.');

  for I := 0 to GetArrayLength(RelativePaths) - 1 do
  begin
    RelativePath := Trim(RelativePaths[I]);
    if (RelativePath = '') or (Pos('..', RelativePath) > 0) or
       (Pos(':', RelativePath) > 0) then
      RaiseException('The ShaderVideoStreamer hard-link manifest is invalid.');

    LinkPath := ExpandConstant(
      '{app}\ShaderVideoStreamer\' + RelativePath);
    TargetPath := ExpandConstant('{app}\' + RelativePath);
    if not FileExists(TargetPath) then
      RaiseException('A shared runtime file is missing: ' + TargetPath);
    if not ForceDirectories(ExtractFileDir(LinkPath)) then
      RaiseException('Could not create directory: ' + ExtractFileDir(LinkPath));
    if FileExists(LinkPath) and not DeleteFile(LinkPath) then
      RaiseException('Could not replace existing file: ' + LinkPath);
    if not CreateHardLinkW(LinkPath, TargetPath, 0) then
      RaiseException('Could not create hard link: ' + LinkPath);
  end;

  Log(Format('Created %d ShaderVideoStreamer hard links.',
    [GetArrayLength(RelativePaths)]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CreatePlayerHardLinks;
end;
