import json
import os
import subprocess


LAUNCH_PATH = os.path.dirname(__file__)


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

    path = os.path.join(
        LAUNCH_PATH,
        "launcher.properties"
    )
    
    

    properties = {}

    with open(path, "r", encoding="utf-8") as file:

        for line in file:

            line = line.strip()

            if not line or line.startswith("#"):
                continue


            key, value = line.split("=", 1)

            properties[key.strip()] = value.strip()


    return properties



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


    for line in process.stdout:

        callback(line.strip())


    process.wait()

    return process.returncode


def launch_game():

    subprocess.Popen(
        [
            "powershell",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            os.path.join(
                LAUNCH_PATH,
                "launcher.ps1"
            )
        ]
    )
    
    