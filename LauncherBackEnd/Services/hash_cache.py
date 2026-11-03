"""Caché de SHA-256 (y SHA-512, para los archivos que pueden venir de Modrinth) por archivo.

Calcular el hash de 1,3 GB de assets en cada arranque sería lento. La caché guarda, para cada
ruta, el tamaño, la fecha de modificación (en nanosegundos) y el hash; si los dos primeros no
cambiaron, el hash se reutiliza. Es el mismo criterio que usan herramientas como rsync o make.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path


def sha256_file(path: Path) -> str:
    """SHA-256 en hexadecimal, leyendo por bloques de 1 MB."""
    return hash_file(path)[0]


def hash_file(path: Path, with_sha512: bool = False) -> tuple[str, str | None]:
    """SHA-256 y, si se pide, SHA-512 en hexadecimal, en una sola lectura por bloques de 1 MB."""
    sha256 = hashlib.sha256()
    sha512 = hashlib.sha512() if with_sha512 else None
    with path.open("rb") as file:
        while chunk := file.read(1024 * 1024):
            sha256.update(chunk)
            if sha512:
                sha512.update(chunk)
    return sha256.hexdigest(), sha512.hexdigest() if sha512 else None


class HashCache:
    """Caché persistente ``ruta → (tamaño, mtime_ns, sha256[, sha512])``."""

    def __init__(self, file: Path):
        self._file = file
        self._entries: dict[str, list] = {}
        self._used: set[str] = set()
        self.hits = 0
        self.misses = 0
        try:
            data = json.loads(file.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                self._entries = data
        except (FileNotFoundError, ValueError):
            pass

    def sha256(self, path: Path) -> str:
        """Hash del archivo, reutilizando el de la caché si tamaño y fecha coinciden."""
        return self.hashes(path)[0]

    def hashes(self, path: Path, with_sha512: bool = False) -> tuple[str, str | None]:
        """SHA-256 y (si se pide) SHA-512, reutilizando los de la caché si tamaño y fecha coinciden."""
        key = str(path)
        stat = path.stat()
        self._used.add(key)
        entry = self._entries.get(key)
        if (entry and entry[0] == stat.st_size and entry[1] == stat.st_mtime_ns
                and (not with_sha512 or len(entry) > 3)):
            self.hits += 1
            return entry[2], entry[3] if with_sha512 else None
        self.misses += 1
        sha256, sha512 = hash_file(path, with_sha512)
        self._entries[key] = [stat.st_size, stat.st_mtime_ns, sha256] + ([sha512] if sha512 else [])
        return sha256, sha512

    def save(self) -> None:
        """Guarda solo las entradas usadas en esta pasada (las de archivos borrados desaparecen)."""
        entries = {key: value for key, value in self._entries.items() if key in self._used}
        self._file.parent.mkdir(parents=True, exist_ok=True)
        tmp = self._file.with_name(self._file.name + ".tmp")
        tmp.write_text(json.dumps(entries, separators=(",", ":")), encoding="utf-8")
        os.replace(tmp, self._file)
