"""Genera ``client_manifest.json`` a partir de la carpeta del cliente.

Formato (el que espera Konnor Launcher)::

    {
      "client_version": "1.0.1",                          ← de Services/client_version.txt
      "delete_unauthorized": true,                        ← Seguridad: borrar lo que no esté aquí
      "always_online": true,                              ← Seguridad: sin servidor no se juega
      "lock_server_url": true,                            ← Seguridad: dirección no modificable
      "protect_packs": false,                             ← Seguridad: packs obligatorios y protegidos
      "released": false,                                  ← Seguridad: instancias liberadas
      "minecraft": {"version": "1.21.1", "loader": "fabric", "loader_version": "0.19.3"},
      "files": { "mods/x.jar": {"sha256": ..., "sha512": ..., "size": ...}, ... },
      "asset_blocks": { "assets_block_0-<hash>.zip": {"sha256", "size", "files": {...}}, ... }
    }

- ``minecraft``: la versión y el cargador que hay en ``versions/`` (ver ``game_version.py``). Con
  ella, el launcher instala el juego desde Mojang y el cargador desde su web oficial, así que
  ``versions/``, ``libraries/`` y ``assets/`` no se publican (``asset_blocks`` queda vacío). Si no
  se reconoce, no aparece y esas carpetas se publican como antes.
- ``files``: todo lo que hay en la carpeta del cliente excepto ``assets/`` (va en los bloques),
  ``packages/`` (los propios bloques) y archivos basura o temporales. Los archivos de primer nivel de
  ``mods/``, ``resourcepacks/`` y ``shaderpacks/`` llevan además su ``sha512``: con él, el launcher
  los busca en Modrinth y los descarga de allí; los que no están en Modrinth los descarga de este
  servidor. La comprobación siempre es contra el ``sha256`` de este manifest.
- ``asset_blocks``: ver ``generate_asset_blocks.py``.

Antes de generar, las carpetas del cliente que tengan carpeta de origen se igualan a ella (ver
``client_sources.py``).

El servidor lo ejecuta solo al arrancar y cada vez que cambia la carpeta del cliente. También se
puede lanzar a mano::

    python -m Services.generate_manifest                  # actualiza el manifest
    python -m Services.generate_manifest --forzar-bloques # además rehace todos los ZIP
    python -m Services.generate_manifest --salida otro.json
    python -m Services.generate_manifest --servidor <id>   # otro servidor (por defecto, el seleccionado)
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

from Services.client_sources import sync_sources
from Services.game_version import describe, detect_game
from Services.generate_asset_blocks import build_blocks, clean_old_blocks
from Services.hash_cache import HashCache
from Services.settings import Settings, is_released, load_settings


# Carpetas de primer nivel del cliente que no van en "files".
EXCLUDED_TOP_LEVEL = {"assets", "packages"}
# Las que instala el propio launcher cuando el manifest publica la versión del juego.
GAME_TOP_LEVEL = {"versions", "libraries"}
# Carpetas cuyos archivos pueden estar en Modrinth (se publica también su SHA-512).
MODRINTH_FOLDERS = {"mods", "resourcepacks", "shaderpacks"}
# Archivos que nunca se publican (basura del sistema o temporales).
EXCLUDED_NAMES = {"Thumbs.db", "desktop.ini", ".DS_Store"}
EXCLUDED_SUFFIXES = (".tmp", ".part")

# Mismas reglas de ruta que aplica el launcher: si una sola ruta las incumple, el launcher
# rechaza el manifest entero, así que se detectan aquí antes de publicarlo.
_FORBIDDEN = re.compile(r'[<>:"|?*\x00-\x1f]')


@dataclass
class GenerationResult:
    ok: bool
    changed: bool = False
    files: int = 0
    blocks: int = 0
    assets: int = 0
    version: str = ""
    game: str = ""                # versión de Minecraft y cargador publicados ("" = no reconocida)
    seconds: float = 0.0
    error: str = ""
    copied: int = 0               # archivos copiados desde las carpetas de origen
    deleted: int = 0              # archivos borrados porque ya no están en su origen
    missing_sources: list[str] = field(default_factory=list)  # orígenes que no existen


def invalid_path_reason(relative: str) -> str | None:
    """Motivo por el que el launcher rechazaría la ruta, o None si es válida."""
    for part in relative.split("/"):
        if part in ("", ".", ".."):
            return "segmento vacío, '.' o '..'"
        if _FORBIDDEN.search(part):
            return 'carácter no permitido (<>:"|?* o de control)'
        if part.endswith((".", " ")):
            return "segmento que termina en punto o espacio"
    return None


def read_client_version(settings: Settings) -> str:
    """Versión del cliente desde el archivo de versión (vacía si no existe)."""
    try:
        return settings.version_file.read_text(encoding="utf-8").strip()
    except FileNotFoundError:
        return ""


def load_manifest(path: Path) -> dict:
    """Manifest existente, o un diccionario vacío si no existe o está dañado."""
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (FileNotFoundError, ValueError):
        return {}


def collect_files(client_dir: Path, cache: HashCache,
                  excluded: set[str] = EXCLUDED_TOP_LEVEL) -> tuple[dict[str, dict], list[str]]:
    """Archivos sueltos para la sección ``files`` y la lista de rutas inválidas. Se omiten las
    carpetas de primer nivel de ``excluded``."""
    files: dict[str, dict] = {}
    invalid: list[str] = []
    for path in sorted(client_dir.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(client_dir).as_posix()
        parts = relative.split("/")
        if parts[0] in excluded:
            continue
        if path.name in EXCLUDED_NAMES or path.name.endswith(EXCLUDED_SUFFIXES):
            continue
        if reason := invalid_path_reason(relative):
            invalid.append(f"{relative}  ({reason})")
            continue
        on_modrinth = len(parts) == 2 and parts[0] in MODRINTH_FOLDERS
        sha256, sha512 = cache.hashes(path, with_sha512=on_modrinth)
        files[relative] = {"sha256": sha256, **({"sha512": sha512} if sha512 else {}),
                           "size": path.stat().st_size}
    return files, invalid


def write_atomic(path: Path, data: dict) -> None:
    """Escribe el JSON en un temporal y lo reemplaza: el servidor nunca sirve un archivo a medias."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    with tmp.open("w", encoding="utf-8", newline="\n") as file:
        json.dump(data, file, indent=4, ensure_ascii=False)
        file.write("\n")
    os.replace(tmp, path)


def generate_manifest(settings: Settings | None = None, force_blocks: bool = False,
                      output: Path | None = None, log=print,
                      touched: set[str] | None = None) -> GenerationResult:
    """Copia las carpetas de origen y genera el manifest completo. Solo reescribe el archivo si el
    contenido cambió. ``touched`` recibe las rutas del cliente que cambió la copia."""
    settings = settings or load_settings()
    output = output or settings.manifest_file
    started = time.perf_counter()
    client_dir = settings.client_dir

    # 0. Carpetas de origen. Si una copia falla, publicar dejaría a los jugadores con una mezcla
    # de archivos viejos y nuevos: el manifest no se toca hasta que se pueda copiar todo.
    synced = sync_sources(settings, touched, log=log)
    if synced.errors:
        log("[ERROR] No se pudo copiar desde las carpetas de origen:")
        for entry in synced.errors:
            log(f"   {entry}")
        return GenerationResult(ok=False, copied=synced.copied, deleted=synced.deleted,
                                missing_sources=synced.missing,
                                error=f"{len(synced.errors)} errores al copiar desde las carpetas "
                                      f"de origen; el manifest no se modificó")

    if not client_dir.is_dir():
        return GenerationResult(ok=False, error=f"No existe la carpeta del cliente: {client_dir}")

    previous = load_manifest(settings.manifest_file)
    previous_blocks = previous.get("asset_blocks") if isinstance(previous.get("asset_blocks"), dict) else {}
    cache = HashCache(settings.hash_cache_file)

    # 1. Versión del juego. Si se reconoce, el launcher instala versions, libraries y assets.
    game = detect_game(client_dir)
    if game:
        log(f"Juego: {describe(game)} (lo instala el launcher; no se publican versions, libraries ni assets)")
    else:
        log("No se reconoce la versión de versions/: se publican versions, libraries y assets")

    # 2. Bloques de assets (solo se reconstruyen los que cambiaron).
    asset_blocks = {} if game else build_blocks(client_dir, cache, previous_blocks, force=force_blocks,
                                                log=log, block_count=settings.block_count)
    invalid = [
        f"{rel}  ({reason})"
        for block in asset_blocks.values()
        for rel in block["files"]
        if (reason := invalid_path_reason(rel))
    ]

    # 3. Archivos sueltos.
    files, invalid_files = collect_files(
        client_dir, cache, EXCLUDED_TOP_LEVEL | GAME_TOP_LEVEL if game else EXCLUDED_TOP_LEVEL)
    invalid = invalid_files + invalid
    cache.save()

    if invalid:
        log("[ERROR] Estas rutas harían que el launcher rechace el manifest. Renómbralas:")
        for entry in invalid:
            log(f"   {entry}")
        return GenerationResult(ok=False, error=f"{len(invalid)} rutas no válidas; el manifest no se modificó")

    # 4. Manifest final (solo se escribe si cambió).
    version = read_client_version(settings)
    manifest: dict = {"client_version": version} if version else {}
    # Los launchers sincronizados aplican esta decisión (los antiguos ignoran el campo).
    manifest["delete_unauthorized"] = settings.delete_unauthorized
    manifest["always_online"] = settings.always_online
    manifest["lock_server_url"] = settings.lock_server_url
    manifest["protect_packs"] = settings.protect_packs
    manifest["released"] = is_released(settings.profile_id)  # al día aunque el servidor no se reinicie
    if game:
        manifest["minecraft"] = game
    manifest |= {"files": files, "asset_blocks": asset_blocks}

    changed = load_manifest(output) != manifest
    if changed:
        write_atomic(output, manifest)

    # 5. Bloques que ya no usa nadie (se conservan los del manifest anterior).
    if output == settings.manifest_file:
        clean_old_blocks(client_dir, keep=set(asset_blocks) | set(previous_blocks), log=log)

    result = GenerationResult(
        ok=True, changed=changed, files=len(files), blocks=len(asset_blocks),
        assets=sum(len(b["files"]) for b in asset_blocks.values()), version=version,
        game=describe(game) if game else "",
        seconds=time.perf_counter() - started,
        copied=synced.copied, deleted=synced.deleted, missing_sources=synced.missing,
    )
    log(f"Manifest {'actualizado' if changed else 'sin cambios'}: versión {version or '-'}, "
        f"{result.game or 'juego publicado como archivos'}, "
        f"{result.files} archivos, {result.blocks} bloques ({result.assets} assets), "
        f"{result.seconds:.1f} s (hashes en caché: {cache.hits}, calculados: {cache.misses})")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Genera client_manifest.json para Konnor Launcher")
    parser.add_argument("--forzar-bloques", action="store_true",
                        help="rehace todos los ZIP de assets aunque no hayan cambiado")
    parser.add_argument("--salida", type=Path, default=None,
                        help="ruta del manifest generado (por defecto, la configurada)")
    parser.add_argument("--servidor", default=None,
                        help="id del servidor (por defecto, KONNOR_PROFILE o el seleccionado en el panel)")
    args = parser.parse_args()
    settings = load_settings(args.servidor)
    print(f"Servidor: {settings.profile_name} ({settings.profile_id})")
    result = generate_manifest(settings, force_blocks=args.forzar_bloques,
                               output=args.salida.resolve() if args.salida else None)
    if not result.ok:
        print(f"[ERROR] {result.error}")
    return 0 if result.ok else 1


if __name__ == "__main__":
    sys.exit(main())
