import json
import os
import subprocess


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



def load_config():

    properties = load_properties()

    config_file = properties.get(
        "config.path",
        "config.json"
    )

    path = os.path.join(
        GAME_PATH,
        config_file
    )

    with open(
        path,
        "r",
        encoding="utf-8"
    ) as file:

        return json.load(file)



def verify_client(callback):

    process = subprocess.Popen(
        [
            "powershell",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            os.path.join(
                LAUNCH_PATH,
                "verify_client.ps1"
            )
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True
    )

    assert process.stdout is not None
    for line in process.stdout:
        callback(line.strip())

    process.wait()
    return process.returncode


def launch_game():
    process = subprocess.Popen(
        [
            "powershell",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            os.path.join(
                LAUNCH_PATH,
                "launcher.ps1"
            )
        ],
        cwd=LAUNCH_PATH,
    )
    return process.wait()
    
    