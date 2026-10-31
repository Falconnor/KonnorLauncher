"""Backend de Konnor Launcher: publica el manifest y los archivos del cliente.

Endpoints (el launcher depende de estas rutas exactas):

- ``GET /``                         estado del servidor, versión publicada y estado de la actualización
- ``GET /client_manifest.json``     manifest (se regenera solo al arrancar y al cambiar el cliente)
- ``GET /client/{ruta}``            archivos de la carpeta del cliente (mods, versions, libraries...)
- ``GET /client/packages/{bloque}`` bloques ZIP de assets
- ``POST /admin/regenerate``        regeneración bajo demanda, solo desde el propio equipo (panel)

La configuración (carpeta del cliente, puerto, bloques, actualización automática...) se describe en
``Services/settings.py``: ``backends.json`` (lo edita el panel) o variables de entorno. Cada proceso
sirve un solo backend: el de ``KONNOR_PROFILE`` o, si no está, el seleccionado en el panel.
Arranque: ``iniciar_panel.bat``, ``iniciar_servidor.bat`` o ``iniciar_desarrollo.bat``.
"""

import asyncio
import json
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from Services.auto_update import AutoUpdater
from Services.settings import load_settings


settings = load_settings()
logger = logging.getLogger("konnor.backend")
# Los mensajes de la actualización automática salen en la misma consola que los de uvicorn.
logger.setLevel(logging.INFO)
if not logger.handlers:
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("%(asctime)s  %(message)s", "%H:%M:%S"))
    logger.addHandler(handler)
    logger.propagate = False


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Al arrancar: actualización automática del manifest. Al cerrar: detiene el vigilante."""
    logger.info("Backend «%s»: carpeta del cliente %s", settings.backend_name, settings.client_dir)
    if not settings.client_dir.is_dir():
        logger.warning("La carpeta del cliente no existe; /client devolverá 404")
    updater = AutoUpdater(settings)
    app.state.updater = updater
    await updater.start()
    yield
    await updater.stop()


app = FastAPI(title="Konnor Launcher Backend", lifespan=lifespan)
_background_tasks: set[asyncio.Task] = set()


@app.get("/")
def root():
    """Estado del servidor. Útil para comprobar desde un navegador que está en línea."""
    client_version = None
    try:
        client_version = json.loads(settings.manifest_file.read_text(encoding="utf-8")).get("client_version")
    except (OSError, ValueError):
        pass
    return {
        "status": "online",
        "server": "Konnor Launcher Backend",
        "backend": {"id": settings.backend_id, "name": settings.backend_name},
        "manifest": settings.manifest_file.is_file(),
        "client_version": client_version,
        "auto_update": app.state.updater.status(),
    }


@app.post("/admin/regenerate", status_code=202)
async def admin_regenerate(request: Request, forzar_bloques: bool = False):
    """Pide una regeneración del manifest (la usa el panel de control).

    Solo se acepta desde el propio equipo: el servidor escucha en todas las interfaces y nadie
    de fuera debe poder lanzar una regeneración. Responde enseguida; el progreso se ve en ``/``.
    """
    if request.client is None or request.client.host not in ("127.0.0.1", "::1"):
        raise HTTPException(status_code=403, detail="Solo disponible desde el propio equipo")
    reason = "panel de control, rehaciendo todos los bloques" if forzar_bloques else "panel de control"
    task = asyncio.create_task(app.state.updater.regenerate(reason, forzar_bloques))
    _background_tasks.add(task)  # referencia fuerte: si no, la tarea podría recolectarse a medias
    task.add_done_callback(_background_tasks.discard)
    return {"detail": "Regeneración en cola"}


@app.get("/client_manifest.json")
def get_manifest():
    """Manifest del cliente, sin caché: los clientes siempre deben ver la última versión.

    Si todavía no existe responde 503 (se está generando) o 404. En ambos casos el launcher
    muestra "servidor no responde", que es lo correcto: no hay nada que verificar todavía.
    """
    if not settings.manifest_file.is_file():
        if app.state.updater.state == "generating":
            return JSONResponse({"detail": "Generando el manifest"}, status_code=503,
                                headers={"Retry-After": "10"})
        raise HTTPException(status_code=404, detail="Manifest no encontrado")
    return FileResponse(settings.manifest_file, media_type="application/json",
                        headers={"Cache-Control": "no-store"})


# Archivos del cliente. check_dir=False permite arrancar aunque la carpeta no exista todavía.
app.mount("/client", StaticFiles(directory=settings.client_dir, check_dir=False), name="client")
