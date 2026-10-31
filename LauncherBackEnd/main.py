"""Backend de Konnor Launcher: publica el manifest y los archivos del cliente.

Endpoints (el launcher depende de estas rutas exactas):

- ``GET /``                         estado del servidor, versión publicada y estado de la actualización
- ``GET /client_manifest.json``     manifest (se regenera solo al arrancar y al cambiar el cliente)
- ``GET /client/{ruta}``            archivos de la carpeta del cliente (mods, versions, libraries...)
- ``GET /client/packages/{bloque}`` bloques ZIP de assets

La configuración (carpeta del cliente, actualización automática...) está en ``Services/settings.py``
y se cambia con variables de entorno. Arranque: ``iniciar_servidor.bat`` o ``iniciar_desarrollo.bat``.
"""

import json
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
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
    logger.info("Carpeta del cliente: %s", settings.client_dir)
    if not settings.client_dir.is_dir():
        logger.warning("La carpeta del cliente no existe; /client devolverá 404")
    updater = AutoUpdater(settings)
    app.state.updater = updater
    await updater.start()
    yield
    await updater.stop()


app = FastAPI(title="Konnor Launcher Backend", lifespan=lifespan)


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
        "manifest": settings.manifest_file.is_file(),
        "client_version": client_version,
        "auto_update": app.state.updater.status(),
    }


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
