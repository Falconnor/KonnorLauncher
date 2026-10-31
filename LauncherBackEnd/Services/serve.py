"""Arranca un servidor con su host y su puerto.

Lo usan ``iniciar_servidor.bat`` y el panel de control::

    python -m Services.serve                 # servidor de KONNOR_PROFILE o el seleccionado en el panel
    python -m Services.serve --servidor <id>
"""

import argparse
import os

import uvicorn

from Services.settings import BASE_DIR, PROFILE_ENV, load_settings


def main() -> None:
    parser = argparse.ArgumentParser(description="Servidor de Servidor de Konnor Launcher")
    parser.add_argument("--servidor", default=None, help="id del servidor que se sirve")
    args = parser.parse_args()

    settings = load_settings(args.servidor)
    # main.py vuelve a leer la configuración al importarse: con esto lee la de este mismo servidor.
    os.environ[PROFILE_ENV] = settings.profile_id
    print(f"Servidor: {settings.profile_name} ({settings.profile_id})", flush=True)
    # app_dir: main.py está en la carpeta LauncherBackEnd, se lance desde donde se lance.
    uvicorn.run("main:app", host=settings.host, port=settings.port, app_dir=str(BASE_DIR))


if __name__ == "__main__":
    main()
