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


def verify_client(callback):
    return client_verifier.verify_client(callback)


def launch_game():
    """Lanza Minecraft construyendo el comando de Java en Python puro.

    Reemplaza launcher.ps1. Sigue el mismo orden de pasos:
    1. Rutas y configuración
    2. Cargar JSON de versión e inheritsFrom
    3. Detectar tipo de cliente (fabric / forge / vanilla)
    4. Obtener assetIndex
    5. Resolver directorios de natives
    6. Construir classpath
    7. Ejecutar Java
    """

    # ------------------------------------------------------------------ #
    # 1. CONFIGURACIÓN DE RUTAS Y CLIENTE                                 #
    # ------------------------------------------------------------------ #

    # Leer todas las propiedades del launcher.
    properties = load_properties()

    # Ruta al directorio del juego (Minecraft/).
    game = get_game_path()

    # Ruta al ejecutable de Java: puede ser absoluta o relativa al juego.
    java_path = properties.get("java.path", "java")
    if os.path.isabs(java_path):
        java = java_path
    else:
        java = os.path.join(game, java_path)

    # ------------------------------------------------------------------ #
    # 2. CARGAR JSON DE LA VERSIÓN SELECCIONADA                          #
    # ------------------------------------------------------------------ #

    # Leer la versión seleccionada en launcher.properties.
    version = properties.get("minecraft.version", "").strip()
    if not version:
        raise RuntimeError("No hay ninguna versión seleccionada en launcher.properties")

    version_json_path = os.path.join(game, "versions", version, f"{version}.json")
    with open(version_json_path, "r", encoding="utf-8") as f:
        version_json = json.load(f)

    # Si la versión hereda de otra (Fabric/Forge), cargar también el JSON padre.
    parent_json = None
    if version_json.get("inheritsFrom"):
        parent_id = version_json["inheritsFrom"]
        parent_json_path = os.path.join(game, "versions", parent_id, f"{parent_id}.json")
        with open(parent_json_path, "r", encoding="utf-8") as f:
            parent_json = json.load(f)

    # ------------------------------------------------------------------ #
    # 3. DETECTAR TIPO DE CLIENTE                                         #
    # ------------------------------------------------------------------ #

    # Fabric: la versión empieza con "fabric-loader-".
    # Forge: la versión empieza con "forge-".
    # Vanilla: cualquier otro caso.
    if version.startswith("fabric-loader-"):
        client_type = "fabric"
        # Extrae la versión de Minecraft del nombre: fabric-loader-X.Y.Z-1.21.1 → 1.21.1
        minecraft_version = "-".join(version.split("-")[3:])
    elif version.startswith("forge-"):
        client_type = "forge"
        minecraft_version = version_json.get("inheritsFrom", version)
    else:
        client_type = "vanilla"
        minecraft_version = version

    # ------------------------------------------------------------------ #
    # 4. OBTENER ASSET INDEX                                              #
    # ------------------------------------------------------------------ #

    # El assetIndex puede estar en el JSON de la versión o en su padre.
    asset_index_data = version_json.get("assetIndex") or (
        parent_json.get("assetIndex") if parent_json else None
    )
    if not asset_index_data:
        raise RuntimeError("No se encontró assetIndex en ningún JSON de versión")
    asset_index = asset_index_data["id"]

    # ------------------------------------------------------------------ #
    # 5. RESOLVER DIRECTORIOS DE NATIVES                                  #
    # ------------------------------------------------------------------ #

    # Los natives son DLLs que Java necesita en su java.library.path.
    # Pueden estar directamente en natives/ o en subdirectorios dentro de él.
    natives_root = os.path.join(game, "versions", version, "natives")
    if not os.path.isdir(natives_root):
        raise RuntimeError(f"No se encontró el directorio de natives: {natives_root}")

    native_directories = set()

    # Buscar DLLs directamente dentro de natives/.
    for entry in os.listdir(natives_root):
        if entry.lower().endswith(".dll"):
            native_directories.add(natives_root)
            break

    # Buscar DLLs dentro de subdirectorios de natives/.
    for subdir in os.listdir(natives_root):
        subdir_path = os.path.join(natives_root, subdir)
        if not os.path.isdir(subdir_path):
            continue
        for entry in os.listdir(subdir_path):
            if entry.lower().endswith(".dll"):
                native_directories.add(subdir_path)
                break

    if not native_directories:
        raise RuntimeError(f"No se encontraron DLLs nativas en: {natives_root}")

    # Unir todas las rutas de natives con ";" como separador (java.library.path).
    natives = ";".join(sorted(native_directories))

    # ------------------------------------------------------------------ #
    # 6. CONSTRUIR CLASSPATH                                              #
    # ------------------------------------------------------------------ #

    # Combinar librerías del JSON de la versión y del JSON padre.
    all_libraries = list(version_json.get("libraries", []))
    if parent_json:
        all_libraries += parent_json.get("libraries", [])

    classpath_entries = []

    for lib in all_libraries:

        # Filtrar librerías con reglas de OS que excluyan Windows.
        rules = lib.get("rules", [])
        if rules:
            allowed = True
            for rule in rules:
                os_rule = rule.get("os", {})
                if os_rule and os_rule.get("name") != "windows":
                    allowed = False
            if not allowed:
                continue

        lib_name = lib.get("name", "")

        # Excluir entradas de natives (no van en classpath).
        if ":natives-" in lib_name:
            continue

        # Método principal: usar la ruta indicada por downloads.artifact.path.
        downloads = lib.get("downloads", {})
        artifact = downloads.get("artifact", {})
        artifact_path = artifact.get("path", "")
        if artifact_path:
            full_path = os.path.join(game, "libraries", artifact_path.replace("/", os.sep))
            if os.path.isfile(full_path):
                classpath_entries.append(full_path)
            continue

        # Fallback: construir la ruta a partir del nombre "grupo:artefacto:version".
        parts = lib_name.split(":")
        if len(parts) >= 3:
            group_path = parts[0].replace(".", os.sep)
            artifact_id = parts[1]
            version_lib = parts[2]
            jar_name = f"{artifact_id}-{version_lib}.jar"
            full_path = os.path.join(
                game, "libraries", group_path, artifact_id, version_lib, jar_name
            )
            if os.path.isfile(full_path):
                classpath_entries.append(full_path)

    # ================================================================== #
    # DEBUG: LIBRERIAS LWJGL INCLUIDAS EN EL CLASSPATH                   #
    # Este bloque puede eliminarse cuando ya no sea necesario depurar     #
    # la resolución de dependencias gráficas de LWJGL.                   #
    # ================================================================== #
    print()
    print("===== LWJGL =====")
    for entry in classpath_entries:
        if os.sep + "org" + os.sep + "lwjgl" + os.sep in entry:
            print(entry)
    print("=================")
    print()

    # Agregar el JAR principal de la versión (ej: fabric-loader-0.19.3-1.21.1.jar).
    main_jar = os.path.join(game, "versions", version, f"{version}.jar")
    if os.path.isfile(main_jar):
        classpath_entries.append(main_jar)

    # Agregar el JAR vanilla base si la versión es un mod-loader sobre vanilla.
    base_jar = os.path.join(game, "versions", minecraft_version, f"{minecraft_version}.jar")
    if minecraft_version != version and os.path.isfile(base_jar):
        classpath_entries.append(base_jar)

    classpath = ";".join(classpath_entries)

    # ================================================================== #
    # DEBUG: INFORMACIÓN GENERAL DEL LANZAMIENTO                         #
    # Muestra en consola los parámetros clave antes de llamar a Java.    #
    # Este bloque puede eliminarse cuando el launcher esté estable.      #
    # ================================================================== #
    print()
    print("===== DEBUG =====")
    print("VERSION:", version)
    print("TYPE:", client_type)
    print("MINECRAFT VERSION:", minecraft_version)
    print("MAIN CLASS:", version_json.get("mainClass"))
    print("ASSET INDEX:", asset_index)
    print()
    print("JAVA:")
    print(java)
    print()
    print("CLASSPATH LENGTH:", len(classpath))
    print("GAME PATH:", game)
    print("EXISTE MODS:", os.path.isdir(os.path.join(game, "mods")))
    print("EXISTE LIBRARIES:", os.path.isdir(os.path.join(game, "libraries")))
    print("EXISTE VERSIONS:", os.path.isdir(os.path.join(game, "versions")))
    print("=================")
    print()

    # ------------------------------------------------------------------ #
    # 7. EJECUTAR MINECRAFT                                               #
    # ------------------------------------------------------------------ #

    memory = properties.get("java.memory", "2G")
    username = properties.get("player.username", "Player")
    uuid = properties.get("player.uuid", "00000000-0000-0000-0000-000000000000")
    access_token = properties.get("player.accessToken", "0")
    main_class = version_json.get("mainClass", "")

    cmd = [
        java,
        f"-Xmx{memory}",
        f"-Djava.library.path={natives}",
        "-cp", classpath,
        main_class,
        "--username", username,
        "--version", version,
        "--gameDir", game,
        "--assetsDir", os.path.join(game, "assets"),
        "--assetIndex", asset_index,
        "--uuid", uuid,
        "--accessToken", access_token,
    ]

    process = subprocess.Popen(cmd, cwd=game)
    return process.wait()
