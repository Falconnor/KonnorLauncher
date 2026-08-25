"""Verificador de integridad del cliente.

Se puede ejecutar de forma aislada con ``python verify_client.py --dry-run``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
import zipfile
import concurrent.futures
from dataclasses import dataclass
from pathlib import Path
from typing import Callable
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import urlopen


# CONFIGURACION BASE
# Las rutas se calculan desde la carpeta donde vive el launcher.
LAUNCH_PATH = Path(__file__).resolve().parent
PROPERTIES_PATH = LAUNCH_PATH / "launcher.properties"
# Solo estas carpetas son administradas por el launcher. La limpieza no toca
# config, saves, screenshots ni otros datos personales del jugador.
PROTECTED_FOLDERS = ("mods", "versions", "libraries", "assets")
SHA256_PATTERN = re.compile(r"^[0-9a-fA-F]{64}$")


class VerificationError(RuntimeError):
    """Error que bloquea el inicio del juego."""


@dataclass(frozen=True)
class ManifestFile:
    """Representa una entrada ya validada del manifest descargado."""

    relative_path: Path
    url_path: str
    sha256: str
    size: int


@dataclass(frozen=True)
class AssetBlock:
    """Representa un bloque ZIP de assets con sus archivos internos."""

    name: str
    sha256: str
    size: int
    files: list[ManifestFile]


# LECTURA DE launcher.properties
def load_properties(path: Path = PROPERTIES_PATH) -> dict[str, str]:
    """Lee claves ``clave=valor`` e ignora comentarios y líneas vacías."""
    properties: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        properties[key.strip()] = value.strip()
    return properties


def get_game_path(properties: dict[str, str]) -> Path:
    """Convierte ``game.path`` en una ruta absoluta del directorio del juego."""
    configured_path = properties.get("game.path", "").strip()
    if not configured_path:
        raise VerificationError("game.path no está configurado")

    path = Path(configured_path)
    if not path.is_absolute():
        path = LAUNCH_PATH / path
    return path.resolve()


def parse_bool(value: str | None) -> bool:
    """Solo el valor literal ``true`` activa una opción booleana."""
    return value is not None and value.strip().lower() == "true"


# VALIDACION DEL MANIFEST
def normalize_manifest_path(value: object) -> tuple[Path, str]:
    """Normaliza la ruta y rechaza rutas absolutas o con ``..``.

    Devuelve una ruta local segura y otra con ``/`` para construir la URL.
    """
    if not isinstance(value, str) or not value.strip():
        raise VerificationError("El manifest contiene una ruta vacía")

    normalized = value.replace("\\", "/")
    parts = normalized.split("/")
    if (
        normalized.startswith("/")
        or any(part in ("", ".", "..") for part in parts)
        or Path(value).is_absolute()
        or (len(parts[0]) == 2 and parts[0][1] == ":")
    ):
        raise VerificationError(f"Ruta inválida en manifest: {value}")

    return Path(*parts), "/".join(parts)


def parse_manifest(payload: object) -> list[ManifestFile]:
    """Comprueba estructura, tamaños y hashes antes de tocar archivos locales."""
    if not isinstance(payload, dict) or not isinstance(payload.get("files"), dict):
        raise VerificationError("El manifest no contiene una sección files válida")

    files: list[ManifestFile] = []
    for raw_path, metadata in payload["files"].items():
        if not isinstance(metadata, dict):
            raise VerificationError(f"Metadatos inválidos para {raw_path}")

        relative_path, url_path = normalize_manifest_path(raw_path)
        sha256 = metadata.get("sha256")
        size = metadata.get("size")
        if not isinstance(sha256, str) or not SHA256_PATTERN.fullmatch(sha256):
            raise VerificationError(f"SHA-256 inválido para {raw_path}")
        if not isinstance(size, int) or isinstance(size, bool) or size < 0:
            raise VerificationError(f"Tamaño inválido para {raw_path}")

        files.append(ManifestFile(relative_path, url_path, sha256.upper(), size))
    return files


def _parse_block_files(block_name: str, files_data: object) -> list[ManifestFile]:
    """Parsea la sección ``files`` de un bloque de assets."""
    if not isinstance(files_data, dict):
        raise VerificationError(f"Sección files inválida en bloque {block_name}")

    files: list[ManifestFile] = []
    for raw_path, metadata in files_data.items():
        if not isinstance(metadata, dict):
            raise VerificationError(f"Metadatos inválidos para {raw_path} en {block_name}")

        relative_path, url_path = normalize_manifest_path(raw_path)
        sha256 = metadata.get("sha256")
        size = metadata.get("size")
        if not isinstance(sha256, str) or not SHA256_PATTERN.fullmatch(sha256):
            raise VerificationError(f"SHA-256 inválido para {raw_path} en {block_name}")
        if not isinstance(size, int) or isinstance(size, bool) or size < 0:
            raise VerificationError(f"Tamaño inválido para {raw_path} en {block_name}")

        files.append(ManifestFile(relative_path, url_path, sha256.upper(), size))
    return files


def parse_asset_blocks(payload: object) -> list[AssetBlock]:
    """Parsea la sección ``asset_blocks`` del manifest.

    Si no existe la sección, devuelve una lista vacía para mantener
    compatibilidad con manifests antiguos.
    """
    if not isinstance(payload, dict):
        return []

    blocks_data = payload.get("asset_blocks")
    if not blocks_data or not isinstance(blocks_data, dict):
        return []

    blocks: list[AssetBlock] = []
    for block_name, block_meta in blocks_data.items():
        if not isinstance(block_meta, dict):
            raise VerificationError(f"Metadatos inválidos para bloque {block_name}")

        sha256 = block_meta.get("sha256")
        size = block_meta.get("size")
        if not isinstance(sha256, str) or not SHA256_PATTERN.fullmatch(sha256):
            raise VerificationError(f"SHA-256 inválido para bloque {block_name}")
        if not isinstance(size, int) or isinstance(size, bool) or size < 0:
            raise VerificationError(f"Tamaño inválido para bloque {block_name}")

        block_files = _parse_block_files(block_name, block_meta.get("files"))
        blocks.append(AssetBlock(block_name, sha256.upper(), size, block_files))

    return blocks


# DESCARGAS Y HASHES
def download_json(url: str) -> object:
    """Descarga el manifest; los problemas de red se muestran en la UI."""
    try:
        with urlopen(url, timeout=15) as response:
            return json.load(response)
    except (HTTPError, URLError, TimeoutError, json.JSONDecodeError) as error:
        raise VerificationError("servidor no responde") from error


def sha256_file(path: Path) -> str:
    """Calcula SHA-256 por bloques de 1 MB sin cargar el archivo entero en RAM."""
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def download_file(
    url: str,
    destination: Path,
    expected_hash: str,
    on_progress: Callable[[int], None],
) -> None:
    """Descarga un archivo, calcula el hash durante la descarga y lo valida.

    El SHA-256 se computa en streaming mientras se escriben los datos,
    evitando releer el archivo completo desde disco después de la descarga.
    """
    try:
        digest = hashlib.sha256()
        with urlopen(url, timeout=30) as response, destination.open("wb") as file:
            downloaded_bytes = 0
            while chunk := response.read(1024 * 1024):
                file.write(chunk)
                digest.update(chunk)
                downloaded_bytes += len(chunk)
                on_progress(downloaded_bytes)
    except (HTTPError, URLError, TimeoutError, OSError) as error:
        raise VerificationError(f"No se pudo descargar {destination.name}") from error

    if digest.hexdigest().upper() != expected_hash:
        raise VerificationError(f"Hash inválido en la descarga: {destination.name}")


# COMPARACION E INSTALACION
def local_path(game_path: Path, manifest_file: ManifestFile) -> Path:
    """Crea el destino local y garantiza que permanezca dentro del cliente."""
    path = game_path.joinpath(manifest_file.relative_path)
    try:
        path.relative_to(game_path)
    except ValueError as error:
        raise VerificationError(f"Ruta fuera del cliente: {manifest_file.url_path}") from error
    return path


def find_files_to_repair(game_path: Path, files: list[ManifestFile]) -> list[ManifestFile]:
    """Devuelve solo archivos ausentes o con tamaño distinto al manifest.

    Si existe y el tamaño coincide, se asume válido. El SHA-256 se calcula
    únicamente para validar los archivos que se descargan.
    """
    repair_files: list[ManifestFile] = []
    for manifest_file in files:
        path = local_path(game_path, manifest_file)
        try:
            same_size = path.is_file() and path.stat().st_size == manifest_file.size
        except OSError:
            same_size = False

        # El SHA-256 se reserva para validar las descargas. Un archivo existente
        # con el mismo tamaño se considera válido, para evitar hashear miles de JAR.
        if not same_size:
            repair_files.append(manifest_file)
    return repair_files


def find_blocks_to_repair(game_path: Path, blocks: list[AssetBlock]) -> list[AssetBlock]:
    """Devuelve bloques que contienen al menos un asset faltante o corrupto.

    Se verifica existencia y tamaño de cada archivo dentro del bloque.
    Si cualquier archivo del bloque falta o tiene tamaño incorrecto,
    el bloque entero se marca para descarga.
    """
    repair_blocks: list[AssetBlock] = []
    for block in blocks:
        for manifest_file in block.files:
            path = local_path(game_path, manifest_file)
            try:
                same_size = path.is_file() and path.stat().st_size == manifest_file.size
            except OSError:
                same_size = False

            if not same_size:
                repair_blocks.append(block)
                break
    return repair_blocks


def download_block(
    base_url: str,
    block: AssetBlock,
    temp_path: Path,
    on_progress: Callable[[int], None],
) -> Path:
    """Descarga un bloque ZIP y valida su hash."""
    block_url = f"{base_url}/client/packages/{quote(block.name, safe='/')}"
    zip_path = temp_path / block.name
    download_file(block_url, zip_path, block.sha256, on_progress)
    return zip_path

def extract_block(
    zip_path: Path,
    game_path: Path,
    block: AssetBlock,
    on_status: Callable[[str], None] | None = None,
) -> None:
    """Extrae un bloque ZIP y lo borra."""
    if on_status:
        on_status(f"extrayendo {block.name}")

    with zipfile.ZipFile(zip_path, "r") as zf:
        members = [mf.url_path for mf in block.files]
        zf.extractall(game_path, members)

    zip_path.unlink(missing_ok=True)


def remove_unauthorized_files(game_path: Path, allowed_paths: set[Path]) -> int:
    """Elimina archivos fuera del manifest, únicamente en carpetas protegidas."""
    removed = 0
    for folder in PROTECTED_FOLDERS:
        folder_path = game_path / folder
        if not folder_path.is_dir():
            continue
        for path in folder_path.rglob("*"):
            if path.is_file() and path.relative_to(game_path) not in allowed_paths:
                path.unlink()
                removed += 1
    return removed


# FLUJO PRINCIPAL DEL VERIFICADOR
def verify_client(status: Callable[[str], None] = print, *, dry_run: bool = False) -> int:
    """Sincroniza el cliente y devuelve 0; ante fallo devuelve 1.

    Orden de ejecución:
    1. Lee configuración y manifest.
    2. Detecta bloques de assets que necesitan reparación.
    3. Descarga y extrae solo los bloques faltantes.
    4. Detecta archivos individuales que necesitan reparación.
    5. Descarga y valida con SHA-256 solo los archivos necesarios.
    6. Si todo salió bien, elimina archivos no autorizados cuando corresponde.

    La limpieza nunca se ejecuta si falla una descarga o el hash no coincide.
    """
    try:
        properties = load_properties()
        if not parse_bool(properties.get("enable.verify")):
            status("verificacion exitosa")
            return 0

        status("verificando archivos")
        game_path = get_game_path(properties)
        game_path.mkdir(parents=True, exist_ok=True)

        base_url = properties.get("server.url", "").rstrip("/")
        if not base_url:
            raise VerificationError("server.url no está configurado")

        raw_manifest = download_json(f"{base_url}/client_manifest.json")
        manifest = parse_manifest(raw_manifest)
        asset_blocks = parse_asset_blocks(raw_manifest)

        repair_blocks = find_blocks_to_repair(game_path, asset_blocks)
        repair_files = find_files_to_repair(game_path, manifest)

        # Modo de prueba: comprueba servidor, manifest y estado local sin
        # descargar, reemplazar ni eliminar archivos.
        if dry_run:
            status(
                f"Simulación: {len(repair_blocks)} bloques y "
                f"{len(repair_files)} archivos requieren reparación"
            )
            status("verificacion exitosa")
            return 0

        with tempfile.TemporaryDirectory(prefix="verify-", dir=game_path) as temp_dir:
            temp_path = Path(temp_dir)

            total_download_bytes = sum(block.size for block in repair_blocks) + sum(file.size for file in repair_files)
            global_downloaded_bytes = 0

            # Fase 1: descargar y extraer bloques de assets
            if repair_blocks:
                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                    futures = []
                    for block in repair_blocks:
                        def report_block_progress(
                            file_downloaded_bytes: int,
                        ) -> None:
                            current = global_downloaded_bytes + file_downloaded_bytes
                            status(
                                f"descargando archivos: "
                                f"{current / (1024 * 1024):.2f} MB / "
                                f"{total_download_bytes / (1024 * 1024):.2f} MB"
                            )

                        zip_path = download_block(
                            base_url, block, temp_path, report_block_progress
                        )
                        futures.append(
                            executor.submit(
                                extract_block, zip_path, game_path, block, None
                            )
                        )
                        global_downloaded_bytes += block.size

                    # Esperar a que terminen todas las extracciones
                    for future in concurrent.futures.as_completed(futures):
                        future.result()

            # Fase 2: descargar archivos individuales (mods, versions, libraries)
            if repair_files:
                for manifest_file in repair_files:
                    def report_progress(file_downloaded_bytes: int) -> None:
                        current_bytes = global_downloaded_bytes + file_downloaded_bytes
                        status(
                            "descargando archivos: "
                            f"{current_bytes / (1024 * 1024):.2f} MB / "
                            f"{total_download_bytes / (1024 * 1024):.2f} MB"
                        )

                    downloaded_path = temp_path / manifest_file.relative_path
                    downloaded_path.parent.mkdir(parents=True, exist_ok=True)
                    file_url = f"{base_url}/client/{quote(manifest_file.url_path, safe='/')}"
                    download_file(
                        file_url,
                        downloaded_path,
                        manifest_file.sha256,
                        report_progress,
                    )

                    # os.replace evita dejar un archivo final a medio descargar.
                    destination = local_path(game_path, manifest_file)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    os.replace(downloaded_path, destination)
                    global_downloaded_bytes += manifest_file.size

        if parse_bool(properties.get("delete_unauthorized")):
            allowed_paths = {file.relative_path for file in manifest}
            for block in asset_blocks:
                for file in block.files:
                    allowed_paths.add(file.relative_path)
            remove_unauthorized_files(game_path, allowed_paths)

        status("verificacion exitosa")
        return 0
    except (OSError, VerificationError) as error:
        # La UI solo necesita diferenciar servidor caído de cualquier otro fallo.
        status(str(error) if str(error) == "servidor no responde" else "verificacion fallida")
        return 1


# EJECUCION MANUAL
def main() -> int:
    """Permite probar el verificador sin abrir la interfaz gráfica."""
    parser = argparse.ArgumentParser(description="Verifica la integridad del cliente")
    parser.add_argument("--dry-run", action="store_true", help="No descarga ni elimina archivos")
    args = parser.parse_args()
    return verify_client(dry_run=args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
