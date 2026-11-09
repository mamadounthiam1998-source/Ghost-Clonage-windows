; Installateur GHOST CLONAGE WINDOWS (Inno Setup 6)
; Compilation : ISCC.exe /DAppVersion=1.0.0 installer\GhostClonage.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "GHOST CLONAGE WINDOWS"
#define AppExe  "GhostClonage.exe"

[Setup]
AppId={{8F2C6B5E-3D41-4C6A-9E57-6A1B0C4D9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Ghost Clonage
DefaultDirName={autopf}\Ghost Clonage Windows
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=GhostClonageWindows-Setup-{#AppVersion}
SetupIconFile=..\src\Ghost.App\Assets\ghost.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; Windows 10 et 11, 64 bits
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Aucune télémétrie, aucune connexion réseau
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
; Application WPF autonome (le runtime .NET est inclus : aucune dépendance à installer)
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Version console + kit de création du support USB WinPE
Source: "..\publish\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\tools\winpe\*"; DestDir: "{app}\winpe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Créer le support USB de démarrage (WinPE)"; Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\winpe\Build-WinPE-USB.ps1"""; \
  WorkingDir: "{app}\winpe"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Documentation"; Filename: "{app}\docs\UTILISATION.md"
Name: "{group}\Désinstaller {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Lancer {#AppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; Les journaux (dans %LocalAppData%\GhostClonage\logs) sont conservés volontairement : ce sont vos données.
Type: filesandordirs; Name: "{app}"
