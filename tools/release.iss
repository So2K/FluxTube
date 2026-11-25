#ifndef AppVersion
#define AppVersion "9.2.1"
#endif

[Setup]
AppId=FluxTube.Native
AppName=FluxTube
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=So2K
AppPublisherURL=https://github.com/So2K/FluxTube
AppUpdatesURL=https://github.com/So2K/FluxTube/releases
DefaultDirName={localappdata}\Programs\FluxTube
DefaultGroupName=FluxTube
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\FluxTube.exe
SetupIconFile=..\src\FluxTube.Native\app.ico
OutputDir=..\dist\release
OutputBaseFilename=FluxTube-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FluxTube"; Filename: "{app}\FluxTube.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\FluxTube.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,FluxTube}"; Flags: nowait postinstall skipifsilent
