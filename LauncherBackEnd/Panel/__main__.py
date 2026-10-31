"""Arranca el panel de control y lo abre en una ventana propia::

    python -m Panel              # abre la ventana
    python -m Panel --sin-ventana

El panel escucha solo en 127.0.0.1 (puerto 59380, o ``KONNOR_PANEL_PORT``). Si ya hay un panel
abierto, solo se abre otra ventana hacia él.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import threading
import urllib.request
import webbrowser
from pathlib import Path

import uvicorn

from Panel.app import PANEL_PORT, app


URL = f"http://127.0.0.1:{PANEL_PORT}/"


def _panel_running() -> bool:
    try:
        with urllib.request.urlopen(URL + "api/ping", timeout=1) as response:
            return b"Konnor Launcher Backend" in response.read()
    except OSError:
        return False


def _find_browser() -> str | None:
    """Edge o Chrome, que pueden abrir el panel como ventana de aplicación (sin pestañas ni barra)."""
    candidates = [shutil.which("msedge"), shutil.which("chrome")]
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles"), os.environ.get("LOCALAPPDATA")):
        if base:
            candidates += [str(Path(base) / "Microsoft/Edge/Application/msedge.exe"),
                           str(Path(base) / "Google/Chrome/Application/chrome.exe")]
    return next((c for c in candidates if c and Path(c).is_file()), None)


def open_window() -> None:
    browser = _find_browser()
    if browser:
        subprocess.Popen([browser, f"--app={URL}", "--window-size=1480,940"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        webbrowser.open(URL)


def main() -> None:
    with_window = "--sin-ventana" not in sys.argv[1:]
    if _panel_running():
        print(f"El panel ya está abierto en {URL}")
        if with_window:
            open_window()
        return

    print(f"Panel de control en {URL}  (cerrar esta ventana detiene el panel y el servidor)")
    config = uvicorn.Config(app, host="127.0.0.1", port=PANEL_PORT, log_level="warning")
    panel = uvicorn.Server(config)
    if with_window:
        # La ventana se abre cuando el panel ya acepta conexiones.
        def wait_and_open() -> None:
            while not panel.started:
                if panel.should_exit:
                    return
                threading.Event().wait(0.1)
            open_window()
        threading.Thread(target=wait_and_open, daemon=True).start()
    panel.run()


if __name__ == "__main__":
    main()
