; Inno Setup script. Compile with: iscc build\installer.iss
; (after build.ps1 has produced dist\MasterRelayVPN\)

#define AppName    "MasterRelayVPN"
#define AppVersion "1.3.0"

[Setup]
AppId={{6A8B4D54-79B4-4A12-8F4A-7C6E9B2E3A5C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=masterking32
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MasterRelayVPN-Setup
Compression=lzma2/ultra
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Files]
Source: "..\dist\MasterRelayVPN\MasterRelayVPN.exe";        DestDir: "{app}";       Flags: ignoreversion
Source: "..\dist\MasterRelayVPN\core\MasterRelayCore.exe";  DestDir: "{app}\core";  Flags: ignoreversion
Source: "..\dist\MasterRelayVPN\README.txt";                DestDir: "{app}";       Flags: ignoreversion skipifsourcedoesntexist

[Dirs]
Name: "{app}\data";       Permissions: users-modify
Name: "{app}\data\cert";  Permissions: users-modify
Name: "{app}\data\logs";  Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\MasterRelayVPN.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MasterRelayVPN.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\MasterRelayVPN.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
