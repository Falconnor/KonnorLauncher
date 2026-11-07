import os
import sys
import json
import shutil
import subprocess
import threading
import re
import requests
from pathlib import Path

# Directorio base de los servidores
SERVERS_DIR = os.path.join(os.getenv('APPDATA', ''), 'KonnorLauncher', 'server')

# Tipos de servidor
VANILLA = 'VANILLA'
PAPER = 'PAPER'
FABRIC = 'FABRIC'
FORGE = 'FORGE'

def _find_best_java() -> str:
    """Busca y devuelve la ruta al ejecutable de Java más reciente encontrado en el sistema."""
    import subprocess, re, shutil
    search_paths = [
        os.environ.get("PROGRAMFILES", "C:\\Program Files"),
        os.environ.get("PROGRAMFILES(X86)", "C:\\Program Files (x86)")
    ]
    java_exes = []
    
    sys_java = shutil.which("java")
    if sys_java:
        java_exes.append(sys_java)
        
    common_vendors = ["Java", "Eclipse Adoptium", "AdoptOpenJDK", "Microsoft", "Amazon Corretto", "BellSoft", "Zulu"]
    
    for base in search_paths:
        if not base or not os.path.isdir(base): continue
        for v in common_vendors:
            vp = os.path.join(base, v)
            if os.path.isdir(vp):
                for d in os.listdir(vp):
                    fp = os.path.join(vp, d)
                    if os.path.isdir(fp):
                        exe = os.path.join(fp, "bin", "java.exe")
                        if os.path.isfile(exe) and exe.lower() not in [x.lower() for x in java_exes]:
                            java_exes.append(exe)
                            
    best = "java"
    best_v = 0.0
    
    for exe in java_exes:
        try:
            r = subprocess.run([exe, "-version"], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
            m = re.search(r'version \x22([^\x22]+)\x22', r.stderr)
            if m:
                v_str = m.group(1)
                major = float(v_str.split(".")[1]) if v_str.startswith("1.") else float(v_str.split(".")[0])
                if major > best_v:
                    best_v = major
                    best = exe
        except Exception:
            pass
            
    return best

def get_minecraft_versions() -> list[str]:
    """Obtiene las versiones release de Minecraft (Vanilla)."""
    try:
        r = requests.get('https://launchermeta.mojang.com/mc/game/version_manifest.json', timeout=10)
        r.raise_for_status()
        data = r.json()
        return [v['id'] for v in data.get('versions', []) if v.get('type') == 'release']
    except Exception as e:
        print(f"Error obteniendo versiones de Minecraft: {e}")
        return []

def get_paper_versions() -> list[str]:
    """Obtiene las versiones disponibles de PaperMC (API v3)."""
    try:
        headers = {'User-Agent': 'FkonnorLauncher/1.0 (fkonnor@github.com)'}
        r = requests.get('https://fill.papermc.io/v3/projects/paper', headers=headers, timeout=15)
        r.raise_for_status()
        data = r.json()
        versions_map = data.get('versions', {})
        result = []
        for group_key, sub_versions in versions_map.items():
            for v in sub_versions:
                if '-rc' not in v and '-pre' not in v:
                    result.append(v)
        return result
    except Exception as e:
        print(f"Error obteniendo versiones de Paper: {e}")
        return []

def get_forge_versions() -> list[str]:
    """Obtiene las versiones de Minecraft que tienen Forge disponible."""
    try:
        r = requests.get('https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json', timeout=15)
        r.raise_for_status()
        data = r.json()
        promos = data.get('promos', {})
        mc_versions = set()
        for key in promos:
            mc_ver = key.rsplit('-', 1)[0]
            mc_versions.add(mc_ver)
        sorted_versions = sorted(mc_versions, key=lambda v: [int(x) if x.isdigit() else 0 for x in v.split('.')], reverse=True)
        return sorted_versions
    except Exception as e:
        print(f"Error obteniendo versiones de Forge: {e}")
        return []

def get_fabric_versions() -> list[str]:
    """Obtiene las versiones estables del juego para Fabric."""
    try:
        r = requests.get('https://meta.fabricmc.net/v2/versions/game', timeout=10)
        r.raise_for_status()
        data = r.json()
        return [v['version'] for v in data if v.get('stable') == True]
    except Exception as e:
        print(f"Error obteniendo versiones de Fabric: {e}")
        return []

def get_fabric_loader_version() -> str:
    """Obtiene la última versión del loader de Fabric."""
    try:
        r = requests.get('https://meta.fabricmc.net/v2/versions/loader', timeout=10)
        r.raise_for_status()
        data = r.json()
        if data:
            return data[0].get('version', '')
        return ''
    except Exception as e:
        print(f"Error obteniendo loader de Fabric: {e}")
        return ''

def get_fabric_installer_version() -> str:
    """Obtiene la última versión del instalador de Fabric."""
    try:
        r = requests.get('https://meta.fabricmc.net/v2/versions/installer', timeout=10)
        r.raise_for_status()
        data = r.json()
        if data:
            return data[0].get('version', '')
        return ''
    except Exception as e:
        print(f"Error obteniendo installer de Fabric: {e}")
        return ''

def list_servers() -> list[dict]:
    """Lista los servidores configurados en el directorio local."""
    servers = []
    if not os.path.exists(SERVERS_DIR):
        return servers
    
    for d in os.listdir(SERVERS_DIR):
        server_path = os.path.join(SERVERS_DIR, d)
        if os.path.isdir(server_path):
            config_file = os.path.join(server_path, 'server_config.json')
            if os.path.isfile(config_file):
                try:
                    with open(config_file, 'r', encoding='utf-8') as f:
                        config = json.load(f)
                        config['path'] = server_path
                        servers.append(config)
                except Exception as e:
                    print(f"Error leyendo configuración del servidor {d}: {e}")
    return servers

def download_file(url: str, dest: str, progress_callback=None, headers=None):
    """Descarga un archivo con seguimiento de progreso."""
    try:
        r = requests.get(url, stream=True, headers=headers, timeout=15)
        r.raise_for_status()
        total_length = r.headers.get('content-length')
        
        if total_length is None:
            # Sin contentLength, no podemos predecir porcentaje real
            with open(dest, 'wb') as f:
                for chunk in r.iter_content(chunk_size=8192):
                    if chunk:
                        f.write(chunk)
            if progress_callback:
                progress_callback(100, 'Descarga completada')
        else:
            total_length = int(total_length)
            downloaded = 0
            with open(dest, 'wb') as f:
                for chunk in r.iter_content(chunk_size=8192):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if progress_callback:
                            percent = int((downloaded / total_length) * 100)
                            progress_callback(percent, 'Descargando...')
    except Exception as e:
        print(f"Error descargando archivo desde {url}: {e}")
        raise e

def create_server(name: str, server_type: str, version: str, ram: str, progress_callback=None) -> str:
    """Crea un servidor nuevo, descargando el JAR correspondiente y generando archivos básicos."""
    server_type = server_type.upper()
    # Limpiar el nombre
    clean_name = re.sub(r'[^a-zA-Z0-9_\-]', '', name.replace(' ', '_'))
    if not clean_name:
        raise ValueError("Nombre de servidor inválido")
        
    server_path = os.path.join(SERVERS_DIR, clean_name)
    os.makedirs(server_path, exist_ok=True)
    
    # Normalizar RAM: si es un int, convertir a string con "G"
    if isinstance(ram, int):
        ram = f"{ram}G"
    elif isinstance(ram, str) and ram.isdigit():
        ram = f"{ram}G"
    
    # Guardar configuración
    config = {
        'name': clean_name,
        'type': server_type,
        'version': version,
        'ram': ram,
        'java_path': _find_best_java()
    }
    with open(os.path.join(server_path, 'server_config.json'), 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=4)
        
    jar_dest = os.path.join(server_path, 'server.jar')
    
    # Descargar según el tipo
    if server_type == VANILLA:
        if progress_callback: progress_callback(0, 'Obteniendo info Vanilla...')
        r = requests.get('https://launchermeta.mojang.com/mc/game/version_manifest.json')
        r.raise_for_status()
        manifest = r.json()
        version_url = None
        for v in manifest.get('versions', []):
            if v['id'] == version:
                version_url = v['url']
                break
        if not version_url:
            raise ValueError(f"Versión Vanilla {version} no encontrada")
            
        r = requests.get(version_url)
        r.raise_for_status()
        v_data = r.json()
        server_jar_url = v_data.get('downloads', {}).get('server', {}).get('url')
        if not server_jar_url:
            raise ValueError(f"JAR de servidor no disponible para la versión {version}")
            
        download_file(server_jar_url, jar_dest, progress_callback)
        
    elif server_type == PAPER:
        if progress_callback: progress_callback(0, 'Obteniendo info Paper (v3)...')
        headers = {'User-Agent': 'FkonnorLauncher/1.0 (fkonnor@github.com)'}
        r = requests.get(f'https://fill.papermc.io/v3/projects/paper/versions/{version}/builds', headers=headers, timeout=15)
        r.raise_for_status()
        builds = r.json()
        if not builds:
            raise ValueError(f"No hay builds para la versión de Paper {version}")
        stable_builds = [b for b in builds if b.get('channel') == 'STABLE']
        build = stable_builds[0] if stable_builds else builds[0]
        dl_info = build.get('downloads', {}).get('server:default', {})
        jar_url = dl_info.get('url')
        if not jar_url:
            raise ValueError(f"No se encontró URL de descarga para Paper {version}")
        download_file(jar_url, jar_dest, progress_callback, headers=headers)
        
    elif server_type == FABRIC:
        if progress_callback: progress_callback(0, 'Obteniendo info Fabric...')
        loader_ver = get_fabric_loader_version()
        installer_ver = get_fabric_installer_version()
        if not loader_ver or not installer_ver:
            raise ValueError("No se pudieron obtener las versiones de Fabric")
            
        jar_url = f"https://meta.fabricmc.net/v2/versions/loader/{version}/{loader_ver}/{installer_ver}/server/jar"
        download_file(jar_url, jar_dest, progress_callback)

    elif server_type == FORGE:
        if progress_callback: progress_callback(0, 'Obteniendo info Forge...')
        r = requests.get('https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json', timeout=15)
        r.raise_for_status()
        promos = r.json().get('promos', {})
        forge_ver = promos.get(f'{version}-recommended') or promos.get(f'{version}-latest')
        if not forge_ver:
            raise ValueError(f"No hay versión de Forge para Minecraft {version}")
        
        if progress_callback: progress_callback(10, f'Descargando Forge {forge_ver}...')
        installer_url = f"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}-{forge_ver}/forge-{version}-{forge_ver}-installer.jar"
        installer_dest = os.path.join(server_path, 'forge-installer.jar')
        download_file(installer_url, installer_dest, progress_callback)
        
        if progress_callback: progress_callback(80, 'Instalando Forge server...')
        java_path = _find_best_java()
        result = subprocess.run(
            [java_path, '-jar', 'forge-installer.jar', '--installServer'],
            cwd=server_path,
            capture_output=True, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW
        )
        if result.returncode != 0:
            raise ValueError(f"Error instalando Forge: {result.stderr[:200]}")
        
        try:
            os.remove(installer_dest)
        except Exception:
            pass
        
        for f in os.listdir(server_path):
            if f.endswith('.jar') and 'forge' in f.lower() and 'installer' not in f.lower():
                os.rename(os.path.join(server_path, f), jar_dest)
                break
    else:
        raise ValueError("Tipo de servidor no soportado")
        
    # Aceptar EULA
    with open(os.path.join(server_path, 'eula.txt'), 'w', encoding='utf-8') as f:
        f.write("eula=true\n")
        
    # Crear server.properties base
    props = {
        'online-mode': 'false',
        'max-players': '20',
        'motd': 'Fkonnor Server',
        'server-port': '25565',
        'difficulty': 'normal'
    }
    save_server_properties(server_path, props)
    
    if progress_callback: progress_callback(100, 'Servidor creado')
    return server_path

def load_server_properties(server_path: str) -> dict:
    """Carga las propiedades del servidor desde server.properties."""
    props = {}
    prop_file = os.path.join(server_path, 'server.properties')
    if os.path.exists(prop_file):
        with open(prop_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith('#'):
                    if '=' in line:
                        k, v = line.split('=', 1)
                        props[k.strip()] = v.strip()
    return props

def save_server_properties(server_path: str, properties: dict):
    """Guarda las propiedades en server.properties."""
    prop_file = os.path.join(server_path, 'server.properties')
    with open(prop_file, 'w', encoding='utf-8') as f:
        f.write("# Minecraft server properties - Fkonnor Server Manager\n")
        for k, v in properties.items():
            f.write(f"{k}={v}\n")

def update_server_config(server_path: str, **kwargs):
    """Actualiza la configuración del servidor (server_config.json)."""
    config_file = os.path.join(server_path, 'server_config.json')
    if os.path.exists(config_file):
        with open(config_file, 'r', encoding='utf-8') as f:
            config = json.load(f)
            
        config.update(kwargs)
        
        with open(config_file, 'w', encoding='utf-8') as f:
            json.dump(config, f, indent=4)

def delete_server(server_path_or_name: str):
    """Elimina el servidor y todos sus archivos. Acepta ruta completa o nombre."""
    if os.path.isabs(server_path_or_name):
        path = server_path_or_name
    else:
        path = os.path.join(SERVERS_DIR, server_path_or_name)
    if os.path.exists(path):
        shutil.rmtree(path)

class ServerProcess:
    """Gestiona el proceso del servidor de Minecraft en ejecución."""
    
    def __init__(self, server_path: str, java_path: str = None, ram: str = '2G'):
        self.server_path = server_path
        self.java_path = java_path if java_path else _find_best_java()
        self.ram = ram
        self.process = None
        self.running = False
        
    def start(self, output_callback=None):
        """Inicia el servidor."""
        if self.is_running():
            return
            
        cmd = [self.java_path, f"-Xmx{self.ram}", f"-Xms{self.ram}", "-jar", "server.jar", "nogui"]
        
        self.process = subprocess.Popen(
            cmd,
            cwd=self.server_path,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW
        )
        self.running = True
        
        def read_output():
            try:
                for line in iter(self.process.stdout.readline, b''):
                    if output_callback:
                        output_callback(line.decode('utf-8', errors='replace').rstrip('\n'))
            except Exception:
                pass
            finally:
                self.running = False
                
        t = threading.Thread(target=read_output, daemon=True)
        t.start()
        
    def stop(self):
        """Detiene el servidor enviando el comando 'stop'."""
        if self.is_running():
            try:
                self.process.stdin.write(b'stop\n')
                self.process.stdin.flush()
            except Exception:
                pass
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
        self.running = False
        
    def send_command(self, command: str):
        """Envía un comando a la consola del servidor."""
        if self.is_running() and self.process.stdin:
            try:
                self.process.stdin.write((command + '\n').encode('utf-8'))
                self.process.stdin.flush()
            except Exception:
                pass
                
    def is_running(self) -> bool:
        """Verifica si el proceso sigue en ejecución."""
        return self.running and self.process and self.process.poll() is None

def open_server_folder(server_path: str):
    """Abre la carpeta del servidor en el explorador de archivos."""
    if os.path.exists(server_path):
        os.startfile(server_path)


# ==========================================
# FUNCIONES DE ALTO NIVEL (interfaz para la UI)
# ==========================================

# Diccionario global de procesos activos por nombre de servidor
_active_servers: dict[str, ServerProcess] = {}

def get_servers() -> list[dict]:
    """Alias para list_servers()."""
    return list_servers()

def fetch_versions(server_type: str) -> list[str]:
    """Obtiene la lista de versiones según el tipo de servidor."""
    st = server_type.lower()
    if st == 'vanilla':
        return get_minecraft_versions()
    elif st == 'paper':
        return get_paper_versions()
    elif st == 'fabric':
        return get_fabric_versions()
    elif st == 'forge':
        return get_forge_versions()
    return []

def get_server_config(name: str) -> dict:
    """Obtiene la configuración completa de un servidor (server.properties + config)."""
    server_path = os.path.join(SERVERS_DIR, name)
    if not os.path.isdir(server_path):
        return {}
    
    # Leer server.properties
    props = load_server_properties(server_path)
    
    # Leer server_config.json para datos adicionales (ram, tipo, versión)
    config_file = os.path.join(server_path, 'server_config.json')
    if os.path.isfile(config_file):
        try:
            with open(config_file, 'r', encoding='utf-8') as f:
                config = json.load(f)
                props['_ram'] = config.get('ram', '4G')
                props['_type'] = config.get('type', 'vanilla')
                props['_version'] = config.get('version', '')
        except Exception:
            pass
    
    return props

def save_server_config(name: str, properties: dict):
    """Guarda las propiedades del servidor (merge con existentes)."""
    server_path = os.path.join(SERVERS_DIR, name)
    if not os.path.isdir(server_path):
        return
    
    # Cargar propiedades existentes para no perder las que la UI no edita
    existing = load_server_properties(server_path)
    
    # Mezclar: las nuevas propiedades sobrescriben las existentes
    server_props = {k: v for k, v in properties.items() if not k.startswith('_')}
    existing.update(server_props)
    
    save_server_properties(server_path, existing)

def start_server(name: str, output_callback=None):
    """Inicia un servidor por nombre. Bloquea hasta que el servidor se detenga."""
    server_path = os.path.join(SERVERS_DIR, name)
    config_file = os.path.join(server_path, 'server_config.json')
    
    ram = '4G'
    if os.path.isfile(config_file):
        try:
            with open(config_file, 'r', encoding='utf-8') as f:
                config = json.load(f)
                ram = config.get('ram', '4G')
        except Exception:
            pass
    
    proc = ServerProcess(server_path, ram=ram)
    _active_servers[name] = proc
    proc.start(output_callback)
    
    # Esperar a que el proceso termine
    if proc.process:
        proc.process.wait()
    proc.running = False
    _active_servers.pop(name, None)

def stop_server(name: str):
    """Detiene un servidor por nombre."""
    proc = _active_servers.get(name)
    if proc:
        proc.stop()

def send_command(name: str, command: str):
    """Envía un comando a un servidor en ejecución."""
    proc = _active_servers.get(name)
    if proc:
        proc.send_command(command)
