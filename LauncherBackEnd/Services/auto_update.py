"""Actualización automática del manifest mientras el servidor está en marcha.

- Al arrancar: genera el manifest en segundo plano. El servidor responde desde el primer
  momento; mientras tanto se sigue sirviendo el manifest anterior (si existe).
- Después: vigila la carpeta del cliente con ``watchfiles`` (notificaciones del sistema, sin
  sondeo) y regenera cuando hay cambios y la carpeta lleva un rato sin modificarse, para no
  regenerar a mitad de una copia grande.

Las generaciones nunca se solapan: se ejecutan de una en una en un hilo aparte.
"""

from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import asdict
from pathlib import Path

from watchfiles import Change, DefaultFilter, awatch

from Services.generate_manifest import GenerationResult, generate_manifest
from Services.settings import Settings


logger = logging.getLogger("konnor.backend")


class _ClientFilter(DefaultFilter):
    """Ignora los bloques generados y los temporales (si no, cada generación dispararía otra)."""

    def __init__(self, packages_dir: Path):
        super().__init__()
        self._packages = str(packages_dir.resolve()).lower()

    def __call__(self, change: Change, path: str) -> bool:
        lowered = path.lower()
        if lowered.startswith(self._packages) or lowered.endswith((".tmp", ".part")):
            return False
        return super().__call__(change, path)


class AutoUpdater:
    """Genera el manifest al arrancar y cada vez que cambia la carpeta del cliente."""

    def __init__(self, settings: Settings):
        self.settings = settings
        self.state = "idle"            # idle | generating | error
        self.last_result: GenerationResult | None = None
        self.last_run: float | None = None
        self._lock = asyncio.Lock()
        self._stop = asyncio.Event()
        self._tasks: list[asyncio.Task] = []

    async def start(self) -> None:
        """Lanza la generación inicial y, si está activado, el vigilante. No bloquea el arranque."""
        if self.settings.auto_update:
            self._tasks.append(asyncio.create_task(self.regenerate("arranque del servidor")))
        if self.settings.watch:
            self._tasks.append(asyncio.create_task(self._watch()))

    async def stop(self) -> None:
        self._stop.set()
        for task in self._tasks:
            task.cancel()
        await asyncio.gather(*self._tasks, return_exceptions=True)

    async def regenerate(self, reason: str, force_blocks: bool = False) -> GenerationResult:
        """Genera el manifest en un hilo, de uno en uno. ``force_blocks`` rehace todos los ZIP."""
        async with self._lock:
            self.state = "generating"
            logger.info("Actualizando manifest (%s)...", reason)
            try:
                result = await asyncio.to_thread(generate_manifest, self.settings, force_blocks,
                                                 log=logger.info)
            except Exception as error:  # nunca debe tumbar el servidor
                logger.exception("Error generando el manifest")
                result = GenerationResult(ok=False, error=str(error))
            self.last_result = result
            self.last_run = time.time()
            self.state = "idle" if result.ok else "error"
            if not result.ok:
                logger.error("No se pudo actualizar el manifest: %s", result.error)
            return result

    async def _watch(self) -> None:
        """Vigila la carpeta del cliente y regenera tras un periodo sin cambios."""
        client_dir = self.settings.client_dir
        if not client_dir.is_dir():
            logger.warning("No se vigila %s: la carpeta no existe", client_dir)
            return
        logger.info("Vigilando cambios en %s", client_dir)
        async for changes in awatch(
            client_dir,
            watch_filter=_ClientFilter(self.settings.packages_dir),
            step=self.settings.watch_quiet_ms,  # silencio necesario antes de avisar
            debounce=60_000,                   # como mucho, un aviso por minuto en copias largas
            stop_event=self._stop,
        ):
            await self.regenerate(f"{len(changes)} cambios en la carpeta del cliente")

    def status(self) -> dict:
        """Estado para el endpoint ``/``."""
        return {
            "state": self.state,
            "last_run": self.last_run,
            "last_result": asdict(self.last_result) if self.last_result else None,
            "watching": self.settings.watch,
        }
