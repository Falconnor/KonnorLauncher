import os, sys, requests, subprocess, shutil, json, re

path = os.path.join(os.path.dirname(__file__), 'server_core.py')
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add FORGE constant
if "FORGE = 'FORGE'" not in content:
    content = content.replace("FABRIC = 'FABRIC'", "FABRIC = 'FABRIC'\nFORGE = 'FORGE'")

# 2. Add get_forge_versions and replace get_paper_versions
if "def get_forge_versions" not in content:
    paper_v2 = '''def get_paper_versions() -> list[str]:
    """Obtiene las versiones disponibles de PaperMC."""
    try:
        headers = {'User-Agent': 'FkonnorLauncher/1.0'}
        r = requests.get('https://api.papermc.io/v2/projects/paper', headers=headers, timeout=10)
        r.raise_for_status()
        data = r.json()
        versions = data.get('versions', [])
        return list(reversed(versions))
    except Exception as e:
        print(f"Error obteniendo versiones de Paper: {e}")
        return []'''
    
    paper_v3_and_forge = '''def get_paper_versions() -> list[str]:
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
        return []'''
    content = content.replace(paper_v2, paper_v3_and_forge)

# 3. Add Forge to create_server and fix Paper
if "elif server_type == FORGE:" not in content:
    old_paper_dl = '''    elif server_type == PAPER:
        if progress_callback: progress_callback(0, 'Obteniendo info Paper...')
        headers = {'User-Agent': 'FkonnorLauncher/1.0'}
        r = requests.get(f'https://api.papermc.io/v2/projects/paper/versions/{version}', headers=headers)
        r.raise_for_status()
        v_data = r.json()
        builds = v_data.get('builds', [])
        if not builds:
            raise ValueError(f"No hay builds para la versión de Paper {version}")
        latest_build = builds[-1]
        
        jar_url = f"https://api.papermc.io/v2/projects/paper/versions/{version}/builds/{latest_build}/downloads/paper-{version}-{latest_build}.jar"
        download_file(jar_url, jar_dest, progress_callback, headers=headers)'''
        
    new_paper_dl = '''    elif server_type == PAPER:
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
        download_file(jar_url, jar_dest, progress_callback, headers=headers)'''
    content = content.replace(old_paper_dl, new_paper_dl)
    
    # insert forge after fabric
    fabric_dl = '''    elif server_type == FABRIC:
        if progress_callback: progress_callback(0, 'Obteniendo info Fabric...')
        loader_ver = get_fabric_loader_version()
        installer_ver = get_fabric_installer_version()
        if not loader_ver or not installer_ver:
            raise ValueError("No se pudieron obtener las versiones de Fabric")
            
        jar_url = f"https://meta.fabricmc.net/v2/versions/loader/{version}/{loader_ver}/{installer_ver}/server/jar"
        download_file(jar_url, jar_dest, progress_callback)'''
        
    forge_dl = '''    elif server_type == FABRIC:
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
                break'''
    content = content.replace(fabric_dl, forge_dl)

# 4. Add Forge to fetch_versions
if "elif st == 'forge':" not in content:
    content = content.replace("elif st == 'fabric':\n        return get_fabric_versions()", "elif st == 'fabric':\n        return get_fabric_versions()\n    elif st == 'forge':\n        return get_forge_versions()")

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

with open(path, 'r', encoding='utf-8') as f:
    c = f.read()
    print('FORGE in file:', 'FORGE' in c)
    print('fill.papermc.io in file:', 'fill.papermc.io' in c)
