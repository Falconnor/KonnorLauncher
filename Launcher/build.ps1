<#
.SYNOPSIS
    Compila, prueba y publica Konnor Launcher, y crea su instalador y sus actualizaciones con Velopack.

.DESCRIPTION
    1. dotnet test (Core)              → si alguna prueba falla, no se publica nada
    2. dotnet publish (App, win-x64)   → ./publish  (autocontenido, single-file: no requiere .NET instalado)
    3. Herramientas del servidor       → ./artifacts/KonnorLauncher-Herramientas-<versión>.zip  (Python portátil con
                                         sus dependencias, panel del servidor y gestor de servidores, listos para usar)
    4. Velopack (vpk pack)             → ./artifacts/velopack:
         KonnorLauncher-win-Setup.exe     instalador (por usuario, sin permisos de administrador)
         KonnorLauncher-win-Portable.zip  versión portátil
         *.nupkg, releases.win.json       lo que lee el launcher para actualizarse (completo y delta)
       Antes se descarga la última release de GitHub para generar la actualización delta (solo lo que cambia).
    5. Con -Upload: sube todo a una release de GitHub (etiqueta v<versión>) y la publica.

    La versión es la de Directory.Build.props (<Version>). El launcher busca actualizaciones en las releases de
    https://github.com/Falconnor/KonnorLauncher.

.PARAMETER ServerUrl
    Si se indica, se escribe en el launcher.properties que se distribuye (server.url de producción).

.PARAMETER SkipTests
    Omite las pruebas (no recomendado).

.PARAMETER SkipServerTools
    No prepara el .zip con el panel y el gestor de servidores.

.PARAMETER PythonVersion
    Versión de Python (embebible de python.org) que llevan las herramientas del servidor.

.PARAMETER SkipPack
    Solo publica; no crea el paquete de Velopack.

.PARAMETER Upload
    Sube la release a GitHub (necesita un token con permiso de escritura en el repositorio: -Token o GITHUB_TOKEN).

.PARAMETER ReleaseNotes
    Archivo Markdown con las novedades de la versión (se ven en la release).

.EXAMPLE
    ./build.ps1                                   # todo en local, sin subir
    ./build.ps1 -Upload -Token ghp_...            # publicar la versión en GitHub
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [switch] $SkipTests,
    [switch] $SkipServerTools,
    [string] $PythonVersion = "3.15.2",
    [switch] $SkipPack,
    [switch] $Upload,
    [string] $Token = $env:GITHUB_TOKEN,
    [string] $ReleaseNotes,
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$RepoUrl = "https://github.com/Falconnor/KonnorLauncher"
$PackId = "KonnorLauncher"
$AppTitle = "Konnor Launcher"
$MainExe = "Konnor Launcher.exe"
# El mismo identificador que usa la ventana (Program.AppUserModelId): accesos directos y ventana se agrupan juntos.
$Aumid = "konnor.launcher.1.0"
$Version = ([xml](Get-Content (Join-Path $PSScriptRoot "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) { throw "No se encontró <Version> en Directory.Build.props." }
Write-Host "Konnor Launcher $Version" -ForegroundColor Cyan

$artifacts = Join-Path $PSScriptRoot "artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null

# ── 1. Pruebas ──────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host "== Pruebas ==" -ForegroundColor Cyan
    dotnet test --project tests/KonnorLauncher.Core.Tests -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Las pruebas fallaron; no se publica." }
}

# ── 2. Publicación ──────────────────────────────────────────────────────
Write-Host "== Publicación ==" -ForegroundColor Cyan
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src/KonnorLauncher.App -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló." }

# Si se indica -ServerUrl, se escribe en el launcher.properties que se distribuye (la configuración inicial: la del
# jugador vive en su carpeta de datos y las actualizaciones no la tocan).
if ($ServerUrl) {
    $props = Join-Path $PSScriptRoot "publish\launcher.properties"
    $text = [IO.File]::ReadAllText($props)
    $text = [Text.RegularExpressions.Regex]::Replace($text, "(?m)^server\.url=.*$", "server.url=$ServerUrl")
    [IO.File]::WriteAllText($props, $text, (New-Object Text.UTF8Encoding $false))
    Write-Host "server.url = $ServerUrl"
}

# ── 3. Herramientas del servidor (.zip aparte) ──────────────────────────
# Python embebible de python.org con las dependencias ya instaladas, más el código del panel (LauncherBackEnd) y del
# gestor (ServerManager). Velopack no tiene componentes opcionales: van en su propio .zip, que se descomprime y se usa.
$tools = Join-Path $PSScriptRoot "publish-herramientas"
if (Test-Path $tools) { Remove-Item $tools -Recurse -Force }
if (-not $SkipServerTools) {
    Write-Host "== Herramientas del servidor ==" -ForegroundColor Cyan
    $repo = Split-Path $PSScriptRoot -Parent
    $pyDir = Join-Path $tools "python"
    $shortVersion = $PythonVersion -replace '^(\d+)\.(\d+).*$', '$1.$2'
    $tag = $shortVersion -replace '\.', ''

    # Se descarga una sola vez (obj\ no está en git).
    $zip = Join-Path $PSScriptRoot "obj\python-$PythonVersion-embed-amd64.zip"
    if (-not (Test-Path $zip)) {
        New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
        Invoke-WebRequest "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip" `
            -OutFile "$zip.part" -UseBasicParsing
        Move-Item "$zip.part" $zip
    }
    Expand-Archive $zip -DestinationPath $pyDir

    # El ._pth fija sys.path: biblioteca estándar, dependencias y el código de los dos programas.
    [IO.File]::WriteAllLines((Join-Path $pyDir "python$tag._pth"),
        [string[]] @("python$tag.zip", ".", "Lib\site-packages", "..\backend", "..\servermanager", "import site"))

    # Dependencias como ruedas para esa versión de Python (no hace falta tenerla instalada igual).
    $site = Join-Path $pyDir "Lib\site-packages"
    python -m pip install --disable-pip-version-check -q --only-binary=:all: `
        --platform win_amd64 --implementation cp --python-version $shortVersion --target $site `
        -r "$repo\LauncherBackEnd\requirements.txt" -r "$repo\ServerManager\requirements.txt"
    if ($LASTEXITCODE -ne 0) { throw "pip install falló." }
    # Los .exe de bin\ apuntan al Python de este equipo: no sirven en el instalado.
    Remove-Item (Join-Path $site "bin") -Recurse -Force -ErrorAction SilentlyContinue

    # PySide6: el gestor solo usa QtCore, QtGui y QtWidgets. El resto (QML, Quick, Designer,
    # herramientas, traducciones...) ocupa más de 150 MB.
    $qt = Join-Path $site "PySide6"
    $keepPyd = "QtCore.pyd", "QtGui.pyd", "QtWidgets.pyd"
    $keepQtDll = "Qt6Core.dll", "Qt6Gui.dll", "Qt6Widgets.dll", "Qt6Network.dll", "Qt6Svg.dll"
    Get-ChildItem $qt -File | Where-Object {
        $_.Extension -in ".exe", ".pyi" -or
        ($_.Extension -eq ".pyd" -and $keepPyd -notcontains $_.Name) -or
        ($_.Name -like "Qt6*.dll" -and $keepQtDll -notcontains $_.Name) -or
        $_.Name -in "opengl32sw.dll", "pyside6qml.abi3.dll"
    } | Remove-Item -Force
    "doc", "glue", "include", "lib", "metatypes", "qml", "resources", "scripts", "translations", "typesystems" |
        ForEach-Object { Remove-Item (Join-Path $qt $_) -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem (Join-Path $qt "plugins") -Directory |
        Where-Object { "platforms", "styles", "imageformats", "iconengines" -notcontains $_.Name } |
        Remove-Item -Recurse -Force

    # Código: lo que está (o irá) en git, sin los .bat, que usan un .venv que aquí no existe.
    $parts = @{ "LauncherBackEnd" = "backend"; "ServerManager" = "servermanager" }
    foreach ($src in $parts.Keys) {
        $files = git -C $repo ls-files --cached --others --exclude-standard -- $src |
            Where-Object { $_ -notmatch '(\.bat|\.lnk|\.gitignore)$' }
        foreach ($f in $files) {
            $dest = Join-Path (Join-Path $tools $parts[$src]) $f.Substring($src.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item (Join-Path $repo $f) $dest
        }
    }

    # Para abrirlos con doble clic (antes eran los accesos directos del instalador).
    [IO.File]::WriteAllText((Join-Path $tools "Panel del servidor.cmd"),
        "@echo off`r`ncd /d `"%~dp0backend`"`r`n`"%~dp0python\python.exe`" -m Panel`r`n")
    [IO.File]::WriteAllText((Join-Path $tools "Gestor de servidores.cmd"),
        "@echo off`r`ncd /d `"%~dp0servermanager`"`r`nstart `"`" `"%~dp0python\pythonw.exe`" server_manager.py`r`n")

    # Comprobación: el Python empaquetado importa todo lo que necesitan los dos programas.
    & (Join-Path $pyDir "python.exe") -c "import fastapi, uvicorn, watchfiles, requests, passlib, jwt, sqlalchemy, bcrypt; from PySide6 import QtCore, QtGui, QtWidgets"
    if ($LASTEXITCODE -ne 0) { throw "El Python de las herramientas no importa sus dependencias." }

    $toolsZip = Join-Path $artifacts "KonnorLauncher-Herramientas-$Version.zip"
    if (Test-Path $toolsZip) { Remove-Item $toolsZip -Force }
    Compress-Archive -Path (Join-Path $tools "*") -DestinationPath $toolsZip -CompressionLevel Optimal
    "{0:N0} MB en {1}" -f ((Get-Item $toolsZip).Length / 1MB), $toolsZip
}

# ── 4. Velopack: instalador y paquetes de actualización ─────────────────
$velopack = Join-Path $artifacts "velopack"
if (-not $SkipPack) {
    Write-Host "== Velopack ==" -ForegroundColor Cyan
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo restaurar vpk (dotnet tool restore)." }
    if (Test-Path $velopack) { Remove-Item $velopack -Recurse -Force }
    New-Item -ItemType Directory -Force $velopack | Out-Null

    # La última release publicada: con ella vpk genera el paquete delta (los jugadores solo descargan lo que cambia).
    # La primera vez no hay ninguna con Velopack: se sigue sin delta.
    dotnet vpk download github --repoUrl $RepoUrl --outputDir $velopack
    if ($LASTEXITCODE -ne 0) { Write-Host "Sin release anterior de Velopack: este paquete no tendrá delta." -ForegroundColor Yellow }

    $packArgs = @(
        "vpk", "pack",
        "--packId", $PackId,
        "--packVersion", $Version,
        "--packDir", (Join-Path $PSScriptRoot "publish"),
        "--mainExe", $MainExe,
        "--runtime", "win-x64",
        "--packTitle", $AppTitle,
        "--packAuthors", "Falconnor",
        "--icon", (Join-Path $PSScriptRoot "src\KonnorLauncher.App\Assets\icon.ico"),
        "--aumid", $Aumid,
        "--shortcuts", "Desktop,StartMenuRoot",
        "--outputDir", $velopack
    )
    if ($ReleaseNotes) { $packArgs += @("--releaseNotes", (Resolve-Path $ReleaseNotes)) }
    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack falló." }
    Get-ChildItem $velopack -File | ForEach-Object { Write-Host "  $($_.Name)" }
}

# ── 5. Publicar en GitHub ───────────────────────────────────────────────
if ($Upload) {
    if ($SkipPack) { throw "-Upload necesita el paquete de Velopack (sin -SkipPack)." }
    if (-not $Token) { throw "Para subir hace falta un token de GitHub (-Token o la variable GITHUB_TOKEN)." }
    Write-Host "== Release v$Version en GitHub ==" -ForegroundColor Cyan
    dotnet vpk upload github --repoUrl $RepoUrl --token $Token --outputDir $velopack `
        --publish --merge --releaseName "$AppTitle $Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload falló." }

    # Las herramientas del servidor, en la misma release.
    $toolsZip = Join-Path $artifacts "KonnorLauncher-Herramientas-$Version.zip"
    if (Test-Path $toolsZip) {
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            $env:GH_TOKEN = $Token
            gh release upload "v$Version" $toolsZip --repo "Falconnor/KonnorLauncher" --clobber
            if ($LASTEXITCODE -ne 0) { throw "No se pudo subir $toolsZip a la release." }
        }
        else {
            Write-Host "Sube a mano $toolsZip a la release v$Version (no está GitHub CLI: gh)." -ForegroundColor Yellow
        }
    }
}

Write-Host "Listo: $Version" -ForegroundColor Green
