#define MyAppName "WSJT-X UDP Fanout"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "HAM Radio Tools"
#define MyAppExeName "WsjtxUdpFanout.exe"

[Setup]
AppId={{7D6CF34B-AC96-4C1E-A14A-9A2B30C0D7A1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\WsjtxUdpFanout
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=WsjtxUdpFanoutSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UseSetupLdr=x64

[Files]
Source: "publish\WsjtxUdpFanout.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "Sample_WsjtxUdpFanout.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "Run_Default.bat"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start automatically when I log in"; GroupDescription: "Startup:"; Flags: unchecked

[Icons]
Name: "{group}\WSJT-X UDP Fanout"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\WSJT-X UDP Fanout"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\WSJT-X UDP Fanout"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch WSJT-X UDP Fanout"; Flags: nowait postinstall skipifsilent
