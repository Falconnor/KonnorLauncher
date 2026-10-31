"""Configuración del backend: uno o varios backends con nombre.

Cada backend es un servidor independiente (su puerto, su carpeta del cliente, su manifest...).
Se guardan en ``backends.json``, que es lo que edita el panel de control::

    {
      "selected": "principal",
      "backends": [ {"id": "principal", "name": "Principal", "port": 59381, ...}, ... ]
    }

Qué backend usa un proceso: ``KONNOR_PROFILE`` (lo fija el panel o ``iniciar_servidor.bat <id>``);
si no está, el seleccionado en el panel.

Cada valor se toma, por orden de prioridad, de:

1. una variable de entorno (``KONNOR_*``), útil para cambiar algo puntual desde un ``.bat``;
2. el backend en ``backends.json``;
3. el valor por defecto de la tabla.

Las rutas relativas se resuelven respecto a la carpeta del backend.

| Clave              | Variable                       | Por defecto                     | Uso                                           |
|--------------------|--------------------------------|---------------------------------|-----------------------------------------------|
| ``host``           | ``KONNOR_HOST``                | ``0.0.0.0``                     | Interfaz en la que escucha el servidor         |
| ``port``           | ``KONNOR_PORT``                | ``59381``                       | Puerto del servidor                            |
| ``client_dir``     | ``KONNOR_CLIENT_DIR``          | ``tests/Client``                | Archivos que se distribuyen al launcher        |
| ``block_count``    | ``KONNOR_BLOCK_COUNT``         | ``16``                          | Bloques ZIP en los que se reparten los assets  |
| ``manifest``       | ``KONNOR_MANIFEST``            | ``client_manifest.json``        | Manifest generado                              |
| ``version_file``   | ``KONNOR_CLIENT_VERSION_FILE`` | ``Services/client_version.txt`` | Versión que anuncia el manifest                |
| ``cache_dir``      | ``KONNOR_CACHE_DIR``           | ``.cache``                      | Caché de hashes                                |
| ``auto_update``    | ``KONNOR_AUTO_UPDATE``         | ``true``                        | Regenerar el manifest al arrancar el servidor  |
| ``watch``          | ``KONNOR_WATCH``               | ``true``                        | Vigilar la carpeta y regenerar al cambiar      |
| ``watch_quiet_ms`` | ``KONNOR_WATCH_QUIET_MS``      | ``3000``                        | Silencio necesario antes de regenerar          |

Los valores por defecto de la tabla son los del primer backend. Los que se crean después
guardan sus archivos en ``perfiles/<id>/`` para no pisarse con los demás.
"""

from __future__ import annotations

import ipaddress
import json
import os
import re
import threading
import unicodedata
from dataclasses import dataclass
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent
STORE_FILE = BASE_DIR / "backends.json"
LEGACY_CONFIG_FILE = BASE_DIR / "backend_config.json"  # formato anterior (un solo backend)
PROFILES_DIR = "perfiles"
DEFAULT_ID = "principal"
DEFAULT_NAME = "Principal"
MAX_BLOCKS = 256

DEFAULTS: dict[str, object] = {
    "host": "0.0.0.0",
    "port": 59381,
    "client_dir": "tests/Client",
    "block_count": 16,
    "manifest": "client_manifest.json",
    "version_file": "Services/client_version.txt",
    "cache_dir": ".cache",
    "auto_update": True,
    "watch": True,
    "watch_quiet_ms": 3000,
}
ENV_VARS: dict[str, str] = {
    "host": "KONNOR_HOST",
    "port": "KONNOR_PORT",
    "client_dir": "KONNOR_CLIENT_DIR",
    "block_count": "KONNOR_BLOCK_COUNT",
    "manifest": "KONNOR_MANIFEST",
    "version_file": "KONNOR_CLIENT_VERSION_FILE",
    "cache_dir": "KONNOR_CACHE_DIR",
    "auto_update": "KONNOR_AUTO_UPDATE",
    "watch": "KONNOR_WATCH",
    "watch_quiet_ms": "KONNOR_WATCH_QUIET_MS",
}
PROFILE_ENV = "KONNOR_PROFILE"
PATH_KEYS = ("client_dir", "manifest", "version_file", "cache_dir")
FLAG_KEYS = ("auto_update", "watch")

_lock = threading.RLock()


@dataclass(frozen=True)
class Settings:
    backend_id: str
    backend_name: str
    host: str
    port: int
    client_dir: Path
    block_count: int
    manifest_file: Path
    version_file: Path
    cache_dir: Path
    auto_update: bool
    watch: bool
    watch_quiet_ms: int

    @property
    def packages_dir(self) -> Path:
        """Bloques ZIP de assets. Viven dentro de la carpeta del cliente para que se sirvan en
        ``/client/packages/``, la ruta que usa el launcher."""
        return self.client_dir / "packages"

    @property
    def assets_dir(self) -> Path:
        return self.client_dir / "assets"

    @property
    def hash_cache_file(self) -> Path:
        return self.cache_dir / "hashes.json"


# ── Valores por defecto de cada backend ─────────────────────────────────────

def profile_defaults(backend_id: str, port: int | None = None) -> dict[str, object]:
    """Valores por defecto de un backend. El principal usa las rutas de siempre; el resto, su
    propia carpeta en ``perfiles/<id>/``."""
    if backend_id == DEFAULT_ID:
        return dict(DEFAULTS)
    base = f"{PROFILES_DIR}/{backend_id}"
    return {
        **DEFAULTS,
        "port": port if port is not None else DEFAULTS["port"],
        "client_dir": f"{base}/Client",
        "manifest": f"{base}/client_manifest.json",
        "version_file": f"{base}/client_version.txt",
        "cache_dir": f"{base}/.cache",
    }


def slugify(name: str) -> str:
    """Identificador estable a partir del nombre: minúsculas, sin acentos, letras, números y guiones."""
    ascii_name = unicodedata.normalize("NFKD", name).encode("ascii", "ignore").decode()
    slug = re.sub(r"[^a-z0-9]+", "-", ascii_name.lower()).strip("-")
    return slug[:32].strip("-") or "backend"


# ── Almacén de backends ─────────────────────────────────────────────────────

def _read_json(path: Path) -> dict | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (FileNotFoundError, ValueError):
        return None


def read_store() -> dict:
    """Backends guardados. Sin ``backends.json``, uno solo ("Principal") con la configuración
    antigua de ``backend_config.json`` si existe, o con los valores por defecto."""
    with _lock:
        data = _read_json(STORE_FILE)
        backends = [b for b in (data or {}).get("backends", []) if isinstance(b, dict) and b.get("id")]
        if not backends:
            legacy = {k: v for k, v in (_read_json(LEGACY_CONFIG_FILE) or {}).items() if k in DEFAULTS}
            backends = [{"id": DEFAULT_ID, "name": DEFAULT_NAME, **DEFAULTS, **legacy}]
        ids = [b["id"] for b in backends]
        selected = (data or {}).get("selected")
        return {"selected": selected if selected in ids else ids[0], "backends": backends}


def write_store(store: dict) -> None:
    """Guarda ``backends.json`` de forma atómica."""
    with _lock:
        tmp = STORE_FILE.with_name(STORE_FILE.name + ".tmp")
        tmp.write_text(json.dumps(store, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
        os.replace(tmp, STORE_FILE)


def get_profile(backend_id: str) -> dict:
    """Backend completo (con los valores por defecto aplicados). ``KeyError`` si no existe."""
    for backend in read_store()["backends"]:
        if backend["id"] == backend_id:
            return {**profile_defaults(backend_id, backend.get("port")), **backend}
    raise KeyError(backend_id)


def list_profiles() -> tuple[str, list[dict]]:
    store = read_store()
    return store["selected"], [get_profile(b["id"]) for b in store["backends"]]


def validate_config(values: dict[str, object]) -> dict[str, object]:
    """Comprueba y normaliza los valores de un backend. Lanza ``ValueError`` con un mensaje
    legible para el panel si alguno no es válido."""
    clean: dict[str, object] = {}

    host = str(values.get("host", "")).strip()
    try:
        ipaddress.ip_address(host)
    except ValueError:
        if host != "localhost":
            raise ValueError(f"Interfaz: '{host}' no es una dirección IP válida") from None
    clean["host"] = host

    clean["port"] = _int_in_range(values.get("port"), "Puerto", 1, 65535)

    for key in PATH_KEYS:
        text = str(values.get(key, "")).strip()
        if not text:
            raise ValueError(f"{key}: la ruta no puede estar vacía")
        clean[key] = text

    clean["block_count"] = _int_in_range(values.get("block_count"), "Bloques de assets", 1, MAX_BLOCKS)
    for key in FLAG_KEYS:
        clean[key] = bool(values.get(key))
    clean["watch_quiet_ms"] = _int_in_range(values.get("watch_quiet_ms"), "Espera sin cambios", 500, 600_000)
    return clean


def _int_in_range(value: object, label: str, low: int, high: int) -> int:
    try:
        number = int(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        raise ValueError(f"{label}: debe ser un número") from None
    if not low <= number <= high:
        raise ValueError(f"{label}: debe estar entre {low} y {high}")
    return number


def _check_conflicts(backend_id: str, values: dict, others: list[dict]) -> None:
    """Dos backends no pueden compartir puerto, carpeta del cliente ni manifest: se pisarían."""
    for other in others:
        if other["id"] == backend_id:
            continue
        other = {**profile_defaults(other["id"], other.get("port")), **other}
        if int(other["port"]) == values["port"]:
            raise ValueError(f"Puerto: {values['port']} ya lo usa el backend «{other['name']}»")
        for key, label in (("client_dir", "Carpeta del cliente"), ("manifest", "Manifest")):
            if resolve_path(other[key]) == resolve_path(values[key]):
                raise ValueError(f"{label}: ya la usa el backend «{other['name']}»")


def _clean_name(name: object) -> str:
    text = " ".join(str(name or "").split())
    if not 1 <= len(text) <= 40:
        raise ValueError("Nombre: entre 1 y 40 caracteres")
    return text


def update_profile(backend_id: str, values: dict) -> dict:
    """Valida y guarda nombre y configuración de un backend. Devuelve el backend guardado."""
    with _lock:
        store = read_store()
        current = next((b for b in store["backends"] if b["id"] == backend_id), None)
        if current is None:
            raise KeyError(backend_id)
        merged = {**profile_defaults(backend_id, current.get("port")), **current, **values}
        clean = validate_config(merged)
        name = _clean_name(merged.get("name"))
        _check_conflicts(backend_id, clean, store["backends"])
        saved = {"id": backend_id, "name": name, **clean}
        store["backends"] = [saved if b["id"] == backend_id else b for b in store["backends"]]
        write_store(store)
        return saved


def create_profile(name: str) -> dict:
    """Crea un backend con su propia carpeta y el primer puerto libre entre los configurados."""
    with _lock:
        store = read_store()
        name = _clean_name(name)
        ids = {b["id"] for b in store["backends"]}
        base = slugify(name)
        backend_id, n = base, 2
        while backend_id in ids:
            backend_id, n = f"{base}-{n}", n + 1
        used_ports = {int(get_profile(b["id"])["port"]) for b in store["backends"]}
        port = int(DEFAULTS["port"])
        while port in used_ports:
            port += 1
        backend = {"id": backend_id, "name": name, **profile_defaults(backend_id, port)}
        store["backends"].append(backend)
        store["selected"] = backend_id
        write_store(store)
        return backend


def delete_profile(backend_id: str) -> None:
    """Quita un backend de la lista. No borra ningún archivo del disco."""
    with _lock:
        store = read_store()
        if len(store["backends"]) <= 1:
            raise ValueError("Tiene que quedar al menos un backend")
        store["backends"] = [b for b in store["backends"] if b["id"] != backend_id]
        if store["selected"] == backend_id:
            store["selected"] = store["backends"][0]["id"]
        write_store(store)


def select_profile(backend_id: str) -> None:
    with _lock:
        store = read_store()
        if backend_id not in {b["id"] for b in store["backends"]}:
            raise KeyError(backend_id)
        store["selected"] = backend_id
        write_store(store)


def env_overrides() -> dict[str, str]:
    """Claves que ahora mismo fija una variable de entorno (tienen prioridad sobre el archivo)."""
    return {key: env for key, env in ENV_VARS.items() if env in os.environ}


# ── Resolución ──────────────────────────────────────────────────────────────

def _flag(value: object) -> bool:
    """Interruptor: "0", "false", "no" u "off" lo desactivan."""
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() not in ("0", "false", "no", "off", "")


def resolve_path(value: object) -> Path:
    """Ruta absoluta; las relativas, respecto a la carpeta del backend."""
    path = Path(str(value))
    return (path if path.is_absolute() else BASE_DIR / path).resolve()


def current_profile_id(use_env: bool = True) -> str:
    """Backend de este proceso: ``KONNOR_PROFILE`` o, si no está, el seleccionado en el panel."""
    if use_env and os.environ.get(PROFILE_ENV):
        return os.environ[PROFILE_ENV]
    return read_store()["selected"]


def raw_config(backend_id: str | None = None, use_env: bool = True) -> dict[str, object]:
    """Valores sin resolver de un backend: por defecto ← backends.json ← entorno."""
    backend_id = backend_id or current_profile_id(use_env)
    try:
        values = get_profile(backend_id)
    except KeyError:
        raise SystemExit(f"No existe el backend '{backend_id}' en {STORE_FILE.name}") from None
    if use_env:
        for key, env in ENV_VARS.items():
            if env in os.environ:
                values[key] = os.environ[env]
    return values


def load_settings(backend_id: str | None = None, use_env: bool = True) -> Settings:
    """Configuración de un backend, con las rutas resueltas."""
    values = raw_config(backend_id, use_env)
    return Settings(
        backend_id=str(values["id"]),
        backend_name=str(values["name"]),
        host=str(values["host"]),
        port=int(values["port"]),  # type: ignore[arg-type]
        client_dir=resolve_path(values["client_dir"]),
        block_count=max(1, min(MAX_BLOCKS, int(values["block_count"]))),  # type: ignore[arg-type]
        manifest_file=resolve_path(values["manifest"]),
        version_file=resolve_path(values["version_file"]),
        cache_dir=resolve_path(values["cache_dir"]),
        auto_update=_flag(values["auto_update"]),
        watch=_flag(values["watch"]),
        watch_quiet_ms=int(values["watch_quiet_ms"]),  # type: ignore[arg-type]
    )
