# Konnor Launcher

Launcher de Minecraft para Windows pensado para servidores privados: se juega con cuenta offline
y no envía ningún tipo de telemetría. Organiza el juego en **instancias** (como el launcher de
Modrinth): cada una tiene su versión de Minecraft, su cargador de mods (Fabric, Forge, NeoForge o
Quilt) y su propia carpeta de mods, configuración y mundos. El launcher descarga e instala todo lo
necesario, incluido el Java que pide cada versión, y puede mantener una instancia sincronizada con
el servidor (LauncherBackEnd) para que todos los jugadores usen los mismos archivos.

La interfaz usa el mismo diseño glassmorphism que el panel del servidor: paneles de cristal
desenfocado sobre el fondo del launcher, con el verde del launcher solo en acciones y estados.

El proyecto empezó en Python (PySide6 + minecraft-launcher-lib) y fue reescrito en
**C# / .NET 10 / WPF**. Las versiones, librerías, assets y Java se gestionan con
**[CmlLib.Core](https://github.com/CmlLib/CmlLib.Core)**, y el instalador se genera con **Inno Setup**.

## Estructura

```
KonnorLauncher.sln
Directory.Build.props          ← versión del launcher y opciones comunes
Directory.Packages.props       ← versiones de NuGet centralizadas
global.json                    ← SDK de .NET 10 y runner de pruebas
build.ps1                      ← pruebas + publicación + instalador en un solo paso
src/
  KonnorLauncher.Core/         ← lógica sin UI: configuración, instancias, cargadores, verificación, CmlLib, flujo de JUGAR
    Instances/                 ← instancias (instance.json) y sus carpetas
    Loaders/                   ← versiones e instalación de Fabric, Quilt, Forge y NeoForge
  KonnorLauncher.App/          ← interfaz WPF (MVVM), assets y launcher.properties por defecto
    Controls/GlassPanel.cs     ← panel de cristal: desenfoca lo que hay detrás (WPF no tiene backdrop-filter)
    Themes/Glass.xaml          ← estilos glassmorphism (mismos tokens que el panel del servidor)
  KonnorLauncher.VerifyCli/    ← herramienta de consola para diagnosticar el servidor
tests/
  KonnorLauncher.Core.Tests/   ← pruebas unitarias, incluido el verificador contra un servidor HTTP local
installer/
  KonnorLauncher.iss           ← script de Inno Setup
(el CI está en la raíz del repositorio: .github/workflows/build.yml)
```

## Requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Inno Setup](https://jrsoftware.org/isinfo.php) 6.7 o superior (solo para generar el instalador)
- Python 3.12 o superior con pip (solo para empaquetar las herramientas del servidor en el instalador)

## Compilar y probar

```powershell
dotnet build KonnorLauncher.sln
dotnet test --project tests/KonnorLauncher.Core.Tests
dotnet run --project src/KonnorLauncher.App
```

## Generar el instalador

```powershell
./build.ps1 -ServerUrl "https://tu-servidor:puerto"
```

El script ejecuta las pruebas, publica el launcher como un exe autocontenido (no requiere .NET
instalado) y genera `artifacts/KonnorLauncher-Setup-<versión>.exe`. El instalador:

- instala en `%APPDATA%\KonnorLauncher` sin pedir permisos de administrador;
- conserva el `launcher.properties` de cada jugador al reinstalar o actualizar;
- incluye desinstalador, que nunca toca las instancias ni los archivos del juego;
- ofrece, desmarcados por defecto, el **panel del servidor** (`LauncherBackEnd`) y el **gestor de
  servidores** (`ServerManager`). Se instalan en `herramientas\` con su propio Python (el embebible
  de python.org, con las dependencias ya incluidas) y se abren desde sus accesos directos del menú
  Inicio y del escritorio: no hace falta tener Python ni ejecutar ningún `.bat`.

`build.ps1 -SkipServerTools` genera el instalador solo con el launcher. La versión de Python que se
empaqueta se cambia con `-PythonVersion`.

## Instancias y carpetas

Todo se guarda en `%APPDATA%\KonnorLauncher`:

```
instances/<id>/        ← una carpeta por instancia: instance.json, mods, config, saves, resourcepacks...
meta/                  ← compartido entre instancias (como en Modrinth)
  versions/  libraries/  assets/  runtime/   (runtime = Java de Mojang)
```

- **Crear una instancia**: nombre, versión de Minecraft (barra que se despliega, con buscador),
  cargador de mods (Vanilla, Fabric, Forge, NeoForge o Quilt) y, si hay cargador, su versión:
  **Estable**, **Última** u **Otra** (lista de todas las disponibles para esa versión de Minecraft).
- **Dependencias automáticas**: al pulsar JUGAR se instala el cargador (Fabric y Quilt desde sus
  servicios oficiales; Forge y NeoForge con su instalador oficial, mediante CmlLib.Core.Installer.Forge)
  y después CmlLib descarga la versión de Minecraft, librerías, assets y el Java adecuado. Lo que
  ya está en `meta/` no se vuelve a descargar.
- **Sincronizar con el servidor** (casilla por instancia): antes de jugar se verifican los archivos
  con LauncherBackEnd. La versión, el cargador y si se borra lo que no está en el manifest los
  decide el servidor (`delete_unauthorized` en el manifest; si un servidor antiguo no lo publica, se borra). Una instancia sincronizada guarda sus propios `versions`, `libraries` y
  `assets` dentro de su carpeta, porque la verificación borra lo que no está en el manifest y nunca
  debe tocar los archivos compartidos de las demás.
- **Instalar paquetes de recursos** (casilla de una instancia sincronizada): descarga los
  `resourcepacks` y `shaderpacks` que publica el servidor. Solo se instalan los que no haya ya con el
  mismo nombre y nunca se borran los del jugador. Si el servidor los protege (`protect_packs`), la
  casilla queda marcada y bloqueada, y los packs se reparan y limpian como el resto de sus archivos.
- **Al abrir el launcher** se consulta el manifest de cada instancia sincronizada (sin descargar
  archivos): se aplica la política actual del servidor y, si anuncia una `client_version` posterior
  a la instalada, la instancia se marca con **Actualización disponible**. La versión instalada solo
  cambia cuando una sincronización termina de descargar los archivos.
- **Sin conexión**: si el cargador ya se instaló, la instancia se puede jugar sin internet.
- **Cuenta sin conexión**: al pulsar el jugador en la barra lateral se cambian el nombre, la foto
  de perfil y la skin. La skin se valida (PNG de 64×64 o 64×32) y se muestra en 3D, girando y con
  sus capas exteriores; se puede arrastrar para girarla y elegir el modelo clásico o delgado. En
  una cuenta sin conexión el juego no descarga skins: dentro del juego solo se ve con un mod de skins.
- **Migración**: la primera vez, si `launcher.properties` tenía una versión o la verificación
  activada, esa configuración se convierte en la instancia "Konnor".

La variable `KONNOR_DATA_DIR` cambia la carpeta de datos (útil para pruebas o una instalación portátil).

## Configuración (`launcher.properties`)

El archivo se encuentra junto al ejecutable y guarda los ajustes del jugador, comunes a todas las
instancias. Nombre, foto de perfil y skin se editan en la **cuenta** (pulsando el jugador, abajo a
la izquierda de la barra lateral); memoria y Java, en **Opciones**:

| Clave | Uso |
|---|---|
| `player.username` | Nombre del jugador offline (de 4 a 16 caracteres: letras, números y `_`) |
| `java.memory` | Memoria máxima para el juego (`4G`) |
| `java.path` | Vacía = se usa el Java de Mojang adecuado para cada versión; con una ruta, se usa ese Java |
| `player.skin` | Skin del jugador: PNG de 64×64 o 64×32 (se copia a `player\skin.png` en la carpeta de datos) |
| `player.skin.model` | Modelo de la skin: `classic` (brazos de 4 píxeles) o `slim` (3 píxeles) |
| `player.avatar` | Foto de perfil (se guarda recortada en `player\avatar.png`) |
| `instance.selected` | Instancia seleccionada en la barra lateral |
| `server.url` | Dirección que se propone al marcar "Sincronizar con el servidor" en una instancia nueva |

`minecraft.version`, `game.path`, `enable.verify` y `delete_unauthorized` son de la versión
anterior: solo se leen para migrarlos a la primera instancia (y los usa la herramienta de diagnóstico).

## Diagnosticar el servidor

El proyecto incluye una herramienta de consola para comprobar el servidor sin abrir la interfaz:

```powershell
dotnet run --project src/KonnorLauncher.VerifyCli -- --dry-run --properties ruta\launcher.properties
```

Indica cuántos bloques y archivos habría que reparar sin tocar el disco. Devuelve 0 si todo está
bien y 1 si algo falla; con `--verbose` muestra el detalle de los errores.

## Logs

Los logs se guardan en `%APPDATA%\KonnorLauncher\logs\`: `launcher-*.log` (uno por día, se
conservan 7) y `game-latest.log` (la salida del juego). Ninguno sale del equipo.
