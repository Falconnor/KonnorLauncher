"""Archivos en disco de un servidor: crearlos al darlo de alta y borrarlos al eliminarlo.

Borrar es la operación peligrosa del panel, así que solo se elimina lo que cumple todo esto:

- está dentro de la carpeta LauncherBackEnd (``LauncherBackEnd/``), nunca fuera: una carpeta del
  cliente en otro disco la eligió alguien a mano y puede contener cualquier cosa;
- no es parte del programa (``Services/``, ``Panel/``, ``.venv/``, ``.git/``) ni una de sus
  carpetas de primer nivel (``tests/``, ``profiles/``);
- no lo usa otro servidor (misma ruta, una carpeta que la contiene o una que está dentro).

Lo que no cumple alguna condición se conserva y el panel explica por qué.
"""

from __future__ import annotations

import os
import shutil
from pathlib import Path

from Services.settings import BASE_DIR, PATH_KEYS, PROFILES_DIR, get_profile, read_store, resolve_path


# Carpetas del programa: ni ellas ni nada de dentro se borra nunca.
PROTECTED_TREES = ("Services", "Panel", ".venv", ".git")
# Carpetas contenedoras: se puede borrar lo que hay dentro, pero no la carpeta en sí.
PROTECTED_ROOTS = ("tests", PROFILES_DIR)
# Subcarpetas con las que se crea la carpeta del cliente de un servidor nuevo.
CLIENT_SUBDIRS = ("mods", "versions", "libraries", "assets")

LABELS = {
    "client_dir": "Carpeta del cliente",
    "manifest": "Manifest",
    "version_file": "Archivo de versión",
    "cache_dir": "Caché de hashes",
    "profile_dir": "Carpeta del servidor",
}


def create_client_dir(profile_id: str) -> Path:
    """Crea la carpeta del cliente de un servidor con su estructura básica."""
    client_dir = resolve_path(get_profile(profile_id)["client_dir"])
    for name in CLIENT_SUBDIRS:
        (client_dir / name).mkdir(parents=True, exist_ok=True)
    return client_dir


def _inside(path: Path, parent: Path) -> bool:
    return path == parent or parent in path.parents


def _size(path: Path) -> int:
    """Tamaño total en bytes; los enlaces y uniones no se siguen."""
    if path.is_symlink():
        return 0
    if path.is_file():
        return path.stat().st_size
    total = 0
    for root, dirs, files in os.walk(path, followlinks=False):
        for name in files:
            try:
                total += (Path(root) / name).lstat().st_size
            except OSError:
                pass
    return total


def _keep_reason(path: Path, others: list[tuple[str, Path]]) -> str | None:
    """Por qué NO se puede borrar una ruta, o None si se puede."""
    if not _inside(path, BASE_DIR) or path == BASE_DIR:
        return "Está fuera de la carpeta LauncherBackEnd"
    relative = path.relative_to(BASE_DIR)
    if relative.parts[0] in PROTECTED_TREES:
        return "Es parte del programa"
    if len(relative.parts) == 1 and relative.parts[0] in PROTECTED_ROOTS:
        return "Es una carpeta compartida del servidor"
    for name, other in others:
        if _inside(path, other) or _inside(other, path):
            return f"La usa el servidor «{name}»"
    return None


def describe_files(profile_id: str) -> list[dict]:
    """Archivos existentes de un servidor y si se borrarían o se conservarían (y por qué).

    Si una carpeta se borra entera, lo que hay dentro no se lista aparte.
    """
    profile = get_profile(profile_id)
    others = [
        (other["name"], resolve_path(other[key]))
        for other in (get_profile(b["id"]) for b in read_store()["servers"] if b["id"] != profile_id)
        for key in PATH_KEYS
    ]
    candidates = [(key, resolve_path(profile[key])) for key in PATH_KEYS]
    candidates.append(("profile_dir", (BASE_DIR / PROFILES_DIR / profile_id).resolve()))

    items: list[dict] = []
    for key, path in candidates:
        if not path.exists() or any(item["path"] == str(path) for item in items):
            continue
        reason = _keep_reason(path, others)
        items.append({
            "key": key,
            "label": LABELS[key],
            "path": str(path),
            "is_dir": path.is_dir(),
            "size": _size(path),
            "delete": reason is None,
            "reason": reason,
        })

    # Lo que queda dentro de una carpeta que se borra entera ya está incluido en ella.
    deleted_dirs = [Path(i["path"]) for i in items if i["delete"] and i["is_dir"]]
    return [
        item for item in items
        if not any(Path(item["path"]) != d and _inside(Path(item["path"]), d) for d in deleted_dirs)
    ]


def delete_files(items: list[dict]) -> tuple[list[str], list[str]]:
    """Borra los elementos marcados como borrables. Devuelve (borrados, errores)."""
    deleted, errors = [], []
    for item in items:
        if not item["delete"]:
            continue
        path = Path(item["path"])
        try:
            if path.is_dir() and not path.is_symlink():
                shutil.rmtree(path)  # en Windows no entra en uniones ni enlaces
            else:
                path.unlink(missing_ok=True)
            deleted.append(str(path))
        except OSError as error:
            errors.append(f"{path}: {error.strerror or error}")
    return deleted, errors
