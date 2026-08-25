from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles


app = FastAPI(title="Fkonnor Server")


# =========================
# CONFIGURACIÓN
# =========================

BASE_DIR = Path(__file__).resolve().parent
CLIENT_DIR = BASE_DIR / "Client"
MANIFEST = BASE_DIR / "client_manifest.json"


# =========================
# ESTADO DEL SERVIDOR
# =========================

@app.get("/")
def root():
    return {
        "status": "online",
        "server": "Fkonnor"
    }


# =========================
# MANIFEST
# =========================

@app.get("/client_manifest.json")
def get_manifest():

    if not MANIFEST.exists():
        return {
            "error": "Manifest no encontrado"
        }

    return FileResponse(
        MANIFEST,
        media_type="application/json"
    )


# =========================
# ARCHIVOS DEL CLIENTE
# =========================

app.mount(
    "/client",
    StaticFiles(directory=CLIENT_DIR),
    name="client"
)