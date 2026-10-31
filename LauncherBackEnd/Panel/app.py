"""API del panel de control (solo en 127.0.0.1).

Backends (``backends.json``):

- ``GET    /api/backends``                    lista con el estado de cada uno y el seleccionado
- ``POST   /api/backends``                    crea uno nuevo ``{name}``
- ``PUT    /api/backends/selected``           cambia el seleccionado ``{id}``
- ``DELETE /api/backends/{id}``               lo quita de la lista (no borra archivos)

Un backend (``/api/backends/{id}/...``):

- ``GET  state``                     estado del servidor, del manifest y de la actividad
- ``GET  logs?after=N``              líneas nuevas del registro · ``POST logs/clear``
- ``POST server/{start|stop|restart}``
- ``GET  config`` / ``PUT config``   nombre y configuración
- ``PUT  client-version``            versión que anuncia el manifest
- ``POST manifest/regenerate``       regenera (en su servidor si está en marcha; si no, aquí)
- ``POST open``                      abre la carpeta del cliente en el Explorador
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
from dataclasses import asdict, dataclass, field
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from Panel.supervisor import LogBuffer, PanelError, ServerProcess
from Services.generate_manifest import generate_manifest, read_client_version
from Services.settings import (BASE_DIR, MAX_BLOCKS, Settings, create_profile, delete_profile,
                               env_overrides, get_profile, list_profiles, load_settings,
                               profile_defaults, raw_config, select_profile, update_profile)


STATIC_DIR = Path(__file__).resolve().parent / "static"
PANEL_PORT = int(os.environ.get("KONNOR_PANEL_PORT", "59380"))


# ── Estado en memoria de cada backend ───────────────────────────────────────

@dataclass
class Runtime:
    """Lo que el panel sabe de un backend mientras está abierto: su proceso y su registro."""
    backend_id: str
    logs: LogBuffer = field(default_factory=LogBuffer)
    server: ServerProcess = field(init=False)
    local: dict = field(default_factory=lambda: {"running": False, "result": None, "finished": None})
    manifest_cache: dict = field(default_factory=lambda: {"key": None, "data": None})

    def __post_init__(self):
        self.server = ServerProcess(self.backend_id, self.logs)


_runtimes: dict[str, Runtime] = {}
_runtimes_lock = threading.Lock()


def runtime(backend_id: str) -> Runtime:
    """Estado de un backend existente (404 si no está en backends.json)."""
    try:
        get_profile(backend_id)
    except KeyError:
        raise HTTPException(404, f"No existe el backend '{backend_id}'") from None
    with _runtimes_lock:
        if backend_id not in _runtimes:
            _runtimes[backend_id] = Runtime(backend_id)
            _runtimes[backend_id].logs.add("Panel de control listo", source="panel", level="info")
        return _runtimes[backend_id]


def settings_of(backend_id: str) -> Settings:
    return load_settings(backend_id, use_env=False)


@asynccontextmanager
async def lifespan(app: FastAPI):
    yield
    # Al cerrar el panel se detienen también los servidores que arrancó.
    await asyncio.gather(*(asyncio.to_thread(rt.server.stop) for rt in list(_runtimes.values())))


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


# ── Sondeo de los servidores ────────────────────────────────────────────────

def _probe_host(host: str) -> str:
    return "127.0.0.1" if host in ("0.0.0.0", "::", "localhost") else host


def _backend_request(settings: Settings, method: str, path: str, timeout: float = 1.5) -> dict | None:
    """Petición al servidor de un backend; None si no responde."""
    url = f"http://{_probe_host(settings.host)}:{settings.port}{path}"
    request = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8") or "{}")
    except (urllib.error.URLError, OSError, ValueError):
        return None


def _server_status(rt: Runtime, probe: dict | None) -> str:
    """Estado combinando el proceso hijo y la respuesta del servidor en su puerto."""
    online = bool(probe and probe.get("server") == "Konnor Launcher Backend")
    if rt.server.alive:
        if rt.server.stopping:
            return "stopping"
        return "running" if online else "starting"
    if online:
        return "external"  # responde un backend que no arrancó este panel (p. ej. el .bat)
    return "crashed" if rt.server.crashed else "stopped"


def _lan_addresses() -> list[str]:
    """Direcciones IPv4 de este equipo en la red local (sin la de loopback)."""
    try:
        infos = socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
    except OSError:
        return []
    return sorted({info[4][0] for info in infos if not info[4][0].startswith("127.")})


# ── Manifest ────────────────────────────────────────────────────────────────

def _manifest_summary(rt: Runtime, path: Path) -> dict:
    """Resumen del manifest publicado. Se relee solo si el archivo cambió."""
    try:
        stat = path.stat()
    except FileNotFoundError:
        return {"exists": False, "path": str(path)}
    key = (str(path), stat.st_mtime_ns, stat.st_size)
    if rt.manifest_cache["key"] == key:
        return rt.manifest_cache["data"]

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
    rt.manifest_cache.update(key=key, data=data)
    return data


def _run_local_generation(rt: Runtime, force_blocks: bool) -> None:
    """Regeneración desde el panel, para cuando el servidor del backend está detenido."""
    settings = settings_of(rt.backend_id)
    log = lambda text: rt.logs.add(str(text), source="panel")
    log("Regenerando el manifest desde el panel (servidor detenido)...")
    try:
        result = generate_manifest(settings, force_blocks, log=log)
    except Exception as error:  # el panel nunca debe caerse por una generación
        rt.logs.add(f"Error generando el manifest: {error}", source="panel", level="error")
        result = None
    else:
        if result.ok:
            log(f"Manifest {'actualizado' if result.changed else 'sin cambios'} en {result.seconds:.1f} s")
        else:
            rt.logs.add(f"No se pudo actualizar el manifest: {result.error}", source="panel", level="error")
    rt.local.update(running=False, result=asdict(result) if result else None, finished=time.time())


# ── Lista de backends ───────────────────────────────────────────────────────

@app.get("/api/backends")
async def backends():
    selected, profiles = list_profiles()

    async def summary(profile: dict) -> dict:
        rt = runtime(profile["id"])
        settings = settings_of(profile["id"])
        probe = await asyncio.to_thread(_backend_request, settings, "GET", "/", 1.0)
        return {
            "id": profile["id"],
            "name": profile["name"],
            "host": settings.host,
            "port": settings.port,
            "status": _server_status(rt, probe),
        }

    return {"selected": selected, "backends": await asyncio.gather(*(summary(p) for p in profiles))}


class NameBody(BaseModel):
    name: str


@app.post("/api/backends")
def new_backend(body: NameBody):
    try:
        backend = create_profile(body.name)
    except ValueError as error:
        raise HTTPException(422, str(error)) from None
    runtime(backend["id"]).logs.add(f"Backend «{backend['name']}» creado", source="panel", level="info")
    return backend


class IdBody(BaseModel):
    id: str


@app.put("/api/backends/selected")
def set_selected(body: IdBody):
    try:
        select_profile(body.id)
    except KeyError:
        raise HTTPException(404, f"No existe el backend '{body.id}'") from None
    return {"selected": body.id}


@app.delete("/api/backends/{backend_id}")
def remove_backend(backend_id: str):
    rt = runtime(backend_id)
    if rt.server.alive:
        raise HTTPException(409, "Detén el servidor de este backend antes de quitarlo")
    try:
        delete_profile(backend_id)
    except ValueError as error:
        raise HTTPException(409, str(error)) from None
    with _runtimes_lock:
        _runtimes.pop(backend_id, None)
    return {"ok": True}


# ── Un backend ──────────────────────────────────────────────────────────────

@app.get("/api/backends/{backend_id}/state")
async def state(backend_id: str):
    rt = runtime(backend_id)
    settings = settings_of(backend_id)
    probe = await asyncio.to_thread(_backend_request, settings, "GET", "/")
    status = _server_status(rt, probe)
    online = status in ("running", "external")

    current = raw_config(backend_id, use_env=False)
    server = rt.server
    restart_required = server.alive and server.config_at_start is not None and server.config_at_start != current
    port = settings.port
    lan = _lan_addresses() if settings.host in ("0.0.0.0", "::") else [settings.host]

    return {
        "backend": {"id": settings.backend_id, "name": settings.backend_name},
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
        "auto_update": probe.get("auto_update") if online and probe else None,
        "local_generation": dict(rt.local),
        "manifest": _manifest_summary(rt, settings.manifest_file),
        "client": {
            "dir": str(settings.client_dir),
            "exists": settings.client_dir.is_dir(),
            "version": read_client_version(settings),
            "block_count": settings.block_count,
        },
        "activity": rt.logs.activity.as_dict(),
        "now": time.time(),
    }


@app.get("/api/backends/{backend_id}/logs")
def get_logs(backend_id: str, after: int = 0):
    lines, last = runtime(backend_id).logs.since(after)
    return {"lines": lines, "last": last}


@app.post("/api/backends/{backend_id}/logs/clear")
def clear_logs(backend_id: str):
    runtime(backend_id).logs.clear()
    return {"ok": True}


@app.post("/api/backends/{backend_id}/server/{action}")
async def server_action(backend_id: str, action: str):
    rt = runtime(backend_id)
    if action not in ("start", "stop", "restart"):
        raise HTTPException(404, "Acción desconocida")
    if action != "stop" and rt.local["running"]:
        raise HTTPException(409, "Espera a que termine la regeneración del manifest")
    try:
        await asyncio.to_thread(getattr(rt.server, action))
    except PanelError as error:
        rt.logs.add(str(error), source="panel", level="error")
        raise HTTPException(409, str(error)) from None
    return {"ok": True}


# ── Configuración ───────────────────────────────────────────────────────────

@app.get("/api/backends/{backend_id}/config")
def get_config(backend_id: str):
    runtime(backend_id)
    profile = get_profile(backend_id)
    defaults = profile_defaults(backend_id, profile["port"] if backend_id != "principal" else None)
    return {
        "values": profile,
        "defaults": defaults,
        "max_blocks": MAX_BLOCKS,
        "base_dir": str(BASE_DIR),
        # Variables KONNOR_* del entorno: afectan a los .bat, no a los servidores que arranca el panel.
        "env_overrides": env_overrides(),
    }


class ConfigBody(BaseModel):
    values: dict


@app.put("/api/backends/{backend_id}/config")
def put_config(backend_id: str, body: ConfigBody):
    rt = runtime(backend_id)
    values = {k: v for k, v in body.values.items() if k != "id"}  # el id no cambia nunca
    try:
        saved = update_profile(backend_id, values)
    except ValueError as error:
        raise HTTPException(422, str(error)) from None
    rt.logs.add("Configuración guardada", source="panel", level="info")
    restart = rt.server.alive and rt.server.config_at_start != raw_config(backend_id, use_env=False)
    return {"values": saved, "restart_required": restart}


class VersionBody(BaseModel):
    version: str


@app.put("/api/backends/{backend_id}/client-version")
async def put_client_version(backend_id: str, body: VersionBody):
    rt = runtime(backend_id)
    version = body.version.strip()
    if not version or len(version) > 64 or any(c.isspace() for c in version):
        raise HTTPException(422, "La versión no puede estar vacía ni contener espacios (máx. 64 caracteres)")
    settings = settings_of(backend_id)
    settings.version_file.parent.mkdir(parents=True, exist_ok=True)
    settings.version_file.write_text(version + "\n", encoding="utf-8")
    rt.logs.add(f"Versión del cliente cambiada a {version}", source="panel", level="info")
    # El archivo de versión no está en la carpeta vigilada: se pide la regeneración expresamente.
    queued = await asyncio.to_thread(_backend_request, settings, "POST", "/admin/regenerate")
    return {"version": version, "regenerating": queued is not None}


class RegenerateBody(BaseModel):
    force_blocks: bool = False


@app.post("/api/backends/{backend_id}/manifest/regenerate")
async def regenerate(backend_id: str, body: RegenerateBody):
    rt = runtime(backend_id)
    settings = settings_of(backend_id)
    path = "/admin/regenerate" + ("?forzar_bloques=true" if body.force_blocks else "")
    if await asyncio.to_thread(_backend_request, settings, "POST", path) is not None:
        return {"where": "server"}
    if rt.server.alive:
        raise HTTPException(409, "El servidor está arrancando; inténtalo en unos segundos")
    if rt.local["running"]:
        raise HTTPException(409, "Ya hay una regeneración en curso")
    rt.local.update(running=True, result=None)
    threading.Thread(target=_run_local_generation, args=(rt, body.force_blocks), daemon=True).start()
    return {"where": "panel"}


@app.post("/api/backends/{backend_id}/open")
def open_folder(backend_id: str):
    runtime(backend_id)
    folder = settings_of(backend_id).client_dir
    if not folder.is_dir():
        raise HTTPException(404, f"No existe la carpeta {folder}")
    if sys.platform == "win32":
        os.startfile(folder)  # noqa: S606 - carpeta del cliente configurada en el panel
    return {"ok": True}


# ── Interfaz ────────────────────────────────────────────────────────────────

@app.get("/api/ping")
def ping():
    return {"panel": "Konnor Launcher Backend"}


@app.get("/")
def index():
    return FileResponse(STATIC_DIR / "index.html", headers={"Cache-Control": "no-store"})


app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
