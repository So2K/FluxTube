#define AppVersion "9.1.3"

[Setup]
AppId=FluxTube.Native
AppName=FluxTube
AppVersion={#AppVersion}
AppPublisher=So2K
AppPublisherURL=https://github.com/So2K/FluxTube
AppUpdatesURL=https://github.com/So2K/FluxTube/releases
DefaultDirName={localappdata}\Programs\FluxTube
DefaultGroupName=FluxTube
UninstallDisplayIcon={app}\FluxTube.exe
SetupIconFile=..\src\FluxTube.Native\app.ico
OutputDir=..\dist\release
OutputBaseFilename=FluxTube-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
CloseApplications=yes
RestartApplications=yes

[Files]
Source: "..\dist\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FluxTube"; Filename: "{app}\FluxTube.exe"

[Run]
Filename: "{app}\FluxTube.exe"; Description: "Запустить FluxTube"; Flags: nowait postinstall skipifsilent
