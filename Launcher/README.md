# Konnor Launcher

Launcher de Minecraft para Windows pensado para servidores privados: se juega con cuenta offline
y no envía ningún tipo de telemetría. Descarga cualquier versión de Minecraft junto con el Java
que esa versión necesita, y mantiene el cliente sincronizado con el servidor (mods, versiones y
assets) para que todos los jugadores usen los mismos archivos.

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
  KonnorLauncher.Core/         ← lógica sin UI: configuración, verificación, CmlLib, flujo de JUGAR
  KonnorLauncher.App/          ← interfaz WPF (MVVM), assets y launcher.properties por defecto
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
- incluye desinstalador, que nunca toca la carpeta del juego.

## Configuración (`launcher.properties`)

El archivo se encuentra junto al ejecutable. Mantiene las mismas claves que la versión en Python,
por lo que las instalaciones existentes siguen funcionando:

| Clave | Uso |
|---|---|
| `game.path` | Carpeta del juego (vacía = `%APPDATA%\.minecraft`) |
| `java.path` | Vacía = se usa el Java de Mojang adecuado para cada versión; con una ruta, se usa ese Java |
| `minecraft.version` | Versión seleccionada |
| `server.url` | Servidor de actualización (`/client_manifest.json`, `/client/...`, `/client/packages/...`) |
| `enable.verify` | `true` = sincroniza el cliente con el servidor antes de jugar |
| `delete_unauthorized` | `true` = borra de `mods`, `versions`, `libraries` y `assets` lo que no esté en el manifest (nunca en la `.minecraft` oficial) |
| `java.memory` | Memoria máxima para el juego (`4G`) |
| `player.username` | Nombre del jugador offline (de 4 a 16 caracteres: letras, números y `_`) |

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
