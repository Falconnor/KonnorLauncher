"""Genera el manifest completo del cliente.

Combina los archivos individuales (mods, versions, libraries) con los
bloques de assets generados por ``generate_asset_blocks``.

Uso::

    python generate_manifest.py
"""

from pathlib import Path
import hashlib
import json

from generate_asset_blocks import generate_blocks


# RUTAS
BASE_DIR = Path(__file__).resolve().parent.parent
CLIENT_DIR = BASE_DIR / "Client"
MANIFEST_FILE = BASE_DIR / "client_manifest.json"

# Carpetas cuyos archivos se excluyen del listado individual porque se
# gestionan como bloques o no son archivos del juego.
EXCLUDED_PREFIXES = ("assets/", "packages/")


def calculate_sha256(file_path: Path) -> str:
    sha256 = hashlib.sha256()

    with file_path.open("rb") as file:
        while chunk := file.read(1024 * 1024):
            sha256.update(chunk)

    return sha256.hexdigest()


def generate_manifest():
    if not CLIENT_DIR.exists():
        print(f"No existe el directorio Client: {CLIENT_DIR}")
        return

    # Fase 1: generar bloques de assets
    print("=== Generando bloques de assets ===")
    asset_blocks = generate_blocks()
    print()

    # Fase 2: procesar archivos individuales (excluyendo assets y packages)
    print("=== Procesando archivos individuales ===")
    files = {}

    for file_path in CLIENT_DIR.rglob("*"):

        if not file_path.is_file():
            continue

        relative_path = file_path.relative_to(CLIENT_DIR)

        # Usamos "/" independientemente de Windows/Linux
        relative_posix = relative_path.as_posix()

        # Omitir assets (gestionados por bloques) y packages (archivos ZIP)
        if any(relative_posix.startswith(prefix) for prefix in EXCLUDED_PREFIXES):
            continue

        print(f"Procesando: {relative_posix}")

        files[relative_posix] = {
            "sha256": calculate_sha256(file_path),
            "size": file_path.stat().st_size
        }

    manifest = {
        "files": files,
        "asset_blocks": asset_blocks,
    }

    with MANIFEST_FILE.open("w", encoding="utf-8") as file:
        json.dump(
            manifest,
            file,
            indent=4,
            ensure_ascii=False
        )

    total_asset_files = sum(len(b["files"]) for b in asset_blocks.values())

    print()
    print("===== MANIFEST =====")
    print(f"Archivos individuales: {len(files)}")
    print(f"Bloques de assets:     {len(asset_blocks)}")
    print(f"Assets en bloques:     {total_asset_files}")
    print(f"Salida:   {MANIFEST_FILE}")
    print("====================")


if __name__ == "__main__":
    generate_manifest()