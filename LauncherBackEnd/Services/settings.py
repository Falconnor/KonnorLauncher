"""Configuración del backend.

Cada valor se toma, por orden de prioridad, de:

1. una variable de entorno (``KONNOR_*``), útil para cambiar algo puntual desde un ``.bat``;
2. ``backend_config.json`` en la carpeta del backend, que es lo que edita el panel de control;
3. el valor por defecto de la tabla.

Las rutas relativas se resuelven respecto a la carpeta del backend.

| Clave              | Variable                       | Por defecto                     | Uso                                           |
|--------------------|--------------------------------|---------------------------------|-----------------------------------------------|
| ``host``           | ``KONNOR_HOST``                | ``0.0.0.0``                     | Interfaz en la que escucha el servidor         |
| ``port``           | ``KONNOR_PORT``                | ``59381``                       | Puerto del servidor                            |
| ``client_dir``     | ``KONNOR_CLIENT_DIR``          | ``tests/Client``                | Archivos que se distribuyen al launcher        |
| ``manifest``       | ``KONNOR_MANIFEST``            | ``client_manifest.json``        | Manifest generado                              |
| ``version_file``   | ``KONNOR_CLIENT_VERSION_FILE`` | ``Services/client_version.txt`` | Versión que anuncia el manifest                |
| ``cache_dir``      | ``KONNOR_CACHE_DIR``           | ``.cache``                      | Caché de hashes                                |
| ``auto_update``    | ``KONNOR_AUTO_UPDATE``         | ``true``                        | Regenerar el manifest al arrancar el servidor  |
| ``watch``          | ``KONNOR_WATCH``               | ``true``                        | Vigilar la carpeta y regenerar al cambiar      |
| ``watch_quiet_ms`` | ``KONNOR_WATCH_QUIET_MS``      | ``3000``                        | Silencio necesario antes de regenerar          |
"""

from __future__ import annotations

import ipaddress
import json
import os
from dataclasses import dataclass
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent
CONFIG_FILE = BASE_DIR / "backend_config.json"

# Valores por defecto y variable de entorno de cada clave. El orden es el de la tabla de arriba.
DEFAULTS: dict[str, object] = {
    "host": "0.0.0.0",
    "port": 59381,
    "client_dir": "tests/Client",
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
    "manifest": "KONNOR_MANIFEST",
    "version_file": "KONNOR_CLIENT_VERSION_FILE",
    "cache_dir": "KONNOR_CACHE_DIR",
    "auto_update": "KONNOR_AUTO_UPDATE",
    "watch": "KONNOR_WATCH",
    "watch_quiet_ms": "KONNOR_WATCH_QUIET_MS",
}
PATH_KEYS = ("client_dir", "manifest", "version_file", "cache_dir")
FLAG_KEYS = ("auto_update", "watch")


@dataclass(frozen=True)
class Settings:
    host: str
    port: int
    client_dir: Path
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


# ── Archivo de configuración ────────────────────────────────────────────────

def read_config_file() -> dict[str, object]:
    """Valores guardados en ``backend_config.json`` (solo las claves conocidas)."""
    try:
        data = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError):
        return {}
    return {key: data[key] for key in DEFAULTS if isinstance(data, dict) and key in data}


def validate_config(values: dict[str, object]) -> dict[str, object]:
    """Comprueba y normaliza una configuración completa. Lanza ``ValueError`` con un mensaje
    legible para el panel si algún valor no es válido."""
    clean: dict[str, object] = {}

    host = str(values.get("host", "")).strip()
    try:
        ipaddress.ip_address(host)
    except ValueError:
        if host != "localhost":
            raise ValueError(f"host: '{host}' no es una dirección IP válida") from None
    clean["host"] = host

    try:
        port = int(values.get("port"))  # type: ignore[arg-type]
    except (TypeError, ValueError):
        raise ValueError("port: debe ser un número") from None
    if not 1 <= port <= 65535:
        raise ValueError("port: debe estar entre 1 y 65535")
    clean["port"] = port

    for key in PATH_KEYS:
        text = str(values.get(key, "")).strip()
        if not text:
            raise ValueError(f"{key}: la ruta no puede estar vacía")
        clean[key] = text

    for key in FLAG_KEYS:
        clean[key] = bool(values.get(key))

    try:
        quiet = int(values.get("watch_quiet_ms"))  # type: ignore[arg-type]
    except (TypeError, ValueError):
        raise ValueError("watch_quiet_ms: debe ser un número") from None
    if not 500 <= quiet <= 600_000:
        raise ValueError("watch_quiet_ms: debe estar entre 500 y 600000")
    clean["watch_quiet_ms"] = quiet
    return clean


def save_config_file(values: dict[str, object]) -> dict[str, object]:
    """Valida y guarda la configuración de forma atómica. Devuelve los valores guardados."""
    clean = validate_config({**DEFAULTS, **values})
    tmp = CONFIG_FILE.with_name(CONFIG_FILE.name + ".tmp")
    tmp.write_text(json.dumps(clean, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, CONFIG_FILE)
    return clean


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


def raw_config(use_env: bool = True) -> dict[str, object]:
    """Valores sin resolver: por defecto ← archivo ← entorno."""
    values = {**DEFAULTS, **read_config_file()}
    if use_env:
        for key, env in ENV_VARS.items():
            if env in os.environ:
                values[key] = os.environ[env]
    return values


def load_settings(use_env: bool = True) -> Settings:
    """Configuración actual, con las rutas resueltas."""
    values = raw_config(use_env)
    return Settings(
        host=str(values["host"]),
        port=int(values["port"]),  # type: ignore[arg-type]
        client_dir=resolve_path(values["client_dir"]),
        manifest_file=resolve_path(values["manifest"]),
        version_file=resolve_path(values["version_file"]),
        cache_dir=resolve_path(values["cache_dir"]),
        auto_update=_flag(values["auto_update"]),
        watch=_flag(values["watch"]),
        watch_quiet_ms=int(values["watch_quiet_ms"]),  # type: ignore[arg-type]
    )
