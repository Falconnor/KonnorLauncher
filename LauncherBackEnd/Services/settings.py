"""Configuración: uno o varios servidores con nombre.

Cada servidor es independiente (su puerto, su carpeta del cliente, su manifest...).
Se guardan en ``servidores.json``, que es lo que edita el panel de control::

    {
      "selected": "principal",
      "servers": [ {"id": "principal", "name": "Principal", "port": 59381, ...}, ... ]
    }

Qué servidor usa un proceso: ``KONNOR_PROFILE`` (lo fija el panel o ``iniciar_servidor.bat <id>``);
si no está, el seleccionado en el panel.

Cada valor se toma, por orden de prioridad, de:

1. una variable de entorno (``KONNOR_*``), útil para cambiar algo puntual desde un ``.bat``;
2. el servidor en ``servidores.json``;
3. el valor por defecto de la tabla.

Las rutas relativas se resuelven respecto a la carpeta LauncherBackEnd.

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
| ``delete_unauthorized`` | ``KONNOR_DELETE_UNAUTHORIZED`` | ``true``                   | Los launchers borran lo que no esté en el manifest |
| ``always_online``  | ``KONNOR_ALWAYS_ONLINE``       | ``true``                        | Sin respuesta del servidor, el launcher no abre el juego |
| ``lock_server_url`` | ``KONNOR_LOCK_SERVER_URL``    | ``true``                        | Los jugadores no pueden cambiar la dirección del servidor |
| ``protect_packs``  | ``KONNOR_PROTECT_PACKS``       | ``false``                       | Los packs se instalan siempre y se protegen como el resto |
| ``source_mods``    | ``KONNOR_SOURCE_MODS``         | (vacía)                         | Carpeta de la que se copia ``mods`` (ver ``client_sources``) |

Sin carpeta de origen no se copia nada: se publica lo que haya en la carpeta del cliente.

Además, ``released`` (no configurable): el servidor liberó sus instancias y los jugadores pueden
desactivar la sincronización. Se activa con :func:`release_profile` y no se puede deshacer.

Los valores por defecto de la tabla son los del primer servidor. Los que se crean después
guardan sus archivos en ``profiles/<id>/`` para no pisarse con los demás.
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
STORE_FILE = BASE_DIR / "servidores.json"
PROFILES_DIR = "profiles"
LEGACY_PROFILES_DIR = "perfiles"  # nombre anterior de la carpeta; se migra solo
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
    "delete_unauthorized": True,
    "always_online": True,
    "lock_server_url": True,
    "protect_packs": False,
    "source_mods": "",
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
    "delete_unauthorized": "KONNOR_DELETE_UNAUTHORIZED",
    "always_online": "KONNOR_ALWAYS_ONLINE",
    "lock_server_url": "KONNOR_LOCK_SERVER_URL",
    "protect_packs": "KONNOR_PROTECT_PACKS",
    "source_mods": "KONNOR_SOURCE_MODS",
}
PROFILE_ENV = "KONNOR_PROFILE"
PATH_KEYS = ("client_dir", "manifest", "version_file", "cache_dir")
FLAG_KEYS = ("auto_update", "watch", "delete_unauthorized", "always_online", "lock_server_url", "protect_packs")
# Carpetas del cliente que se pueden copiar de otra (clave → carpeta dentro del cliente). Solo
# mods: las versions y libraries de un servidor de Minecraft no sirven para el cliente y los
# servidores no tienen assets; esas se ponen a mano en la carpeta del cliente. No está en
# PATH_KEYS a propósito: es de otro programa y el panel nunca debe borrarla.
SOURCE_KEYS: dict[str, str] = {
    "source_mods": "mods",
}

_lock = threading.RLock()


@dataclass(frozen=True)
class Settings:
    profile_id: str
    profile_name: str
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
    # Se publica en el manifest: los launchers sincronizados borran de mods, versions, libraries y
    # assets lo que no esté en él. La decisión es del servidor, no de cada jugador.
    delete_unauthorized: bool
    # Se publica en el manifest: con true, sin respuesta del servidor el launcher no abre el juego;
    # con false, juega con los archivos que ya tiene.
    always_online: bool
    # Se publica en el manifest: los launchers no dejan cambiar la dirección del servidor en las
    # instancias ya sincronizadas. Reversible (a diferencia de la liberación).
    lock_server_url: bool
    # Se publica en el manifest: con true, los paquetes de recursos y de shaders (resourcepacks y
    # shaderpacks) se instalan siempre y se protegen como el resto del cliente; con false, cada
    # jugador decide si instalarlos y nunca se reemplazan ni se borran los suyos.
    protect_packs: bool
    # Instancias liberadas: los jugadores pueden desactivar la sincronización. Irreversible.
    released: bool
    # Carpetas del cliente que se copian de otra carpeta antes de generar: (carpeta, origen).
    sources: tuple[tuple[str, Path], ...] = ()

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


# ── Valores por defecto de cada servidor ─────────────────────────────────────

def profile_defaults(profile_id: str, port: int | None = None) -> dict[str, object]:
    """Valores por defecto de un servidor. El principal usa las rutas de siempre; el resto, su
    propia carpeta en ``profiles/<id>/``."""
    if profile_id == DEFAULT_ID:
        return dict(DEFAULTS)
    base = f"{PROFILES_DIR}/{profile_id}"
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
    return slug[:32].strip("-") or "servidor"


# ── Almacén de servidores ─────────────────────────────────────────────────────

def _read_json(path: Path) -> dict | None:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (FileNotFoundError, ValueError):
        return None


def read_store() -> dict:
    """Servidores guardados. Sin ``servidores.json``, uno solo ("Principal") con los valores por
    defecto."""
    with _lock:
        data = _read_json(STORE_FILE)
        entries = [b for b in (data or {}).get("servers", []) if isinstance(b, dict) and b.get("id")]
        if not entries:
            entries = [{"id": DEFAULT_ID, "name": DEFAULT_NAME, **DEFAULTS}]
        ids = [b["id"] for b in entries]
        selected = (data or {}).get("selected")
        store = {"selected": selected if selected in ids else ids[0], "servers": entries}
        if data is not None and _migrate_legacy_profiles(store):
            write_store(store)
        return store


def _migrate_legacy_profiles(store: dict) -> bool:
    """Pasa los servidores de ``perfiles/<id>/`` a ``profiles/<id>/``: cambia sus rutas y mueve
    la carpeta si existe. Devuelve True si cambió algo."""
    changed = False
    for entry in store["servers"]:
        old_prefix = f"{LEGACY_PROFILES_DIR}/{entry['id']}/"
        moved = False
        for key in PATH_KEYS:
            value = str(entry.get(key, "")).replace("\\", "/")
            if value.startswith(old_prefix):
                entry[key] = f"{PROFILES_DIR}/{value[len(LEGACY_PROFILES_DIR) + 1:]}"
                moved = changed = True
        old_dir = BASE_DIR / LEGACY_PROFILES_DIR / entry["id"]
        new_dir = BASE_DIR / PROFILES_DIR / entry["id"]
        if moved and old_dir.is_dir() and not new_dir.exists():
            new_dir.parent.mkdir(parents=True, exist_ok=True)
            old_dir.rename(new_dir)
    legacy_root = BASE_DIR / LEGACY_PROFILES_DIR
    if changed and legacy_root.is_dir() and not any(legacy_root.iterdir()):
        legacy_root.rmdir()
    return changed


def write_store(store: dict) -> None:
    """Guarda ``servidores.json`` de forma atómica."""
    with _lock:
        tmp = STORE_FILE.with_name(STORE_FILE.name + ".tmp")
        tmp.write_text(json.dumps(store, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
        os.replace(tmp, STORE_FILE)


def get_profile(profile_id: str) -> dict:
    """Servidor completo (con los valores por defecto aplicados). ``KeyError`` si no existe."""
    for entry in read_store()["servers"]:
        if entry["id"] == profile_id:
            return {**profile_defaults(profile_id, entry.get("port")), **entry}
    raise KeyError(profile_id)


def list_profiles() -> tuple[str, list[dict]]:
    store = read_store()
    return store["selected"], [get_profile(b["id"]) for b in store["servers"]]


def validate_config(values: dict[str, object]) -> dict[str, object]:
    """Comprueba y normaliza los valores de un servidor. Lanza ``ValueError`` con un mensaje
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
    for key in SOURCE_KEYS:
        clean[key] = str(values.get(key) or "").strip()
    _check_sources(clean)
    return clean


def _check_sources(values: dict[str, object]) -> None:
    """Un origen no puede estar dentro de la carpeta del cliente ni contenerla: cada copia
    provocaría otra."""
    client_dir = resolve_path(values["client_dir"])
    for key, folder in SOURCE_KEYS.items():
        if not values[key]:
            continue
        source = resolve_path(values[key])
        if source == client_dir or client_dir in source.parents:
            raise ValueError(f"Origen de {folder}: está dentro de la carpeta del cliente")
        if source in client_dir.parents:
            raise ValueError(f"Origen de {folder}: contiene la carpeta del cliente")


def _int_in_range(value: object, label: str, low: int, high: int) -> int:
    try:
        number = int(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        raise ValueError(f"{label}: debe ser un número") from None
    if not low <= number <= high:
        raise ValueError(f"{label}: debe estar entre {low} y {high}")
    return number


def _check_conflicts(profile_id: str, values: dict, others: list[dict]) -> None:
    """Dos servidores no pueden compartir puerto, carpeta del cliente ni manifest: se pisarían."""
    for other in others:
        if other["id"] == profile_id:
            continue
        other = {**profile_defaults(other["id"], other.get("port")), **other}
        if int(other["port"]) == values["port"]:
            raise ValueError(f"Puerto: {values['port']} ya lo usa el servidor «{other['name']}»")
        for key, label in (("client_dir", "Carpeta del cliente"), ("manifest", "Manifest")):
            if resolve_path(other[key]) == resolve_path(values[key]):
                raise ValueError(f"{label}: ya la usa el servidor «{other['name']}»")


def _clean_name(name: object) -> str:
    text = " ".join(str(name or "").split())
    if not 1 <= len(text) <= 40:
        raise ValueError("Nombre: entre 1 y 40 caracteres")
    return text


def update_profile(profile_id: str, values: dict) -> dict:
    """Valida y guarda nombre y configuración de un servidor. Devuelve el servidor guardado."""
    with _lock:
        store = read_store()
        current = next((b for b in store["servers"] if b["id"] == profile_id), None)
        if current is None:
            raise KeyError(profile_id)
        merged = {**profile_defaults(profile_id, current.get("port")), **current, **values}
        clean = validate_config(merged)
        name = _clean_name(merged.get("name"))
        _check_conflicts(profile_id, clean, store["servers"])
        saved = {"id": profile_id, "name": name, **clean}
        if current.get("released") is True:
            saved["released"] = True  # irreversible: la configuración no puede deshacerla
        store["servers"] = [saved if b["id"] == profile_id else b for b in store["servers"]]
        write_store(store)
        return saved


def release_profile(profile_id: str) -> dict:
    """Libera las instancias de un servidor: los launchers podrán desactivar la sincronización.
    No hay función inversa: una vez liberado, queda liberado."""
    with _lock:
        store = read_store()
        current = next((b for b in store["servers"] if b["id"] == profile_id), None)
        if current is None:
            raise KeyError(profile_id)
        current["released"] = True
        write_store(store)
        return current


def is_released(profile_id: str) -> bool:
    """Se lee del archivo cada vez: un servidor en marcha publica la liberación sin reiniciarse."""
    try:
        return get_profile(profile_id).get("released") is True
    except KeyError:
        return False


def create_profile(name: str) -> dict:
    """Crea un servidor con su propia carpeta y el primer puerto libre entre los configurados."""
    with _lock:
        store = read_store()
        name = _clean_name(name)
        ids = {b["id"] for b in store["servers"]}
        base = slugify(name)
        profile_id, n = base, 2
        while profile_id in ids:
            profile_id, n = f"{base}-{n}", n + 1
        used_ports = {int(get_profile(b["id"])["port"]) for b in store["servers"]}
        port = int(DEFAULTS["port"])
        while port in used_ports:
            port += 1
        entry = {"id": profile_id, "name": name, **profile_defaults(profile_id, port)}
        store["servers"].append(entry)
        store["selected"] = profile_id
        write_store(store)
        return entry


def delete_profile(profile_id: str) -> None:
    """Quita un servidor de la lista. No borra ningún archivo del disco."""
    with _lock:
        store = read_store()
        if len(store["servers"]) <= 1:
            raise ValueError("Tiene que quedar al menos un servidor")
        store["servers"] = [b for b in store["servers"] if b["id"] != profile_id]
        if store["selected"] == profile_id:
            store["selected"] = store["servers"][0]["id"]
        write_store(store)


def select_profile(profile_id: str) -> None:
    with _lock:
        store = read_store()
        if profile_id not in {b["id"] for b in store["servers"]}:
            raise KeyError(profile_id)
        store["selected"] = profile_id
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
    """Ruta absoluta; las relativas, respecto a la carpeta LauncherBackEnd."""
    path = Path(str(value))
    return (path if path.is_absolute() else BASE_DIR / path).resolve()


def current_profile_id(use_env: bool = True) -> str:
    """Servidor de este proceso: ``KONNOR_PROFILE`` o, si no está, el seleccionado en el panel."""
    if use_env and os.environ.get(PROFILE_ENV):
        return os.environ[PROFILE_ENV]
    return read_store()["selected"]


def raw_config(profile_id: str | None = None, use_env: bool = True) -> dict[str, object]:
    """Valores sin resolver de un servidor: por defecto ← servidores.json ← entorno."""
    profile_id = profile_id or current_profile_id(use_env)
    try:
        values = get_profile(profile_id)
    except KeyError:
        raise SystemExit(f"No existe el servidor '{profile_id}' en {STORE_FILE.name}") from None
    if use_env:
        for key, env in ENV_VARS.items():
            if env in os.environ:
                values[key] = os.environ[env]
    return values


def load_settings(profile_id: str | None = None, use_env: bool = True) -> Settings:
    """Configuración de un servidor, con las rutas resueltas."""
    values = raw_config(profile_id, use_env)
    return Settings(
        profile_id=str(values["id"]),
        profile_name=str(values["name"]),
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
        delete_unauthorized=_flag(values["delete_unauthorized"]),
        always_online=_flag(values["always_online"]),
        lock_server_url=_flag(values["lock_server_url"]),
        protect_packs=_flag(values["protect_packs"]),
        released=values.get("released") is True,
        sources=tuple(
            (folder, resolve_path(str(values[key]).strip()))
            for key, folder in SOURCE_KEYS.items()
            if str(values.get(key) or "").strip()
        ),
    )
