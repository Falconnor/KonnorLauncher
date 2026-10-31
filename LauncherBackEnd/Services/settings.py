"""Configuración del backend.

Todos los valores se pueden cambiar con variables de entorno, sin tocar el código. Las rutas
relativas se resuelven respecto a la carpeta del backend.

| Variable                     | Por defecto                    | Uso                                          |
|------------------------------|--------------------------------|----------------------------------------------|
| ``KONNOR_CLIENT_DIR``        | ``tests/Client``               | Archivos que se distribuyen al launcher       |
| ``KONNOR_MANIFEST``          | ``client_manifest.json``       | Manifest generado                             |
| ``KONNOR_CLIENT_VERSION_FILE`` | ``Services/client_version.txt`` | Versión que anuncia el manifest           |
| ``KONNOR_CACHE_DIR``         | ``.cache``                     | Caché de hashes                               |
| ``KONNOR_AUTO_UPDATE``       | ``1``                          | Regenerar el manifest al arrancar el servidor |
| ``KONNOR_WATCH``             | ``1``                          | Vigilar la carpeta y regenerar al cambiar     |
| ``KONNOR_WATCH_QUIET_MS``    | ``3000``                       | Silencio necesario antes de regenerar         |
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent


def _path(env: str, default: str) -> Path:
    """Ruta de una variable de entorno; si es relativa, relativa a la carpeta del backend."""
    value = Path(os.environ.get(env, default))
    return (value if value.is_absolute() else BASE_DIR / value).resolve()


def _flag(env: str, default: bool) -> bool:
    """Interruptor: "0", "false", "no" u "off" lo desactivan."""
    value = os.environ.get(env)
    if value is None:
        return default
    return value.strip().lower() not in ("0", "false", "no", "off", "")


@dataclass(frozen=True)
class Settings:
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


def load_settings() -> Settings:
    """Lee la configuración actual del entorno."""
    return Settings(
        client_dir=_path("KONNOR_CLIENT_DIR", "tests/Client"),
        manifest_file=_path("KONNOR_MANIFEST", "client_manifest.json"),
        version_file=_path("KONNOR_CLIENT_VERSION_FILE", "Services/client_version.txt"),
        cache_dir=_path("KONNOR_CACHE_DIR", ".cache"),
        auto_update=_flag("KONNOR_AUTO_UPDATE", True),
        watch=_flag("KONNOR_WATCH", True),
        watch_quiet_ms=int(os.environ.get("KONNOR_WATCH_QUIET_MS", "3000")),
    )
