"""Versión de Minecraft y cargador de mods del cliente, leídos de su carpeta ``versions/``.

Si se reconocen, el manifest los publica en la sección ``minecraft`` y el launcher instala el juego
por su cuenta (Mojang, Fabric, Quilt, Forge o NeoForge), así que ``versions/``, ``libraries/`` y
``assets/`` ya no se envían. Si no se reconocen (un cargador desconocido, una versión retocada...),
el manifest no lleva la sección y se publican esas carpetas como siempre.

Se elige la misma versión que lanzaría el launcher:

1. la de un cargador: su JSON hereda de otra (``inheritsFrom``) o arranca con una clase que no es la
   de vanilla;
2. si no hay ninguna, la vanilla;
3. entre varias del mismo tipo, la más reciente (``releaseTime``).
"""

from __future__ import annotations

import json
import re
from datetime import datetime, timezone
from pathlib import Path

VANILLA_MAIN_CLASSES = {"net.minecraft.client.main.Main", "net.minecraft.client.Minecraft"}

_MC = r"\d+\.\d+(?:\.\d+)?"
# Mismos ids que interpreta el launcher (GameInstance.ParseVersionId).
_LOADER_IDS = (
    ("fabric", re.compile(rf"^fabric-loader-(?P<lv>.+)-(?P<mc>{_MC})$")),
    ("quilt", re.compile(rf"^quilt-loader-(?P<lv>.+)-(?P<mc>{_MC})$")),
    ("forge", re.compile(rf"^(?P<mc>{_MC})-forge-?(?:{_MC}-)?(?P<lv>.+)$")),
    ("neoforge", re.compile(r"^neoforge-(?P<lv>\d+\.\d+\..+)$")),
)
_VANILLA_ID = re.compile(rf"^{_MC}(?:-(?:pre|rc)\d+)?$|^\d{{2}}w\d{{2}}[a-z]$")


def _release_time(data: dict, json_file: Path) -> datetime:
    try:
        return datetime.fromisoformat(str(data["releaseTime"]))
    except (KeyError, ValueError):
        return datetime.fromtimestamp(json_file.stat().st_mtime, timezone.utc)


def _candidates(versions_dir: Path) -> list[tuple[str, dict, bool, datetime]]:
    found = []
    if not versions_dir.is_dir():
        return found
    for folder in versions_dir.iterdir():
        json_file = folder / f"{folder.name}.json"
        if not folder.is_dir() or not json_file.is_file():
            continue
        try:
            data = json.loads(json_file.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue  # una versión dañada no se elige
        if not isinstance(data, dict):
            continue
        is_loader = isinstance(data.get("inheritsFrom"), str) or (
            isinstance(data.get("mainClass"), str) and data["mainClass"] not in VANILLA_MAIN_CLASSES)
        when = _release_time(data, json_file)
        if when.tzinfo is None:
            when = when.replace(tzinfo=timezone.utc)
        found.append((folder.name, data, is_loader, when))
    return found


def detect_game(client_dir: Path) -> dict | None:
    """``{"version", "loader", "loader_version"}`` del cliente, o None si no se reconoce."""
    candidates = _candidates(client_dir / "versions")
    if not candidates:
        return None
    version_id, data, is_loader, _ = max(candidates, key=lambda c: (c[2], c[3]))

    if not is_loader:
        # Solo ids oficiales: uno renombrado no existe en Mojang y el launcher no podría bajarlo.
        return {"version": version_id, "loader": "vanilla"} if _VANILLA_ID.match(version_id) else None

    for loader, pattern in _LOADER_IDS:
        if match := pattern.match(version_id):
            parent = data.get("inheritsFrom")
            minecraft = parent if isinstance(parent, str) else match.groupdict().get("mc")
            if not minecraft or not _VANILLA_ID.match(minecraft):
                return None
            return {"version": minecraft, "loader": loader, "loader_version": match["lv"]}
    return None


def describe(game: dict | None) -> str:
    """Texto corto: "Minecraft 1.21.1 · Fabric 0.19.3"."""
    if not game:
        return "no reconocida"
    names = {"fabric": "Fabric", "quilt": "Quilt", "forge": "Forge", "neoforge": "NeoForge"}
    text = f"Minecraft {game['version']}"
    if game.get("loader") in names:
        text += f" · {names[game['loader']]} {game.get('loader_version', '')}".rstrip()
    return text
