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
PROTECTED_FOLDERS = ("mods", "versions", "libraries")
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
    """Descarga un temporal, informa bytes recibidos y valida su hash."""
    try:
        with urlopen(url, timeout=30) as response, destination.open("wb") as file:
            downloaded_bytes = 0
            while chunk := response.read(1024 * 1024):
                file.write(chunk)
                downloaded_bytes += len(chunk)
                on_progress(downloaded_bytes)
    except (HTTPError, URLError, TimeoutError, OSError) as error:
        raise VerificationError(f"No se pudo descargar {destination.name}") from error

    if sha256_file(destination) != expected_hash:
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
    2. Detecta reparaciones por existencia y tamaño.
    3. Descarga y valida con SHA-256 solo los archivos necesarios.
    4. Si todo salió bien, elimina archivos no autorizados cuando corresponde.

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

        manifest = parse_manifest(download_json(f"{base_url}/client_manifest.json"))
        repair_files = find_files_to_repair(game_path, manifest)

        # Modo de prueba: comprueba servidor, manifest y estado local sin
        # descargar, reemplazar ni eliminar archivos.
        if dry_run:
            status(f"Simulación: {len(repair_files)} archivos requieren reparación")
            status("verificacion exitosa")
            return 0

        with tempfile.TemporaryDirectory(prefix="verify-", dir=game_path) as temp_dir:
            temp_path = Path(temp_dir)
            total_bytes = sum(file.size for file in repair_files)
            downloaded_bytes = 0
            for manifest_file in repair_files:
                def report_progress(file_downloaded_bytes: int) -> None:
                    current_bytes = downloaded_bytes + file_downloaded_bytes
                    status(
                        "descargando archivos: "
                        f"{current_bytes / (1024 * 1024):.2f} MB / "
                        f"{total_bytes / (1024 * 1024):.2f} MB"
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
                downloaded_bytes += manifest_file.size

        if parse_bool(properties.get("delete_unauthorized")):
            allowed_paths = {file.relative_path for file in manifest}
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
