"""Caché de SHA-256 por archivo.

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
    digest = hashlib.sha256()
    with path.open("rb") as file:
        while chunk := file.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


class HashCache:
    """Caché persistente ``ruta → (tamaño, mtime_ns, sha256)``."""

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
        key = str(path)
        stat = path.stat()
        self._used.add(key)
        entry = self._entries.get(key)
        if entry and entry[0] == stat.st_size and entry[1] == stat.st_mtime_ns:
            self.hits += 1
            return entry[2]
        self.misses += 1
        digest = sha256_file(path)
        self._entries[key] = [stat.st_size, stat.st_mtime_ns, digest]
        return digest

    def save(self) -> None:
        """Guarda solo las entradas usadas en esta pasada (las de archivos borrados desaparecen)."""
        entries = {key: value for key, value in self._entries.items() if key in self._used}
        self._file.parent.mkdir(parents=True, exist_ok=True)
        tmp = self._file.with_name(self._file.name + ".tmp")
        tmp.write_text(json.dumps(entries, separators=(",", ":")), encoding="utf-8")
        os.replace(tmp, self._file)
