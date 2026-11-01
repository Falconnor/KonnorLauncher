"""Carpeta de origen de ``mods``: de dónde se copian los mods del cliente.

La carpeta ``mods`` del cliente puede tener una carpeta de origen (normalmente, la carpeta ``mods``
del servidor de Minecraft). Antes de cada generación del manifest, se iguala a su origen:

- se copia lo que falte o haya cambiado (tamaño o fecha de modificación distintos);
- se borra lo que ya no esté en el origen, para que los jugadores no sigan recibiendo un mod
  que el servidor quitó.

Sin carpeta de origen, la carpeta del cliente se usa tal cual (se copian los archivos a mano).
``versions``, ``libraries`` y ``assets`` siempre se ponen a mano en la carpeta del cliente (ver
``SOURCE_KEYS`` en ``settings``).

El origen solo se lee: nunca se escribe ni se borra nada en él. Si no existe (un disco
desconectado, una ruta mal escrita), esa carpeta se omite y la del cliente no se toca; vaciarla
por error dejaría a todos los jugadores sin mods.
"""

from __future__ import annotations

import os
import shutil
from dataclasses import dataclass, field
from pathlib import Path

from Services.settings import Settings


# Mismos archivos que nunca publica el manifest.
SKIPPED_NAMES = {"Thumbs.db", "desktop.ini", ".DS_Store"}
SKIPPED_SUFFIXES = (".tmp", ".part")
# Diferencia de fecha que se tolera: FAT32 y exFAT guardan la hora con 2 s de precisión.
MTIME_TOLERANCE_NS = 2_000_000_000
# Archivos copiados o borrados que se nombran en el registro (el resto, solo en el recuento).
LOGGED_NAMES = 10


@dataclass
class SyncResult:
    copied: int = 0
    deleted: int = 0
    errors: list[str] = field(default_factory=list)
    missing: list[str] = field(default_factory=list)  # carpetas de origen que no existen


def _key(relative: str) -> str:
    """Clave de comparación: en Windows, ``Mod.jar`` y ``mod.jar`` son el mismo archivo."""
    return os.path.normcase(relative)


def _skipped(name: str) -> bool:
    return name in SKIPPED_NAMES or name.endswith(SKIPPED_SUFFIXES)


def _source_files(source: Path) -> dict[str, tuple[str, os.stat_result]]:
    """Archivos del origen: clave → (ruta relativa, stat). No entra en enlaces ni uniones.

    Si una subcarpeta no se puede leer lanza ``OSError``: con una lista incompleta se borrarían
    del cliente archivos que sí están en el origen."""
    def fail(error: OSError) -> None:
        raise error

    files: dict[str, tuple[str, os.stat_result]] = {}
    for root, dirs, names in os.walk(source, onerror=fail, followlinks=False):
        dirs[:] = [d for d in dirs if not (Path(root) / d).is_symlink()]
        for name in names:
            if _skipped(name):
                continue
            path = Path(root) / name
            try:
                stat = path.stat()
            except FileNotFoundError:
                continue  # se borró mientras se recorría, o es un enlace roto
            relative = path.relative_to(source).as_posix()
            files[_key(relative)] = (relative, stat)
    return files


def _up_to_date(target: Path, stat: os.stat_result) -> bool:
    try:
        current = target.stat()
    except OSError:
        return False
    return (current.st_size == stat.st_size
            and abs(current.st_mtime_ns - stat.st_mtime_ns) <= MTIME_TOLERANCE_NS)


def overlap_reason(source: Path, client_dir: Path) -> str | None:
    """Por qué un origen no se puede usar con esta carpeta del cliente, o None si se puede."""
    if source == client_dir or client_dir in source.parents:
        return "está dentro de la carpeta del cliente"
    if source in client_dir.parents:
        return "contiene la carpeta del cliente"
    return None


def sync_folder(folder: str, source: Path, client_dir: Path, result: SyncResult,
                touched: set[str] | None = None, log=print) -> None:
    """Iguala ``client_dir/folder`` a ``source``."""
    target_root = client_dir / folder
    if reason := overlap_reason(source, client_dir):
        result.errors.append(f"Origen de {folder} ({source}): {reason}")
        return
    if not source.is_dir():
        result.missing.append(folder)
        log(f"[AVISO] No existe la carpeta de origen de {folder}: {source}. "
            f"Se publica lo que ya hay en la carpeta del cliente.")
        return

    try:
        files = _source_files(source)
    except OSError as error:
        result.errors.append(f"Origen de {folder} ({source}): no se pudo leer ({error.strerror or error})")
        return
    copied: list[str] = []
    deleted: list[str] = []

    def touch(path: Path) -> None:
        # También sus carpetas: Windows avisa de que cambiaron al añadir o quitar un archivo.
        if touched is None:
            return
        for item in (path, *path.parents):
            touched.add(os.path.normcase(str(item)))
            if item == client_dir:
                break

    # 1. Copiar lo que falta o cambió (a un temporal y reemplazando: nunca queda un archivo a medias).
    for relative, stat in files.values():
        target = target_root / relative
        if _up_to_date(target, stat):
            continue
        tmp = target.with_name(target.name + ".part")
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / relative, tmp)
            os.replace(tmp, target)
        except OSError as error:
            tmp.unlink(missing_ok=True)
            result.errors.append(f"{folder}/{relative}: {error.strerror or error}")
            continue
        touch(target)
        copied.append(relative)

    # 2. Borrar lo que ya no está en el origen (y las carpetas que queden vacías).
    if target_root.is_dir():
        for root, dirs, names in os.walk(target_root, topdown=False, followlinks=False):
            for name in names:
                path = Path(root) / name
                relative = path.relative_to(target_root).as_posix()
                if _key(relative) in files:
                    continue
                try:
                    path.unlink()
                except OSError as error:
                    result.errors.append(f"{folder}/{relative}: {error.strerror or error}")
                    continue
                touch(path)
                if not _skipped(name):
                    deleted.append(relative)
            for name in dirs:
                path = Path(root) / name
                if path.is_symlink():
                    continue
                try:
                    path.rmdir()  # solo si está vacía
                except OSError:
                    pass

    result.copied += len(copied)
    result.deleted += len(deleted)
    if copied or deleted:
        log(f"{folder}: {len(copied)} copiados y {len(deleted)} borrados desde {source}")
        for verb, names in (("copiado", copied), ("borrado", deleted)):
            for relative in names[:LOGGED_NAMES]:
                log(f"   {verb}: {folder}/{relative}")
            if len(names) > LOGGED_NAMES:
                log(f"   ... y {len(names) - LOGGED_NAMES} más")


def sync_sources(settings: Settings, touched: set[str] | None = None, log=print) -> SyncResult:
    """Iguala cada carpeta del cliente que tenga origen. ``touched`` recibe las rutas que se
    escribieron o borraron (el vigilante las ignora para no regenerar dos veces)."""
    result = SyncResult()
    for folder, source in settings.sources:
        sync_folder(folder, source, settings.client_dir, result, touched, log)
    return result
