# Konnor Launcher

Launcher de Minecraft para Windows pensado para servidores privados, junto con el servidor que
le distribuye los archivos y un gestor de servidores de Minecraft.

| Carpeta | Contenido |
|---|---|
| [`Launcher/`](Launcher/) | El launcher: C# / .NET 10 / WPF con CmlLib.Core, instalador con Inno Setup |
| [`LauncherBackEnd/`](LauncherBackEnd/) | Servidor de actualización (Python, FastAPI + uvicorn): publica el manifest y los archivos del cliente |
| [`ServerManager/`](ServerManager/) | Gestor de servidores de Minecraft (Python, PySide6) |

## Cómo encajan

1. `LauncherBackEnd` publica `client_manifest.json` y los archivos del cliente (mods, versiones,
   librerías y assets en bloques ZIP). El manifest se actualiza solo al arrancar el servidor y
   cada vez que cambia la carpeta del cliente.
2. Con **VERIFICAR ARCHIVOS AL INICIAR** activado, el launcher descarga el manifest, compara los
   archivos del jugador y descarga lo que falte: varios bloques a la vez, extrayéndolos en
   paralelo y reanudando las descargas interrumpidas.
3. Después, el launcher instala la versión de Minecraft y su Java con CmlLib y arranca el juego.

Cada carpeta tiene su propio README con los detalles para compilar, configurar y ejecutar.
