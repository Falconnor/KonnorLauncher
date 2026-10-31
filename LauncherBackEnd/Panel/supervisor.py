"""Proceso del servidor de un backend visto desde el panel: arranque, parada, registro y actividad.

Cada backend corre como proceso hijo (``python -m Services.serve --backend <id>``) con su salida
redirigida al panel. Así el panel puede mostrar su registro en vivo, detenerlo de forma ordenada
y tener varios backends en marcha a la vez.
"""

from __future__ import annotations

import os
import re
import signal
import socket
import subprocess
import sys
import threading
import time
from collections import deque
from dataclasses import dataclass, field

from Services.settings import BASE_DIR, PROFILE_ENV, load_settings, raw_config


# ── Registro ────────────────────────────────────────────────────────────────

# Línea de acceso de uvicorn: INFO:     192.168.1.20:51234 - "GET /client/mods/x.jar HTTP/1.1" 200 OK
_ACCESS = re.compile(r'(?P<ip>[\d.:a-fA-F\[\]]+?):\d+ - "(?P<method>[A-Z]+) (?P<path>\S+) HTTP/[\d.]+" (?P<status>\d{3})')


def _level(text: str) -> str:
    """Nivel aproximado de una línea, para colorearla y filtrarla en el panel."""
    upper = text.upper()
    if "ERROR" in upper or "TRACEBACK" in upper or "EXCEPTION" in upper or "CRITICAL" in upper:
        return "error"
    if "WARNING" in upper or "AVISO" in upper or "[WARN" in upper:
        return "warn"
    return "info"


@dataclass
class Activity:
    """Actividad de los launchers desde el último arranque, deducida de las líneas de acceso."""
    verifications: int = 0          # descargas del manifest: una por cada launcher que verifica
    downloads: int = 0              # archivos y bloques servidos (200 o 206)
    errors: int = 0                 # respuestas 4xx/5xx a /client o al manifest
    clients: set[str] = field(default_factory=set)
    last_request: float | None = None

    def record(self, ip: str, path: str, status: int) -> None:
        if path.startswith("/client_manifest.json"):
            if status == 200:
                self.verifications += 1
        elif path.startswith("/client/"):
            if status in (200, 206):
                self.downloads += 1
        else:
            return  # el sondeo del panel (GET /) y otras rutas no cuentan como actividad
        if status >= 400:
            self.errors += 1
        self.clients.add(ip)
        self.last_request = time.time()

    def as_dict(self) -> dict:
        return {
            "verifications": self.verifications,
            "downloads": self.downloads,
            "errors": self.errors,
            "clients": len(self.clients),
            "last_request": self.last_request,
        }


class LogBuffer:
    """Últimas líneas del servidor y del panel, numeradas para pedir solo las nuevas."""

    def __init__(self, maxlen: int = 1500):
        self._lines: deque[dict] = deque(maxlen=maxlen)
        self._seq = 0
        self._lock = threading.Lock()
        self.activity = Activity()

    def add(self, text: str, source: str = "server", level: str | None = None) -> None:
        text = text.rstrip()
        if not text:
            return
        match = _ACCESS.search(text) if source == "server" else None
        # El sondeo del propio panel (GET / desde este equipo cada 2 s) llenaría el registro.
        if match and match["path"] == "/" and match["method"] == "GET" and match["ip"] in ("127.0.0.1", "::1"):
            return
        with self._lock:
            self._seq += 1
            self._lines.append({
                "seq": self._seq,
                "time": time.time(),
                "text": text,
                "source": source,
                "level": level or _level(text),
            })
            if match:
                self.activity.record(match["ip"], match["path"], int(match["status"]))

    def since(self, seq: int) -> tuple[list[dict], int]:
        with self._lock:
            return [line for line in self._lines if line["seq"] > seq], self._seq

    def clear(self) -> None:
        with self._lock:
            self._lines.clear()

    def reset_activity(self) -> None:
        with self._lock:
            self.activity = Activity()


# ── Proceso del servidor ────────────────────────────────────────────────────

class PanelError(Exception):
    """Error que el panel muestra tal cual al usuario."""


def port_in_use(host: str, port: int) -> bool:
    """True si algo ya escucha en ese puerto (otro servidor, otra copia del backend...)."""
    probe_host = "127.0.0.1" if host in ("0.0.0.0", "::", "localhost") else host
    try:
        with socket.create_connection((probe_host, port), timeout=0.5):
            return True
    except OSError:
        pass
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.bind((host if host != "localhost" else "127.0.0.1", port))
        return False
    except OSError:
        return True


class ServerProcess:
    """El servidor de un backend como proceso hijo del panel."""

    STOP_TIMEOUT = 10  # segundos de margen para el cierre ordenado antes de forzarlo

    def __init__(self, backend_id: str, logs: LogBuffer):
        self.backend_id = backend_id
        self.logs = logs
        self._proc: subprocess.Popen | None = None
        self._lock = threading.Lock()
        self._stopping = False
        self.started_at: float | None = None
        self.exit_code: int | None = None
        self.crashed = False
        self.config_at_start: dict | None = None

    @property
    def alive(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    @property
    def pid(self) -> int | None:
        return self._proc.pid if self.alive else None

    @property
    def stopping(self) -> bool:
        return self._stopping

    def start(self) -> None:
        with self._lock:
            if self.alive:
                raise PanelError("El servidor ya está en marcha")
            settings = load_settings(self.backend_id, use_env=False)
            if port_in_use(settings.host, settings.port):
                raise PanelError(f"El puerto {settings.port} ya está en uso. ¿Hay otro servidor abierto?")

            # El panel manda: el servidor usa backends.json, sin variables KONNOR_* heredadas.
            env = {k: v for k, v in os.environ.items() if not k.upper().startswith("KONNOR_")}
            env.update(PYTHONUTF8="1", PYTHONUNBUFFERED="1", **{PROFILE_ENV: self.backend_id})
            # Grupo de procesos propio: permite enviarle CTRL_BREAK (cierre ordenado) sin afectar al panel.
            flags = subprocess.CREATE_NEW_PROCESS_GROUP if sys.platform == "win32" else 0

            self._proc = subprocess.Popen(
                [sys.executable, "-m", "Services.serve", "--backend", self.backend_id],
                cwd=BASE_DIR, env=env, creationflags=flags,
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, encoding="utf-8", errors="replace", bufsize=1,
            )
            self._stopping = False
            self.crashed = False
            self.exit_code = None
            self.started_at = time.time()
            self.config_at_start = raw_config(self.backend_id, use_env=False)
            self.logs.reset_activity()
            self.logs.add(f"Iniciando servidor en {settings.host}:{settings.port} (PID {self._proc.pid})",
                          source="panel", level="info")
            threading.Thread(target=self._pump, args=(self._proc,), daemon=True).start()

    def _pump(self, proc: subprocess.Popen) -> None:
        """Copia la salida del servidor al registro hasta que el proceso termina."""
        assert proc.stdout is not None
        for line in proc.stdout:
            self.logs.add(line)
        code = proc.wait()
        with self._lock:
            if proc is not self._proc:
                return
            self.exit_code = code
            if self._stopping:
                self.logs.add("Servidor detenido", source="panel", level="info")
            else:
                self.crashed = True
                self.logs.add(f"El servidor se cerró inesperadamente (código {code})",
                              source="panel", level="error")

    def stop(self) -> None:
        """Cierre ordenado (CTRL_BREAK / SIGTERM); si no responde a tiempo, se fuerza."""
        with self._lock:
            proc = self._proc
            if proc is None or proc.poll() is not None:
                return
            self._stopping = True
        self.logs.add("Deteniendo servidor...", source="panel", level="info")
        try:
            if sys.platform == "win32":
                os.kill(proc.pid, signal.CTRL_BREAK_EVENT)
            else:
                proc.terminate()
            proc.wait(self.STOP_TIMEOUT)
        except subprocess.TimeoutExpired:
            self.logs.add("El servidor no respondió; cerrándolo a la fuerza", source="panel", level="warn")
            proc.kill()
            proc.wait(5)
        except OSError:
            proc.kill()
            proc.wait(5)

    def restart(self) -> None:
        self.stop()
        self.start()
