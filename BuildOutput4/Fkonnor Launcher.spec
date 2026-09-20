# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['..\\Launcher\\launcher.py'],
    pathex=[],
    binaries=[],
    datas=[('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\arrow_down.svg', '.'), ('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\background.png', '.'), ('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\Boring Time.otf', '.'), ('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\icon.png', '.'), ('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\steve.png', '.'), ('c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\launcher.properties', '.')],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='Fkonnor Launcher',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['c:\\Users\\Luis\\Desktop\\Fkonnor Launcher\\Launcher\\icon.ico'],
)
