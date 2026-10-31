"""API del panel de control (solo en 127.0.0.1).

- ``GET  /api/state``                  estado del servidor, del manifest y de la actividad
- ``GET  /api/logs?after=N``           líneas nuevas del registro
- ``POST /api/server/{start|stop|restart}``
- ``GET  /api/config`` / ``PUT /api/config``   configuración de ``backend_config.json``
- ``PUT  /api/client-version``         versión que anuncia el manifest
- ``POST /api/manifest/regenerate``    regenera (en el servidor si está en marcha; si no, aquí)
- ``POST /api/open``                   abre la carpeta del cliente o del backend en el Explorador
"""

from __future__ import annotations

import asyncio
import json
import os
import socket
import sys
import threading
import time
import urllib.error
import urllib.request
from contextlib import asynccontextmanager
from dataclasses import asdict
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from Panel.supervisor import LogBuffer, PanelError, ServerProcess
from Services.generate_manifest import generate_manifest, read_client_version
from Services.settings import (BASE_DIR, DEFAULTS, env_overrides, load_settings, raw_config,
                               read_config_file, save_config_file)


STATIC_DIR = Path(__file__).resolve().parent / "static"
PANEL_PORT = int(os.environ.get("KONNOR_PANEL_PORT", "59380"))

logs = LogBuffer()
server = ServerProcess(logs)


@asynccontextmanager
async def lifespan(app: FastAPI):
    logs.add("Panel de control listo", source="panel", level="info")
    yield
    # Al cerrar el panel se detiene también el servidor que arrancó.
    await asyncio.to_thread(server.stop)


app = FastAPI(title="Konnor Launcher Backend - Panel", lifespan=lifespan, docs_url=None, redoc_url=None)


# ── Protección del panel ────────────────────────────────────────────────────
# El panel solo escucha en 127.0.0.1, pero una web abierta en el navegador podría intentar
# llamarlo. Se rechaza cualquier Host que no sea local (DNS rebinding) y las acciones exigen una
# cabecera propia, que un formulario o un fetch de otro origen no pueden enviar sin permiso CORS.

_ALLOWED_HOSTS = {f"127.0.0.1:{PANEL_PORT}", f"localhost:{PANEL_PORT}"}


@app.middleware("http")
async def local_only(request: Request, call_next):
    if request.headers.get("host", "") not in _ALLOWED_HOSTS:
        return JSONResponse({"detail": "Host no permitido"}, status_code=403)
    if request.method != "GET" and request.headers.get("x-konnor-panel") != "1":
        return JSONResponse({"detail": "Falta la cabecera del panel"}, status_code=403)
    return await call_next(request)


# ── Sondeo del servidor ─────────────────────────────────────────────────────

def _probe_host(host: str) -> str:
    return "127.0.0.1" if host in ("0.0.0.0", "::", "localhost") else host


def _backend_request(method: str, path: str, timeout: float = 1.5) -> dict | None:
    """Petición al servidor del backend; None si no responde."""
    settings = load_settings(use_env=False)
    url = f"http://{_probe_host(settings.host)}:{settings.port}{path}"
    request = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8") or "{}")
    except (urllib.error.URLError, OSError, ValueError):
        return None


def _lan_addresses() -> list[str]:
    """Direcciones IPv4 de este equipo en la red local (sin la de loopback)."""
    try:
        infos = socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
    except OSError:
        return []
    return sorted({info[4][0] for info in infos if not info[4][0].startswith("127.")})


# ── Manifest ────────────────────────────────────────────────────────────────

_manifest_cache: dict = {"key": None, "data": None}


def _manifest_summary(path: Path) -> dict:
    """Resumen del manifest publicado. Se relee solo si el archivo cambió."""
    try:
        stat = path.stat()
    except FileNotFoundError:
        return {"exists": False, "path": str(path)}
    key = (str(path), stat.st_mtime_ns, stat.st_size)
    if _manifest_cache["key"] == key:
        return _manifest_cache["data"]

    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        return {"exists": True, "path": str(path), "error": f"No se pudo leer: {error}"}

    files = manifest.get("files") or {}
    blocks = manifest.get("asset_blocks") or {}
    folders: dict[str, int] = {}
    for relative in files:
        top = relative.split("/")[0] if "/" in relative else "(raíz)"
        folders[top] = folders.get(top, 0) + 1
    data = {
        "exists": True,
        "path": str(path),
        "modified": stat.st_mtime,
        "client_version": manifest.get("client_version"),
        "files": len(files),
        "files_size": sum(int(f.get("size", 0)) for f in files.values()),
        "blocks": len(blocks),
        "blocks_size": sum(int(b.get("size", 0)) for b in blocks.values()),
        "assets": sum(len(b.get("files") or {}) for b in blocks.values()),
        "folders": dict(sorted(folders.items(), key=lambda item: -item[1])),
    }
    _manifest_cache.update(key=key, data=data)
    return data


# Regeneración local (cuando el servidor está detenido).
_local = {"running": False, "result": None, "finished": None}


def _run_local_generation(force_blocks: bool) -> None:
    settings = load_settings(use_env=False)
    log = lambda text: logs.add(str(text), source="panel")
    log("Regenerando el manifest desde el panel (servidor detenido)...")
    try:
        result = generate_manifest(settings, force_blocks, log=log)
    except Exception as error:  # el panel nunca debe caerse por una generación
        logs.add(f"Error generando el manifest: {error}", source="panel", level="error")
        result = None
    else:
        if result.ok:
            log(f"Manifest {'actualizado' if result.changed else 'sin cambios'} en {result.seconds:.1f} s")
        else:
            logs.add(f"No se pudo actualizar el manifest: {result.error}", source="panel", level="error")
    _local.update(running=False, result=asdict(result) if result else None, finished=time.time())


# ── Estado ──────────────────────────────────────────────────────────────────

@app.get("/api/state")
async def state():
    settings = load_settings(use_env=False)
    probe = await asyncio.to_thread(_backend_request, "GET", "/")
    online = bool(probe and probe.get("server") == "Konnor Launcher Backend")

    if server.alive:
        if server.stopping:
            status = "stopping"
        elif online:
            status = "running"
        else:
            status = "starting"
    elif online:
        status = "external"  # responde un backend que no arrancó este panel (p. ej. el .bat)
    elif server.crashed:
        status = "crashed"
    else:
        status = "stopped"

    current = raw_config(use_env=False)
    restart_required = server.alive and server.config_at_start is not None and server.config_at_start != current
    port = settings.port
    lan = _lan_addresses() if settings.host in ("0.0.0.0", "::") else [settings.host]

    return {
        "server": {
            "status": status,
            "pid": server.pid,
            "started_at": server.started_at if server.alive else None,
            "exit_code": server.exit_code,
            "host": settings.host,
            "port": port,
            "local_url": f"http://{_probe_host(settings.host)}:{port}",
            "lan_urls": [f"http://{ip}:{port}" for ip in lan],
            "restart_required": restart_required,
        },
        "auto_update": probe.get("auto_update") if online else None,
        "local_generation": dict(_local),
        "manifest": _manifest_summary(settings.manifest_file),
        "client": {
            "dir": str(settings.client_dir),
            "exists": settings.client_dir.is_dir(),
            "version": read_client_version(settings),
        },
        "activity": logs.activity.as_dict(),
        "now": time.time(),
    }


@app.get("/api/logs")
def get_logs(after: int = 0):
    lines, last = logs.since(after)
    return {"lines": lines, "last": last}


@app.post("/api/logs/clear")
def clear_logs():
    logs.clear()
    return {"ok": True}


# ── Servidor ────────────────────────────────────────────────────────────────

@app.post("/api/server/{action}")
async def server_action(action: str):
    if action not in ("start", "stop", "restart"):
        raise HTTPException(404, "Acción desconocida")
    if action != "stop" and _local["running"]:
        raise HTTPException(409, "Espera a que termine la regeneración del manifest")
    try:
        await asyncio.to_thread(getattr(server, action))
    except PanelError as error:
        logs.add(str(error), source="panel", level="error")
        raise HTTPException(409, str(error)) from None
    return {"ok": True}


# ── Configuración ───────────────────────────────────────────────────────────

class ConfigBody(BaseModel):
    values: dict


@app.get("/api/config")
def get_config():
    return {
        "values": {**DEFAULTS, **read_config_file()},
        "defaults": DEFAULTS,
        "base_dir": str(BASE_DIR),
        # Variables KONNOR_* del entorno: afectan a los .bat, no al servidor que arranca el panel.
        "env_overrides": env_overrides(),
    }


@app.put("/api/config")
def put_config(body: ConfigBody):
    try:
        values = save_config_file(body.values)
    except ValueError as error:
        raise HTTPException(422, str(error)) from None
    logs.add("Configuración guardada", source="panel", level="info")
    return {"values": values, "restart_required": server.alive and server.config_at_start != values}


class VersionBody(BaseModel):
    version: str


@app.put("/api/client-version")
async def put_client_version(body: VersionBody):
    version = body.version.strip()
    if not version or len(version) > 64 or any(c.isspace() for c in version):
        raise HTTPException(422, "La versión no puede estar vacía ni contener espacios (máx. 64 caracteres)")
    settings = load_settings(use_env=False)
    settings.version_file.parent.mkdir(parents=True, exist_ok=True)
    settings.version_file.write_text(version + "\n", encoding="utf-8")
    logs.add(f"Versión del cliente cambiada a {version}", source="panel", level="info")
    # El archivo de versión no está en la carpeta vigilada: se pide la regeneración expresamente.
    queued = await asyncio.to_thread(_backend_request, "POST", "/admin/regenerate")
    return {"version": version, "regenerating": queued is not None}


# ── Manifest ────────────────────────────────────────────────────────────────

class RegenerateBody(BaseModel):
    force_blocks: bool = False


@app.post("/api/manifest/regenerate")
async def regenerate(body: RegenerateBody):
    path = "/admin/regenerate" + ("?forzar_bloques=true" if body.force_blocks else "")
    if await asyncio.to_thread(_backend_request, "POST", path) is not None:
        return {"where": "server"}
    if server.alive:
        raise HTTPException(409, "El servidor está arrancando; inténtalo en unos segundos")
    if _local["running"]:
        raise HTTPException(409, "Ya hay una regeneración en curso")
    _local.update(running=True, result=None)
    threading.Thread(target=_run_local_generation, args=(body.force_blocks,), daemon=True).start()
    return {"where": "panel"}


class OpenBody(BaseModel):
    target: str


@app.post("/api/open")
def open_folder(body: OpenBody):
    settings = load_settings(use_env=False)
    targets = {"client": settings.client_dir, "backend": BASE_DIR}
    folder = targets.get(body.target)
    if folder is None:
        raise HTTPException(404, "Destino desconocido")
    if not folder.is_dir():
        raise HTTPException(404, f"No existe la carpeta {folder}")
    if sys.platform == "win32":
        os.startfile(folder)  # noqa: S606 - ruta conocida, solo carpetas del backend
    return {"ok": True}


# ── Interfaz ────────────────────────────────────────────────────────────────

@app.get("/api/ping")
def ping():
    return {"panel": "Konnor Launcher Backend"}


@app.get("/")
def index():
    return FileResponse(STATIC_DIR / "index.html", headers={"Cache-Control": "no-store"})


app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
