"""Arranca el servidor de un backend con su host y su puerto.

Lo usan ``iniciar_servidor.bat`` y el panel de control::

    python -m Services.serve                 # backend de KONNOR_PROFILE o el seleccionado en el panel
    python -m Services.serve --backend <id>
"""

import argparse
import os

import uvicorn

from Services.settings import BASE_DIR, PROFILE_ENV, load_settings


def main() -> None:
    parser = argparse.ArgumentParser(description="Servidor de Konnor Launcher Backend")
    parser.add_argument("--backend", default=None, help="id del backend que se sirve")
    args = parser.parse_args()

    settings = load_settings(args.backend)
    # main.py vuelve a leer la configuración al importarse: con esto lee la de este mismo backend.
    os.environ[PROFILE_ENV] = settings.backend_id
    print(f"Backend: {settings.backend_name} ({settings.backend_id})", flush=True)
    # app_dir: main.py está en la carpeta del backend, se lance desde donde se lance.
    uvicorn.run("main:app", host=settings.host, port=settings.port, app_dir=str(BASE_DIR))


if __name__ == "__main__":
    main()
