"""Arranca el panel de control y lo abre en una ventana propia::

    python -m Panel              # abre la ventana
    python -m Panel --sin-ventana

El panel escucha solo en 127.0.0.1 (puerto 59380, o ``KONNOR_PANEL_PORT``). Si ya hay un panel
abierto, solo se abre otra ventana hacia él.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
import urllib.request
import webbrowser
from pathlib import Path

import uvicorn

from Panel.app import API_VERSION, PANEL_PORT, app


URL = f"http://127.0.0.1:{PANEL_PORT}/"


def _running_panel() -> dict | None:
    """Respuesta de /api/ping del panel que ya está abierto, o None si no hay ninguno."""
    try:
        with urllib.request.urlopen(URL + "api/ping", timeout=1) as response:
            data = json.loads(response.read().decode("utf-8"))
    except (OSError, ValueError):
        return None
    return data if data.get("panel") == "Servidor de Konnor Launcher" else None


def _find_browser() -> str | None:
    """Edge o Chrome, que pueden abrir el panel como ventana de aplicación (sin pestañas ni barra)."""
    candidates = [shutil.which("msedge"), shutil.which("chrome")]
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles"), os.environ.get("LOCALAPPDATA")):
        if base:
            candidates += [str(Path(base) / "Microsoft/Edge/Application/msedge.exe"),
                           str(Path(base) / "Google/Chrome/Application/chrome.exe")]
    return next((c for c in candidates if c and Path(c).is_file()), None)


# Perfil propio del navegador para la ventana del panel. Sin él, si Edge ya está abierto, la
# ventana se la queda el proceso existente, que ignora el tamaño pedido.
BROWSER_PROFILE = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "KonnorLauncher" / "panel-navegador"


def _window_bounds() -> tuple[int, int, int, int]:
    """Tamaño 4:3 que cabe en el área de trabajo (sin la barra de tareas), centrado.

    Ocupa el 85 % del alto disponible, o menos si en ese alto la ventana sería más ancha que la
    pantalla. Las medidas son en píxeles lógicos, las mismas que usa el navegador.
    """
    left, top, width, height = 0, 0, 1920, 1040
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes
        rect = wintypes.RECT()
        if ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):  # SPI_GETWORKAREA
            left, top, width, height = rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top
    win_h = int(height * 0.85)
    win_w = win_h * 4 // 3
    if win_w > width * 0.95:
        win_w = int(width * 0.95)
        win_h = win_w * 3 // 4
    return left + (width - win_w) // 2, top + (height - win_h) // 2, win_w, win_h


def open_window() -> None:
    browser = _find_browser()
    if not browser:
        webbrowser.open(URL)
        return
    args = [browser, f"--app={URL}", f"--user-data-dir={BROWSER_PROFILE}",
            "--no-first-run", "--no-default-browser-check"]
    # Tamaño 4:3 en cada apertura: Edge no recuerda el tamaño de las ventanas --app y, sin
    # indicarlo, las abre estrechas y casi tan altas como la pantalla.
    x, y, w, h = _window_bounds()
    args += [f"--window-size={w},{h}", f"--window-position={x},{y}"]
    subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main() -> None:
    with_window = "--sin-ventana" not in sys.argv[1:]
    running = _running_panel()
    # Un panel abierto con código anterior seguiría ejecutándolo: hay que cerrarlo primero.
    if running is not None and (int(running.get("api", 0)) < API_VERSION or running.get("outdated")):
        print(f"Hay un panel abierto en {URL} con una versión anterior del código.")
        print("Cierra su consola (detendrá sus servidores) y vuelve a abrir el panel.")
        sys.exit(1)
    if running is not None:
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
