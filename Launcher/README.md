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
    Modrinth/                  ← búsqueda e instalación de contenido de Modrinth (Explorar)
    Players/                   ← skins: validación, galería de MineSkin y skins descargadas
    Servers/                   ← directorio de servidores, estado en vivo, ping, servers.dat y Favoritos
    Hosting/                   ← servidores alojados en este equipo y publicación de su cliente (Kestrel)
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
                         (icon.png: icono del modpack con que se creó; servers.dat: su lista Multijugador)
meta/                  ← compartido entre instancias (como en Modrinth)
  versions/  libraries/  assets/  runtime/   (runtime = Java de Mojang)
player/                ← skin y foto de perfil del jugador
  skins/               ← skins descargadas (sin aplicar) y su índice downloaded.json
servers.json           ← Favoritos de la sección Servidores y la instancia creada para cada servidor
hosting/<id>/          ← un servidor de la sección Hosting: konnor-server.json, server.jar, world, mods o plugins...
  cliente/             ← archivos que reciben los jugadores además de los mods (config, resourcepacks...)
  .konnor/             ← manifest publicado y caché de hashes
```

Los iconos de Modrinth y las texturas de la galería de skins se guardan en una caché aparte,
`%LOCALAPPDATA%\KonnorLauncher\cache`, que se puede borrar sin perder nada.

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
- **Menú de la instancia**: con clic derecho sobre una instancia de la barra lateral (o la tecla de
  menú) se abre un menú con Jugar, Abrir carpeta, Editar instancia y Eliminar instancia, sin cambiar
  la instancia seleccionada.
- **Migración**: la primera vez, si `launcher.properties` tenía una versión o la verificación
  activada, esa configuración se convierte en la instancia "Konnor".

La variable `KONNOR_DATA_DIR` cambia la carpeta de datos (útil para pruebas o una instalación portátil).

## Explorar, Skins, Servidores y Hosting

La barra lateral da acceso a cuatro secciones además de las instancias (Hosting ocupa el lugar que
tenía Shaders; los shaders siguen en la pestaña Shaders de Explorar):

- **Explorar (Mods, Shaders y el resto de pestañas)**: busca en [Modrinth](https://modrinth.com) modpacks,
  mods, paquetes de recursos, data packs y shaders, con filtros (versión, cargador, categoría, entorno)
  y orden. Los mods se instalan en la instancia elegida con sus dependencias; un modpack crea una
  instancia nueva con su versión, su cargador, sus archivos y su icono.
- **Skins**: galería pública de [MineSkin](https://mineskin.org) (pestañas Recientes y Populares, con
  búsqueda) y la pestaña **Descargadas**. La skin elegida se ve en 3D: gira sola, se orienta con el
  botón pulsado y se acerca con la rueda o con el deslizador de zoom. **Usar esta skin** la aplica a la
  cuenta; **Descargar skin** la guarda en Descargadas sin aplicarla, y desde ahí se puede borrar.
- **Servidores**: directorio de servidores de Java de Modrinth (API v3: dirección, jugadores conectados,
  región, idiomas y modpack de cada servidor), con búsqueda y filtros por orden, región, versión,
  categoría e idioma. Pestañas:
  - **Premium**: servidores que piden cuenta de Minecraft premium. Con las cuentas sin conexión del
    launcher el servidor rechaza la conexión.
  - **No premium**: servidores en modo offline, a los que se entra con las cuentas del launcher.
  - **Explorar**: todos los servidores.
  - **Favoritos**: direcciones guardadas (con la estrella de un servidor o escribiéndolas), con su
    estado en vivo de [mcstatus.io](https://mcstatus.io): jugadores, versión, mensaje e icono.

  **Jugar** en un servidor del directorio crea (o reutiliza) su instancia con su modpack o su versión,
  lo añade a la lista Multijugador del juego (`servers.dat`) y abre el juego entrando directamente al
  servidor. En Favoritos, Jugar lo añade a la instancia seleccionada. Cada servidor muestra el ping
  desde este equipo (tiempo de abrir la conexión): verde hasta 80 ms, amarillo hasta 120 ms y rojo
  por encima. Los servidores que solo publican su puerto con un registro SRV pueden no mostrar ping.
- **Hosting**: crear y gestionar servidores de Minecraft en este equipo. Reúne en el launcher lo que
  hacían ServerManager (crear, descargar e iniciar servidores) y LauncherBackEnd (publicar el cliente
  para la sincronización), sin necesitar Python ni esos proyectos. Ver [Hosting](#hosting).

## Hosting

**Crear servidor**: nombre, software, versión de Minecraft y del cargador (o build de Paper), memoria,
puerto, MOTD, si solo admite cuentas premium y la aceptación del EULA de Minecraft. El launcher lo
descarga e instala con las fuentes oficiales:

| Software | Instalación |
|---|---|
| Vanilla | `server.jar` de Mojang (comprobado con su SHA-1) |
| Paper | API Fill v3 de PaperMC (comprobado con su SHA-256) |
| Fabric | Lanzador de servidor de `meta.fabricmc.net` y el `server.jar` de Mojang |
| Quilt | Instalador oficial (`install server … --download-server`) |
| Forge / NeoForge | Instalador oficial (`--installServer`); se arranca con su `win_args.txt` |

El Java no se busca en el sistema: es el de Mojang que corresponde a la versión (el mismo que usa el
juego, en `meta/runtime`), y CmlLib lo descarga si falta. Los servidores nuevos aceptan cuentas sin
conexión (`online-mode=false`) y empiezan sin lista blanca.

**Mis servidores** lista los servidores con su estado, jugadores conectados, tiempo activo y dirección.
Cada servidor tiene su página:

- **Consola**: la salida del servidor en vivo (avisos en amarillo, errores en rojo) y un campo de
  comandos con historial (flechas arriba y abajo). Iniciar, detener (con `stop`, que guarda el
  mundo), reiniciar y forzar el cierre.
- **Configuración**: `server.properties` (MOTD, puerto, jugadores, distancia, dificultad, modo de
  juego, premium, lista blanca, vuelo, extremo, semilla) conservando el resto del archivo y sus
  comentarios; memoria e inicio automático al abrir el launcher; reinstalar y eliminar. PvP y bloques
  de comandos solo aparecen en las versiones que todavía los tienen en `server.properties` (desde la
  26.1 son reglas del juego).
- **Jugadores**: todos los que entraron alguna vez (los archivos de `playerdata` del mundo y sus nombres
  de `usercache.json`), con un punto verde si están conectados y gris si no; operador, expulsar y
  banear, y acciones por nombre (operador, lista blanca, desbanear).
- **Contenido**: mods o plugins (activar o desactivar, quitar) y accesos a las carpetas del servidor,
  del mundo y del cliente. **Añadir mod** (**Añadir plugin** en Paper, **Explorar contenido** en Vanilla)
  abre Explorar para ese servidor: a la derecha de las pestañas se avisa «Instalando en …» y todas las
  pestañas instalan en él, con la versión de Minecraft del servidor (y, en mods y modpacks, su cargador y
  el entorno «Servidor») marcados y bloqueados, para que no aparezca nada incompatible:

  | Pestaña | Destino |
  |---|---|
  | Mods | `mods` (o `plugins` en Paper), con sus dependencias |
  | Modpacks | Los archivos del servidor y `overrides` + `server-overrides` en el servidor; los de solo cliente y `client-overrides` en la carpeta del cliente (los reciben los jugadores) |
  | Paquetes de recursos y Shaders | Carpeta del cliente (`resourcepacks`, `shaderpacks`): los reciben los jugadores al sincronizar |
  | Data packs | `datapacks` del mundo del servidor |

  Lo que el servidor no puede cargar (mods en Vanilla, modpacks en Paper o Vanilla) se avisa al abrir la
  pestaña. **Desde un archivo…** copia un `.jar` del equipo.
- **Sincronización**: lo que hacía LauncherBackEnd. Un servidor HTTP (Kestrel, dentro del launcher)
  publica `client_manifest.json` con la versión y el cargador del servidor, sus mods y lo que haya en
  la carpeta `cliente`, con la misma política que el panel (borrar lo no autorizado, requerir conexión,
  bloquear la dirección, paquetes obligatorios) y además **unirse automáticamente** (activado por
  defecto): el manifest publica `auto_join` y `server_address`, y el launcher de cada jugador entra
  directamente al servidor al pulsar JUGAR. Con **paquetes de recursos obligatorios**, el servidor de
  Minecraft también exige el primer `.zip` de `cliente/resourcepacks` (`resource-pack`,
  `resource-pack-sha1` y `require-resource-pack` en `server.properties`, descargado de la propia
  sincronización); un paquete puesto a mano se respeta. La **dirección pública** (opcional, un dominio o
  IP fija) se usa para las dos cosas; vacía, la IP pública detectada. Solo se sirven los archivos del
  manifest, y este se regenera solo cuando la carpeta deja de cambiar unos segundos. Paper y Vanilla
  publican el juego sin cargador.

La cabecera muestra la dirección de internet (consultada a `api.ipify.org`) y la de la red local.
**Abrir el router** abre la página del router (la puerta de enlace) para redirigir los puertos a este
equipo: cada router es distinto y pide su contraseña, así que no se intenta hacer automáticamente. Al
cerrar el launcher con servidores en marcha, se pide confirmación y se detienen guardando sus mundos.

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

`minecraft.version`, `enable.verify` y `cached.client.version` son de la versión anterior: solo se
leen para migrarlos a la primera instancia. `game.path`, `versions.path` y `delete_unauthorized` solo
los usa la herramienta de diagnóstico.

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
