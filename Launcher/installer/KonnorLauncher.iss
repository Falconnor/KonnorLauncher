; ═══════════════════════════════════════════════════════════════════════════
; Instalador de Konnor Launcher (Inno Setup 6.7 o superior).
; Se compila con:  ISCC.exe installer\KonnorLauncher.iss   (o con build.ps1, que antes publica)
;
; Qué hace:
;  - Instala en %APPDATA%\KonnorLauncher, sin pedir permisos de administrador.
;  - Copia "Konnor Launcher.exe" e icon.ico; launcher.properties SOLO si no existe,
;    así cada jugador conserva su configuración al reinstalar o actualizar.
;  - Crea el acceso directo en el escritorio (directorio de trabajo = instalación).
;  - Trae desinstalador, que nunca borra las instancias (instances\), los archivos del juego
;    compartidos (meta\) ni launcher.properties: los mundos de los jugadores se conservan.
; ═══════════════════════════════════════════════════════════════════════════

#define AppName "Konnor Launcher"
#define AppExeName "Konnor Launcher.exe"
#define PublishDir "..\publish"
#define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExeName)

[Setup]
; AppId fijo: con él Windows reconoce el launcher para actualizar y desinstalar. No debe cambiarse nunca.
AppId={{37649CE1-D4E8-4217-BEA5-A1ED9743642F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Falconnor
VersionInfoVersion={#AppVersion}
DefaultDirName={userappdata}\KonnorLauncher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=KonnorLauncher-Setup-{#AppVersion}
SetupIconFile=..\src\KonnorLauncher.App\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
; Firma de código: activar cuando haya certificado. Ejemplo:
; SignTool=firma $f
; SignedUninstaller=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
; Marcada por defecto: el acceso directo se crea siempre salvo que se desmarque.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Restos de versiones anteriores, cuando el launcher se llamaba "Fkonnor Launcher" (si existieran).
Type: filesandordirs; Name: "{app}\_internal"
Type: files; Name: "{app}\Instalar Fkonnor Launcher.exe"
Type: files; Name: "{app}\Fkonnor Launcher.exe"
Type: files; Name: "{autodesktop}\Fkonnor Launcher.lnk"
Type: files; Name: "{autoprograms}\Fkonnor Launcher.lnk"

[Files]
; Todo lo publicado excepto launcher.properties...
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "launcher.properties,*.pdb"; \
  Flags: ignoreversion recursesubdirs createallsubdirs
; ...que se instala solo si no existe y nunca se desinstala (es la configuración del jugador).
Source: "{#PublishDir}\launcher.properties"; DestDir: "{app}"; \
  Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\src\KonnorLauncher.App\Assets\icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Solo se borran los logs del launcher. Las instancias (con sus mundos) y meta\ nunca se tocan.
Type: filesandordirs; Name: "{app}\logs"
