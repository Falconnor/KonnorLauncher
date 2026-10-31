"""Bloques ZIP de assets para distribuir los miles de archivos pequeños de ``assets/``.

Descargar ~9.000 archivos sueltos sería lento (una petición por archivo). Por eso se agrupan en
16 bloques ZIP: los de ``assets/objects/XX/`` según el primer carácter hex de ``XX``
(00-0f → bloque 0, 10-1f → bloque 1...), y ``assets/indexes/``, ``assets/log_configs/`` y el resto
en el bloque 0. El launcher descarga un bloque mientras extrae el anterior.

Dentro de cada ZIP, el nombre de cada entrada es su ruta relativa a la carpeta del cliente
(con ``/``): el launcher usa ese nombre para extraerla y para compararla con el manifest.

Decisiones:

- **Incremental**: solo se reconstruyen los bloques cuyos archivos cambiaron.
- **Nombre con hash** (``assets_block_3-1a2b3c4d5e6f.zip``): un bloque publicado nunca se
  sobrescribe. Mientras alguien descarga la versión anterior, ese archivo sigue existiendo.
  El launcher descarga el nombre que indique el manifest, así que no necesita cambios.
- **Limpieza**: se conservan los bloques del manifest nuevo y del anterior; el resto se borra.
"""

from __future__ import annotations

import os
import re
import zipfile
from pathlib import Path

from Services.hash_cache import HashCache, sha256_file


BLOCK_COUNT = 16
_BLOCK_NAME = re.compile(r"^assets_block_([0-9a-f])(?:-[0-9a-f]+)?\.zip$")


def block_id_of(name: str) -> int | None:
    """Número de bloque a partir del nombre del ZIP (admite el formato antiguo sin hash)."""
    match = _BLOCK_NAME.match(name)
    return int(match.group(1), 16) if match else None


def assign_block(relative_posix: str) -> int:
    """Bloque (0-15) de un archivo de assets según su ruta relativa a la carpeta del cliente."""
    parts = relative_posix.split("/")
    # assets/objects/XX/archivo → primer carácter hex de XX
    if len(parts) >= 3 and parts[1] == "objects" and parts[2]:
        try:
            return int(parts[2][0], 16)
        except ValueError:
            return 0
    # indexes, log_configs o cualquier otro → bloque 0
    return 0


def collect_assets(client_dir: Path) -> dict[int, list[tuple[str, Path]]]:
    """Archivos de ``assets/`` agrupados por bloque, en orden estable."""
    blocks: dict[int, list[tuple[str, Path]]] = {i: [] for i in range(BLOCK_COUNT)}
    assets_dir = client_dir / "assets"
    if not assets_dir.is_dir():
        return blocks
    for path in sorted(assets_dir.rglob("*")):
        if path.is_file() and not path.name.endswith((".tmp", ".part")):
            relative = path.relative_to(client_dir).as_posix()
            blocks[assign_block(relative)].append((relative, path))
    return blocks


def _build_zip(packages_dir: Path, block_id: int, files: list[tuple[str, Path]]) -> tuple[str, Path]:
    """Crea el ZIP en un temporal y lo publica con su hash en el nombre."""
    tmp = packages_dir / f"assets_block_{block_id:x}.zip.tmp"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zf:
        for relative, path in files:
            zf.write(path, relative)
    digest = sha256_file(tmp)
    name = f"assets_block_{block_id:x}-{digest[:12]}.zip"
    final = packages_dir / name
    if final.exists():
        tmp.unlink()  # mismo contenido ya publicado: no se toca el archivo existente
    else:
        os.replace(tmp, final)
    return name, final


def build_blocks(client_dir: Path, cache: HashCache, previous: dict | None, force: bool = False,
                 log=print) -> dict:
    """Devuelve la sección ``asset_blocks`` del manifest, reconstruyendo solo lo necesario.

    Un bloque se reutiliza si en el manifest anterior hay un bloque con el mismo número, los
    mismos archivos con el mismo hash y tamaño, y su ZIP sigue en disco con el tamaño registrado.
    """
    packages_dir = client_dir / "packages"
    packages_dir.mkdir(parents=True, exist_ok=True)

    # Bloques anteriores indexados por número.
    previous_by_id: dict[int, tuple[str, dict]] = {}
    for name, block in (previous or {}).items():
        block_id = block_id_of(name)
        if block_id is not None and isinstance(block, dict):
            previous_by_id[block_id] = (name, block)

    result: dict[str, dict] = {}
    rebuilt = 0
    for block_id, files in collect_assets(client_dir).items():
        if not files:
            continue

        current = {
            relative: {"sha256": cache.sha256(path), "size": path.stat().st_size}
            for relative, path in files
        }

        prev = previous_by_id.get(block_id)
        if not force and prev:
            prev_name, prev_block = prev
            prev_files = {
                rel: {"sha256": str(meta.get("sha256", "")).lower(), "size": meta.get("size")}
                for rel, meta in prev_block.get("files", {}).items()
            }
            zip_path = packages_dir / prev_name
            if (prev_files == current and zip_path.is_file()
                    and zip_path.stat().st_size == prev_block.get("size")):
                result[prev_name] = prev_block
                continue

        log(f"Generando bloque {block_id:x} ({len(files)} archivos)...")
        name, path = _build_zip(packages_dir, block_id, files)
        size = path.stat().st_size
        result[name] = {"sha256": sha256_file(path), "size": size, "files": current}
        rebuilt += 1
        log(f"  -> {name}: {size / (1024 * 1024):.1f} MB")

    log(f"Bloques: {len(result)} ({rebuilt} regenerados, {len(result) - rebuilt} reutilizados)")
    return dict(sorted(result.items()))


def clean_old_blocks(client_dir: Path, keep: set[str], log=print) -> None:
    """Borra bloques y temporales que ya no referencia ni el manifest actual ni el anterior."""
    packages_dir = client_dir / "packages"
    if not packages_dir.is_dir():
        return
    for path in packages_dir.iterdir():
        if not path.is_file():
            continue
        if path.name.endswith(".zip.tmp") or (block_id_of(path.name) is not None and path.name not in keep):
            try:
                path.unlink()
                log(f"Bloque antiguo eliminado: {path.name}")
            except OSError as error:  # por ejemplo, si alguien lo está descargando en este momento
                log(f"No se pudo eliminar {path.name}: {error}")
        elif block_id_of(path.name) is None:
            log(f"[AVISO] {path.name} no es un bloque generado, pero se publica en /client/packages/")


if __name__ == "__main__":
    print("Los bloques se generan junto con el manifest para que sus hashes coincidan. Usa:")
    print("    python -m Services.generate_manifest --forzar-bloques")
    raise SystemExit(1)
