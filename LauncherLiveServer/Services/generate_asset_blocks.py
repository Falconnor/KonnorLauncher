"""Genera bloques ZIP de assets para distribución eficiente.

Agrupa los archivos de assets/objects/ en 16 bloques ZIP basados
en el primer carácter hex del directorio padre (00-0f → bloque 0, etc.).
Los archivos de assets/indexes/ y assets/log_configs/ se incluyen en el bloque 0.

Uso independiente::

    python generate_asset_blocks.py
"""

from __future__ import annotations

import hashlib
import json
import zipfile
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent
CLIENT_DIR = BASE_DIR / "Client"
ASSETS_DIR = CLIENT_DIR / "assets"
PACKAGES_DIR = CLIENT_DIR / "packages"
BLOCK_COUNT = 16


def calculate_sha256(file_path: Path) -> str:
    """Calcula SHA-256 por bloques de 1 MB."""
    sha256 = hashlib.sha256()
    with file_path.open("rb") as file:
        while chunk := file.read(1024 * 1024):
            sha256.update(chunk)
    return sha256.hexdigest()


def assign_block(relative_posix: str) -> int:
    """Asigna un archivo de asset a un bloque (0-15).

    - ``assets/objects/XX/...`` → bloque según primer carácter hex de *XX*.
    - ``assets/indexes/...`` y ``assets/log_configs/...`` → bloque 0.
    """
    parts = relative_posix.split("/")
    # assets/objects/XX/filename
    if len(parts) >= 3 and parts[1] == "objects":
        hex_prefix = parts[2]
        if hex_prefix:
            try:
                return int(hex_prefix[0], 16)
            except ValueError:
                return 0
    # indexes, log_configs o cualquier otro → bloque 0
    return 0


def generate_blocks() -> dict:
    """Genera los bloques ZIP en ``Client/packages/`` y devuelve metadata.

    Retorna un diccionario con la estructura::

        {
            "assets_block_0.zip": {
                "sha256": "<hash del ZIP>",
                "size": <bytes del ZIP>,
                "files": {
                    "assets/objects/00/00abc...": {"sha256": "...", "size": 100},
                    ...
                }
            },
            ...
        }
    """
    if not ASSETS_DIR.exists():
        print(f"No existe el directorio de assets: {ASSETS_DIR}")
        return {}

    PACKAGES_DIR.mkdir(parents=True, exist_ok=True)

    # Recolectar archivos por bloque
    blocks: dict[int, list[tuple[str, Path]]] = {i: [] for i in range(BLOCK_COUNT)}

    for file_path in sorted(ASSETS_DIR.rglob("*")):
        if not file_path.is_file():
            continue
        relative = file_path.relative_to(CLIENT_DIR).as_posix()
        block_id = assign_block(relative)
        blocks[block_id].append((relative, file_path))

    # Generar ZIPs y metadata
    asset_blocks: dict[str, dict] = {}

    for block_id in range(BLOCK_COUNT):
        files = blocks[block_id]
        if not files:
            continue

        block_name = f"assets_block_{block_id:x}.zip"
        zip_path = PACKAGES_DIR / block_name

        print(f"Generando {block_name} ({len(files)} archivos)...")

        # Crear ZIP
        block_files: dict[str, dict] = {}
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            for relative, file_path in files:
                zf.write(file_path, relative)
                block_files[relative] = {
                    "sha256": calculate_sha256(file_path),
                    "size": file_path.stat().st_size,
                }

        # Hash y tamaño del ZIP
        zip_hash = calculate_sha256(zip_path)
        zip_size = zip_path.stat().st_size

        asset_blocks[block_name] = {
            "sha256": zip_hash,
            "size": zip_size,
            "files": block_files,
        }

        print(
            f"  -> {block_name}: {zip_size / (1024 * 1024):.1f} MB, "
            f"{len(files)} archivos"
        )

    return asset_blocks


if __name__ == "__main__":
    result = generate_blocks()
    meta_path = PACKAGES_DIR / "asset_blocks_meta.json"
    with meta_path.open("w", encoding="utf-8") as f:
        json.dump(result, f, indent=4, ensure_ascii=False)
    print(f"\nMetadata guardada en: {meta_path}")
    print(f"Total bloques: {len(result)}")
    total_files = sum(len(b["files"]) for b in result.values())
    print(f"Total archivos: {total_files}")
