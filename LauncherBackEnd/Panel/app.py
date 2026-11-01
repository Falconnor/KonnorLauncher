"""API del panel de control (solo en 127.0.0.1).

Servidores (``servidores.json``):

- ``GET    /api/servers``                    lista con el estado de cada uno y el seleccionado
- ``POST   /api/servers``                    crea uno nuevo ``{name}``
- ``PUT    /api/servers/selected``           cambia el seleccionado ``{id}``
- ``DELETE /api/servers/{id}``               lo quita de la lista; ``?eliminar_archivos=true``
                                              borra también sus archivos (ver ``profile_files``)

Un servidor (``/api/servers/{id}/...``):

- ``GET  state``                     estado del servidor, del manifest y de la actividad
- ``GET  logs?after=N``              líneas nuevas del registro · ``POST logs/clear``
- ``POST server/{start|stop|restart}``
- ``GET  config`` / ``PUT config``   nombre y configuración
- ``PUT  client-version``            versión que anuncia el manifest
- ``POST manifest/regenerate``       regenera (en su servidor si está en marcha; si no, aquí)
- ``POST open``                      abre la carpeta del cliente en el Explorador
- ``POST release``                   libera las instancias (irreversible) y regenera el manifest
- ``GET  files``                     archivos en disco y si se borrarían al eliminarlo
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
from Services.profile_files import create_client_dir, delete_files, describe_files
from Services.settings import (release_profile, BASE_DIR, MAX_BLOCKS, PROFILES_DIR, Settings, create_profile,
                               delete_profile, env_overrides, get_profile, list_profiles,
                               load_settings, profile_defaults, raw_config, resolve_path,
                               select_profile, update_profile)


STATIC_DIR = Path(__file__).resolve().parent / "static"
PANEL_PORT = int(os.environ.get("KONNOR_PANEL_PORT", "59380"))
# Versión de la API. La interfaz se sirve desde el disco, así que un panel abierto antes de
# actualizar serviría la interfaz nueva con la API vieja: con esto ambas lo detectan.
API_VERSION = 3


def _source_stamp() -> int:
    """Fecha de modificación más reciente del código Python del panel y de los servidores."""
    files = [*Path(__file__).resolve().parent.glob("*.py"), *(BASE_DIR / "Services").glob("*.py")]
    return max((f.stat().st_mtime_ns for f in files if f.is_file()), default=0)


# Si el código cambia con el panel abierto, este sigue ejecutando el anterior hasta reiniciarse.
_STARTED_STAMP = _source_stamp()


# ── Estado en memoria de cada servidor ───────────────────────────────────────

@dataclass
class Runtime:
    """Lo que el panel sabe de un servidor mientras está abierto: su proceso y su registro."""
    profile_id: str
    logs: LogBuffer = field(default_factory=LogBuffer)
    server: ServerProcess = field(init=False)
    local: dict = field(default_factory=lambda: {"running": False, "result": None, "finished": None})
    manifest_cache: dict = field(default_factory=lambda: {"key": None, "data": None})

    def __post_init__(self):
        self.server = ServerProcess(self.profile_id, self.logs)


_runtimes: dict[str, Runtime] = {}
_runtimes_lock = threading.Lock()


def runtime(profile_id: str) -> Runtime:
    """Estado de un servidor existente (404 si no está en servidores.json)."""
    try:
        get_profile(profile_id)
    except KeyError:
        raise HTTPException(404, f"No existe el servidor '{profile_id}'") from None
    with _runtimes_lock:
        if profile_id not in _runtimes:
            _runtimes[profile_id] = Runtime(profile_id)
            _runtimes[profile_id].logs.add("Panel de control listo", source="panel", level="info")
        return _runtimes[profile_id]


def settings_of(profile_id: str) -> Settings:
    return load_settings(profile_id, use_env=False)


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Servidores creados en profiles/ cuya carpeta del cliente falta (por ejemplo, creados con una
    # versión anterior del panel): se crea con su estructura.
    _, profiles = list_profiles()
    for profile in profiles:
        client_dir = resolve_path(profile["client_dir"])
        if not client_dir.exists() and (BASE_DIR / PROFILES_DIR / profile["id"]) in client_dir.parents:
            create_client_dir(profile["id"])
    yield
    # Al cerrar el panel se detienen también los servidores que arrancó.
    await asyncio.gather(*(asyncio.to_thread(rt.server.stop) for rt in list(_runtimes.values())))


app = FastAPI(title="Servidor de Konnor Launcher - Panel", lifespan=lifespan, docs_url=None, redoc_url=None)


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


def _port_open(host: str, port: int) -> bool:
    """Comprobación rápida de que algo escucha. En Windows, conectar a un puerto local cerrado
    tarda unos 2 s (el sistema reintenta); sin esto, cada sondeo a un servidor detenido frenaría
    la interfaz."""
    try:
        with socket.create_connection((host, port), timeout=0.25):
            return True
    except OSError:
        return False


def _server_request(settings: Settings, method: str, path: str, timeout: float = 1.5) -> dict | None:
    """Petición a un servidor; None si no responde."""
    host = _probe_host(settings.host)
    if not _port_open(host, settings.port):
        return None
    url = f"http://{host}:{settings.port}{path}"
    request = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8") or "{}")
    except (urllib.error.URLError, OSError, ValueError):
        return None


def _server_status(rt: Runtime, probe: dict | None) -> str:
    """Estado combinando el proceso hijo y la respuesta del servidor en su puerto."""
    online = bool(probe and probe.get("server") == "Servidor de Konnor Launcher")
    if rt.server.alive:
        if rt.server.stopping:
            return "stopping"
        return "running" if online else "starting"
    if online:
        return "external"  # responde un servidor que no arrancó este panel (p. ej. el .bat)
    return "crashed" if rt.server.crashed else "stopped"


def _lan_addresses() -> list[str]:
    """Direcciones IPv4 de este equipo en la red local (sin la de loopback)."""
    try:
        infos = socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
    except OSError:
        return []
    return sorted({info[4][0] for info in infos if not info[4][0].startswith("127.")})


# IP pública de la red: la que usan los jugadores de fuera. Se pregunta a un servicio externo
# en segundo plano y se guarda unos minutos para que el sondeo del panel no espere por ella.
PUBLIC_IP_SERVICES = ("https://api.ipify.org", "https://checkip.amazonaws.com")
PUBLIC_IP_TTL = 600.0
PUBLIC_IP_RETRY = 60.0
_public_ip = {"value": None, "checked": 0.0, "running": False}
_public_ip_lock = threading.Lock()


def _fetch_public_ip() -> None:
    value = None
    for url in PUBLIC_IP_SERVICES:
        try:
            with urllib.request.urlopen(url, timeout=5) as response:
                text = response.read(64).decode("ascii", "replace").strip()
            socket.inet_aton(text)  # solo se acepta una IPv4 válida
            value = text
            break
        except (urllib.error.URLError, OSError, ValueError):
            continue
    with _public_ip_lock:
        if value:
            _public_ip["value"] = value
        _public_ip.update(checked=time.time(), running=False)


def _public_address() -> str | None:
    """Última IP pública conocida; si está caducada, la renueva en segundo plano."""
    with _public_ip_lock:
        age = time.time() - _public_ip["checked"]
        stale = age > (PUBLIC_IP_TTL if _public_ip["value"] else PUBLIC_IP_RETRY)
        if stale and not _public_ip["running"]:
            _public_ip["running"] = True
            threading.Thread(target=_fetch_public_ip, daemon=True).start()
        return _public_ip["value"]


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
    """Regeneración desde el panel, para cuando el servidor está detenido."""
    settings = settings_of(rt.profile_id)
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


# ── Lista de servidores ───────────────────────────────────────────────────────

@app.get("/api/servers")
async def servers():
    selected, profiles = list_profiles()

    async def summary(profile: dict) -> dict:
        rt = runtime(profile["id"])
        settings = settings_of(profile["id"])
        probe = await asyncio.to_thread(_server_request, settings, "GET", "/", 1.0)
        return {
            "id": profile["id"],
            "name": profile["name"],
            "host": settings.host,
            "port": settings.port,
            "status": _server_status(rt, probe),
        }

    return {"selected": selected, "servers": await asyncio.gather(*(summary(p) for p in profiles))}


class NameBody(BaseModel):
    name: str


@app.post("/api/servers")
def new_server(body: NameBody):
    try:
        profile = create_profile(body.name)
    except ValueError as error:
        raise HTTPException(422, str(error)) from None
    logs = runtime(profile["id"]).logs
    logs.add(f"Servidor «{profile['name']}» creado", source="panel", level="info")
    # La carpeta del cliente se crea ya, con su estructura, lista para copiar el modpack.
    try:
        client_dir = create_client_dir(profile["id"])
        logs.add(f"Carpeta del cliente creada: {client_dir}", source="panel", level="info")
    except OSError as error:
        logs.add(f"No se pudo crear la carpeta del cliente: {error}", source="panel", level="error")
    return profile


class IdBody(BaseModel):
    id: str


@app.put("/api/servers/selected")
def set_selected(body: IdBody):
    try:
        select_profile(body.id)
    except KeyError:
        raise HTTPException(404, f"No existe el servidor '{body.id}'") from None
    return {"selected": body.id}


@app.get("/api/servers/{profile_id}/files")
async def server_files(profile_id: str):
    runtime(profile_id)
    return {"items": await asyncio.to_thread(describe_files, profile_id)}


@app.delete("/api/servers/{profile_id}")
async def remove_server(profile_id: str, eliminar_archivos: bool = False):
    rt = runtime(profile_id)
    if rt.server.alive:
        raise HTTPException(409, "Detén este servidor antes de eliminarlo")
    if rt.local["running"]:
        raise HTTPException(409, "Espera a que termine la regeneración del manifest")
    # Qué se borra se decide ahora, en el servidor, con los demás servidores todavía en la lista.
    items = await asyncio.to_thread(describe_files, profile_id) if eliminar_archivos else []
    try:
        delete_profile(profile_id)
    except ValueError as error:
        raise HTTPException(409, str(error)) from None
    with _runtimes_lock:
        _runtimes.pop(profile_id, None)
    deleted, errors = await asyncio.to_thread(delete_files, items)
    return {"ok": True, "deleted": deleted, "errors": errors}


# ── Un servidor ──────────────────────────────────────────────────────────────

@app.get("/api/servers/{profile_id}/state")
async def state(profile_id: str):
    rt = runtime(profile_id)
    settings = settings_of(profile_id)
    probe = await asyncio.to_thread(_server_request, settings, "GET", "/")
    status = _server_status(rt, probe)
    online = status in ("running", "external")

    current = raw_config(profile_id, use_env=False)
    server = rt.server
    restart_required = server.alive and server.config_at_start is not None and server.config_at_start != current
    port = settings.port
    all_interfaces = settings.host in ("0.0.0.0", "::")
    lan = _lan_addresses() if all_interfaces else [settings.host]
    # Solo tiene sentido si el servidor escucha en todas las interfaces (accesible desde Internet).
    public_ip = _public_address() if all_interfaces else None

    return {
        "profile": {"id": settings.profile_id, "name": settings.profile_name},
        "server": {
            "status": status,
            "pid": server.pid,
            "started_at": server.started_at if server.alive else None,
            "exit_code": server.exit_code,
            "host": settings.host,
            "port": port,
            "local_url": f"http://{_probe_host(settings.host)}:{port}",
            "lan_urls": [f"http://{ip}:{port}" for ip in lan],
            "public_url": f"http://{public_ip}:{port}" if public_ip else None,
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


@app.get("/api/servers/{profile_id}/logs")
def get_logs(profile_id: str, after: int = 0):
    lines, last = runtime(profile_id).logs.since(after)
    return {"lines": lines, "last": last}


@app.post("/api/servers/{profile_id}/logs/clear")
def clear_logs(profile_id: str):
    runtime(profile_id).logs.clear()
    return {"ok": True}


@app.post("/api/servers/{profile_id}/server/{action}")
async def server_action(profile_id: str, action: str):
    rt = runtime(profile_id)
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

@app.get("/api/servers/{profile_id}/config")
def get_config(profile_id: str):
    runtime(profile_id)
    profile = get_profile(profile_id)
    defaults = profile_defaults(profile_id, profile["port"] if profile_id != "principal" else None)
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


@app.put("/api/servers/{profile_id}/config")
def put_config(profile_id: str, body: ConfigBody):
    rt = runtime(profile_id)
    values = {k: v for k, v in body.values.items() if k != "id"}  # el id no cambia nunca
    try:
        saved = update_profile(profile_id, values)
    except ValueError as error:
        raise HTTPException(422, str(error)) from None
    rt.logs.add("Configuración guardada", source="panel", level="info")
    restart = rt.server.alive and rt.server.config_at_start != raw_config(profile_id, use_env=False)
    return {"values": saved, "restart_required": restart}


class VersionBody(BaseModel):
    version: str


@app.put("/api/servers/{profile_id}/client-version")
async def put_client_version(profile_id: str, body: VersionBody):
    rt = runtime(profile_id)
    version = body.version.strip()
    if not version or len(version) > 64 or any(c.isspace() for c in version):
        raise HTTPException(422, "La versión no puede estar vacía ni contener espacios (máx. 64 caracteres)")
    settings = settings_of(profile_id)
    settings.version_file.parent.mkdir(parents=True, exist_ok=True)
    settings.version_file.write_text(version + "\n", encoding="utf-8")
    rt.logs.add(f"Versión del cliente cambiada a {version}", source="panel", level="info")
    # El archivo de versión no está en la carpeta vigilada: se pide la regeneración expresamente.
    queued = await asyncio.to_thread(_server_request, settings, "POST", "/admin/regenerate")
    return {"version": version, "regenerating": queued is not None}


class RegenerateBody(BaseModel):
    force_blocks: bool = False


@app.post("/api/servers/{profile_id}/manifest/regenerate")
async def regenerate(profile_id: str, body: RegenerateBody):
    rt = runtime(profile_id)
    settings = settings_of(profile_id)
    path = "/admin/regenerate" + ("?forzar_bloques=true" if body.force_blocks else "")
    if await asyncio.to_thread(_server_request, settings, "POST", path) is not None:
        return {"where": "server"}
    if rt.server.alive:
        raise HTTPException(409, "El servidor está arrancando; inténtalo en unos segundos")
    if rt.local["running"]:
        raise HTTPException(409, "Ya hay una regeneración en curso")
    rt.local.update(running=True, result=None)
    threading.Thread(target=_run_local_generation, args=(rt, body.force_blocks), daemon=True).start()
    return {"where": "panel"}


@app.post("/api/servers/{profile_id}/release")
async def release(profile_id: str):
    """Libera las instancias de este servidor (no se puede deshacer) y publica el cambio."""
    rt = runtime(profile_id)
    await asyncio.to_thread(release_profile, profile_id)
    rt.logs.add("Instancias liberadas: los launchers podrán desactivar la sincronización", source="panel", level="warn")
    try:
        where = (await regenerate(profile_id, RegenerateBody()))["where"]
    except HTTPException:
        where = "pendiente"  # se publicará en la siguiente regeneración
    return {"released": True, "where": where}


@app.post("/api/servers/{profile_id}/open")
def open_folder(profile_id: str):
    runtime(profile_id)
    folder = settings_of(profile_id).client_dir
    if not folder.is_dir():
        raise HTTPException(404, f"No existe la carpeta {folder}")
    if sys.platform == "win32":
        os.startfile(folder)  # noqa: S606 - carpeta del cliente configurada en el panel
    return {"ok": True}


# ── Interfaz ────────────────────────────────────────────────────────────────

@app.get("/api/ping")
def ping():
    return {"panel": "Servidor de Konnor Launcher", "api": API_VERSION, "outdated": _source_stamp() != _STARTED_STAMP}


@app.get("/")
def index():
    return FileResponse(STATIC_DIR / "index.html", headers={"Cache-Control": "no-store"})


app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
