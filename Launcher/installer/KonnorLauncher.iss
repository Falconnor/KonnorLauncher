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
;  - Componentes opcionales, desmarcados por defecto: el panel del servidor (LauncherBackEnd) y el
;    gestor de servidores (ServerManager), en herramientas\ con un Python propio (build.ps1 los
;    prepara en publish-herramientas\). Se abren desde sus accesos directos, sin instalar nada más.
; ═══════════════════════════════════════════════════════════════════════════

#define AppName "Konnor Launcher"
#define AppExeName "Konnor Launcher.exe"
#define PublishDir "..\publish"
#define ToolsDir "..\publish-herramientas"
#define HasTools DirExists(AddBackslash(SourcePath) + ToolsDir)
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
; Windows 10 1809 o posterior: lo necesita WebView2 (página de los proyectos de Modrinth).
MinVersion=10.0.17763
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

#if HasTools
[Types]
Name: "jugador"; Description: "Solo el launcher (jugadores)"
Name: "completa"; Description: "Launcher y herramientas del servidor"
Name: "personalizada"; Description: "Personalizada"; Flags: iscustom

[Components]
Name: "launcher"; Description: "Konnor Launcher"; Types: jugador completa personalizada; Flags: fixed
Name: "panel"; Description: "Panel del servidor (para alojar el servidor de actualización)"; Types: completa
Name: "gestor"; Description: "Gestor de servidores de Minecraft"; Types: completa

#endif
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
#if HasTools
; El Python de las herramientas se reemplaza entero: sin restos de paquetes de la versión anterior.
Type: filesandordirs; Name: "{app}\herramientas\python"; Components: panel or gestor
#endif

[Files]
; Todo lo publicado excepto launcher.properties...
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "launcher.properties,*.pdb"; \
  Flags: ignoreversion recursesubdirs createallsubdirs
; ...que se instala solo si no existe y nunca se desinstala (es la configuración del jugador).
Source: "{#PublishDir}\launcher.properties"; DestDir: "{app}"; \
  Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\src\KonnorLauncher.App\Assets\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
#if HasTools
Source: "{#ToolsDir}\python\*"; DestDir: "{app}\herramientas\python"; Components: panel or gestor; \
  Flags: ignoreversion recursesubdirs createallsubdirs
; La versión publicada del cliente la edita el panel: se conserva al actualizar.
Source: "{#ToolsDir}\backend\*"; DestDir: "{app}\herramientas\backend"; Excludes: "client_version.txt"; \
  Components: panel; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ToolsDir}\backend\Services\client_version.txt"; DestDir: "{app}\herramientas\backend\Services"; \
  Components: panel; Flags: onlyifdoesntexist uninsneveruninstall
; Igual con la configuración de la API de autenticación del gestor.
Source: "{#ToolsDir}\servermanager\*"; DestDir: "{app}\herramientas\servermanager"; Excludes: "auth_config.json"; \
  Components: gestor; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ToolsDir}\servermanager\auth\auth_config.json"; DestDir: "{app}\herramientas\servermanager\auth"; \
  Components: gestor; Flags: onlyifdoesntexist uninsneveruninstall
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon
#if HasTools
; El panel abre una consola (cerrarla detiene el panel y sus servidores); el gestor, solo su ventana.
Name: "{autoprograms}\Panel del servidor"; Filename: "{app}\herramientas\python\python.exe"; Parameters: "-m Panel"; \
  WorkingDir: "{app}\herramientas\backend"; IconFilename: "{app}\icon.ico"; Components: panel
Name: "{autodesktop}\Panel del servidor"; Filename: "{app}\herramientas\python\python.exe"; Parameters: "-m Panel"; \
  WorkingDir: "{app}\herramientas\backend"; IconFilename: "{app}\icon.ico"; Components: panel; Tasks: desktopicon
Name: "{autoprograms}\Gestor de servidores"; Filename: "{app}\herramientas\python\pythonw.exe"; Parameters: "server_manager.py"; \
  WorkingDir: "{app}\herramientas\servermanager"; IconFilename: "{app}\icon.ico"; Components: gestor
Name: "{autodesktop}\Gestor de servidores"; Filename: "{app}\herramientas\python\pythonw.exe"; Parameters: "server_manager.py"; \
  WorkingDir: "{app}\herramientas\servermanager"; IconFilename: "{app}\icon.ico"; Components: gestor; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Solo se borran los logs del launcher. Las instancias (con sus mundos) y meta\ nunca se tocan.
Type: filesandordirs; Name: "{app}\logs"
; De las herramientas, solo el Python y la caché de código. Los servidores del panel (servidores.json,
; profiles\) y los del gestor (server\) se conservan.
Type: filesandordirs; Name: "{app}\herramientas\python"
Type: filesandordirs; Name: "{app}\herramientas\backend\Panel\__pycache__"
Type: filesandordirs; Name: "{app}\herramientas\backend\Services\__pycache__"
Type: filesandordirs; Name: "{app}\herramientas\servermanager\__pycache__"
Type: filesandordirs; Name: "{app}\herramientas\servermanager\auth\__pycache__"
