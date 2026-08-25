import json
import os
import subprocess


GAME_DIR = r"C:\Users\Luis\Desktop\Fkonnor Launcher\Fk Launcher"

VERSION = "fabric-loader-0.19.3-1.21.1"

JSON_PATH = os.path.join(
    GAME_DIR,
    "versions",
    VERSION,
    VERSION + ".json"
)


with open(JSON_PATH, "r") as f:
    data = json.load(f)


libraries = []


for lib in data["libraries"]:

    if "name" not in lib:
        continue

    parts = lib["name"].split(":")

    if len(parts) < 3:
        continue

    group = parts[0].replace(".", os.sep)
    artifact = parts[1]
    version = parts[2]

    jar = os.path.join(
        GAME_DIR,
        "libraries",
        group,
        artifact,
        version,
        f"{artifact}-{version}.jar"
    )

    if os.path.exists(jar):
        libraries.append(jar)


# Añadir Minecraft + Fabric

libraries.append(
    os.path.join(
        GAME_DIR,
        "versions",
        "1.21.1",
        "1.21.1.jar"
    )
)

libraries.append(
    os.path.join(
        GAME_DIR,
        "versions",
        VERSION,
        VERSION+".jar"
    )
)


classpath = os.pathsep.join(libraries)


java = os.path.join(
    GAME_DIR,
    "runtime",
    "java-runtime-delta",
    "windows",
    "java-runtime-delta",
    "bin",
    "java.exe"
)


natives = os.path.join(
    GAME_DIR,
    "versions",
    VERSION,
    "natives"
)


args = [
    java,

    f"-Djava.library.path={natives}",

    "-cp",
    classpath,

    "net.fabricmc.loader.impl.launch.knot.KnotClient",

    "--username",
    "Luis",

    "--version",
    VERSION,

    "--gameDir",
    GAME_DIR,

    "--assetsDir",
    os.path.join(GAME_DIR,"assets"),

    "--assetIndex",
    "17",

    "--uuid",
    "00000000-0000-0000-0000-000000000000",

    "--accessToken",
    "0"
]


subprocess.run(args)