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

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
