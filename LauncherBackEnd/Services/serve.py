"""Arranca el servidor con el host y el puerto de la configuración.

Lo usan ``iniciar_servidor.bat`` y el panel de control, así ambos escuchan donde dice
``backend_config.json`` (o las variables ``KONNOR_HOST`` / ``KONNOR_PORT``)::

    python -m Services.serve
"""

import uvicorn

from Services.settings import BASE_DIR, load_settings


def main() -> None:
    settings = load_settings()
    # app_dir: main.py está en la carpeta del backend, se lance desde donde se lance.
    uvicorn.run("main:app", host=settings.host, port=settings.port, app_dir=str(BASE_DIR))


if __name__ == "__main__":
    main()
