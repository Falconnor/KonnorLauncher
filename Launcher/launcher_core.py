import json
import os
import subprocess

import verify_client as client_verifier


LAUNCH_PATH = os.path.dirname(__file__)
PROPERTIES_PATH = os.path.join(LAUNCH_PATH, "launcher.properties")


def _candidate_game_paths():
    properties = load_properties()
    configured_path = properties.get("game.path", "")

    candidates = []

    if configured_path:
        candidates.append(configured_path)

    candidates.extend([
        os.path.join(LAUNCH_PATH, "..", "Minecraft"),
        os.path.join(LAUNCH_PATH, "..", "Fk Launcher"),
        os.path.join(LAUNCH_PATH, "..", "Fkonnor Launcher"),
        os.path.join(LAUNCH_PATH, "..", "..", "Minecraft"),
    ])

    seen = set()
    unique_candidates = []

    for candidate in candidates:
        normalized = os.path.normpath(candidate)
        if normalized not in seen:
            seen.add(normalized)
            unique_candidates.append(normalized)

    return unique_candidates


def load_properties():
    properties = {}

    with open(PROPERTIES_PATH, "r", encoding="utf-8") as file:
        for line in file:
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if "=" not in line:
                continue

            key, value = line.split("=", 1)
            properties[key.strip()] = value.strip()

    return properties


def save_property(key: str, value: str):
    if not os.path.isfile(PROPERTIES_PATH):
        raise FileNotFoundError(f"No se encontró {PROPERTIES_PATH}")

    updated = False
    lines = []

    with open(PROPERTIES_PATH, "r", encoding="utf-8") as file:
        for raw in file:
            stripped = raw.strip()
            if not stripped or stripped.startswith("#") or "=" not in raw:
                lines.append(raw)
                continue

            current_key = raw.split("=", 1)[0].strip()
            if current_key == key:
                lines.append(f"{key}={value}\n")
                updated = True
            else:
                lines.append(raw)

    if not updated:
        if lines and not lines[-1].endswith("\n"):
            lines[-1] = lines[-1] + "\n"
        lines.append(f"{key}={value}\n")

    with open(PROPERTIES_PATH, "w", encoding="utf-8") as file:
        file.writelines(lines)

    global PROPERTIES, GAME_PATH
    PROPERTIES = load_properties()
    GAME_PATH = get_game_path()


def get_selected_version():
    return load_properties().get("minecraft.version", "").strip()


def get_versions_path():
    properties = load_properties()
    versions_path = properties.get("versions.path", "versions").strip()
    if os.path.isabs(versions_path):
        return versions_path
    return os.path.join(get_game_path(), versions_path)


def get_installed_versions():
    versions_dir = get_versions_path()
    if not os.path.isdir(versions_dir):
        return []

    version_names = []
    for entry in sorted(os.listdir(versions_dir)):
        entry_path = os.path.join(versions_dir, entry)
        if not os.path.isdir(entry_path):
            continue

        json_file = os.path.join(entry_path, f"{entry}.json")
        if os.path.isfile(json_file):
            version_names.append(entry)
            continue

        for candidate in os.listdir(entry_path):
            if candidate.endswith(".json"):
                version_names.append(entry)
                break

    return version_names

_mojang_versions_cache = None

def fetch_mojang_versions_sync():
    global _mojang_versions_cache
    try:
        import minecraft_launcher_lib
        raw = minecraft_launcher_lib.utils.get_version_list()
        _mojang_versions_cache = [v['id'] for v in raw if v.get('type') == 'release']
    except Exception:
        _mojang_versions_cache = []

def get_all_versions():
    installed = get_installed_versions()
    global _mojang_versions_cache
            
    all_v = list(installed)
    if _mojang_versions_cache:
        for v in _mojang_versions_cache:
            if v not in all_v:
                all_v.append(v)
    return all_v

def set_selected_version(version: str):
    if not version:
        raise ValueError("La versión no puede estar vacía")
    save_property("minecraft.version", version)
    global PROPERTIES
    PROPERTIES = load_properties()

PROPERTIES = load_properties()



def get_game_path():

    game_path = PROPERTIES.get("game.path", "")

    if game_path:
        if os.path.isabs(game_path):
            candidate = os.path.normpath(game_path)
            if os.path.isdir(candidate):
                return candidate
        else:
            candidate = os.path.abspath(os.path.join(LAUNCH_PATH, game_path))
            if os.path.isdir(candidate):
                return candidate

    for candidate in _candidate_game_paths():
        if os.path.isdir(candidate):
            return os.path.abspath(candidate)

    return os.path.abspath(os.path.join(LAUNCH_PATH, "..", "Minecraft"))



GAME_PATH = get_game_path()



def get_client_version() -> str:
    """Obtiene la versión del cliente desde el manifest del servidor.

    Si el servidor responde, actualiza la caché local en launcher.properties.
    Si el servidor no responde, devuelve la última versión conocida (caché).
    """
    try:
        from urllib.request import urlopen
        import json as _json

        properties = load_properties()
        base_url = properties.get("server.url", "").rstrip("/")
        if not base_url:
            return load_properties().get("cached.client.version", "")

        with urlopen(f"{base_url}/client_manifest.json", timeout=5) as response:
            manifest = _json.loads(response.read().decode("utf-8"))

        version = manifest.get("client_version", "")
        if version:
            # Guardar en caché local para cuando el servidor no esté disponible
            save_property("cached.client.version", version)
        return version
    except Exception:
        # Servidor no disponible → devolver última versión conocida
        return load_properties().get("cached.client.version", "")


def install_vanilla_version(version: str, callback=None):
    import minecraft_launcher_lib
    game_path = get_game_path()
    
    # Variables de estado para el callback global
    state = {
        "max": 1, 
        "status": "Preparando descarga",
        "phase": "init", # init, libs, assets
    }
    
    def get_global_percentage(val, max_val):
        if max_val == 0:
            max_val = 1
        local_pct = val / max_val
        
        # Pesos arbitrarios de las fases para simular un progreso global
        if state["phase"] == "init":
            # 0% a 5%
            return int(0 + (local_pct * 5))
        elif state["phase"] == "libs":
            # 5% a 30%
            return int(5 + (local_pct * 25))
        elif state["phase"] == "assets":
            # 30% a 100%
            return int(30 + (local_pct * 70))
        return int(local_pct * 100)

    def set_max(max_val):
        state["max"] = max_val or 1
        
    def set_status(text):
        lower_text = text.lower()
        if "librar" in lower_text or "librer" in lower_text or "library" in lower_text:
            state["phase"] = "libs"
        elif "asset" in lower_text or "recurso" in lower_text:
            state["phase"] = "assets"
            
        state["status"] = text
        if callback: 
            global_pct = get_global_percentage(0, state['max'])
            # Mostramos el contador de la fase actual, ej: "Descargando... (0/150)"
            callback(f"MC_PROGRESS|{text} (0/{state['max']})|{global_pct}|100")
        
    def set_progress(val):
        if callback: 
            global_pct = get_global_percentage(val, state['max'])
            callback(f"MC_PROGRESS|{state['status']} ({val}/{state['max']})|{global_pct}|100")
    
    callbacks = {
        "setStatus": set_status,
        "setProgress": set_progress,
        "setMax": set_max
    }
    minecraft_launcher_lib.install.install_minecraft_version(version, game_path, callback=callbacks)

def verify_client(callback):
    return client_verifier.verify_client(callback)


def launch_game():
    import minecraft_launcher_lib
    
    properties = load_properties()
    version = properties.get("minecraft.version", "").strip()
    if not version:
        raise RuntimeError("No hay ninguna versión seleccionada en launcher.properties")
        
    game_path = get_game_path()
    
    java_path = properties.get("java.path", "java")
    if not os.path.isabs(java_path):
        java_path = os.path.join(game_path, java_path)

    memory = properties.get("java.memory", "2G")
    # Convert '2G' to '2048' roughly, or just pass it to JVM args directly.
    # minecraft_launcher_lib maneja 'jvmArguments'.
    
    options = {
        "username": properties.get("player.username", "Player"),
        "uuid": properties.get("player.uuid", "00000000-0000-0000-0000-000000000000"),
        "token": properties.get("player.accessToken", "0"),
        "executablePath": java_path,
        "jvmArguments": [f"-Xmx{memory}"]
    }
    
    cmd = minecraft_launcher_lib.command.get_minecraft_command(version, game_path, options)
    print("EJECUTANDO MINECRAFT CON minecraft-launcher-lib...")
    print("Comando:", " ".join(cmd))
    
    process = subprocess.Popen(cmd, cwd=game_path)
    return process.wait()
