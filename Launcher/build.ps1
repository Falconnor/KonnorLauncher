<#
.SYNOPSIS
    Compila, prueba y publica Konnor Launcher y genera el instalador.

.DESCRIPTION
    1. dotnet test (Core)            → si alguna prueba falla, no se publica nada
    2. dotnet publish (App, win-x64) → ./publish  (autocontenido, single-file: no requiere .NET instalado)
    3. Herramientas del servidor     → ./publish-herramientas  (Python portátil con sus dependencias,
                                       panel del servidor y gestor de servidores, listos para usar)
    4. ISCC installer\KonnorLauncher.iss → ./artifacts/KonnorLauncher-Setup-<versión>.exe

.PARAMETER ServerUrl
    Si se indica, se escribe en el launcher.properties que se distribuye (server.url de producción).

.PARAMETER SkipTests
    Omite las pruebas (no recomendado).

.PARAMETER SkipServerTools
    El instalador lleva solo el launcher, sin el panel ni el gestor de servidores.

.PARAMETER PythonVersion
    Versión de Python (embebible de python.org) que llevan las herramientas del servidor.

.PARAMETER SkipInstaller
    Solo publica; no compila el instalador.

.EXAMPLE
    ./build.ps1 -ServerUrl "https://mi-servidor.example:8080"
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [switch] $SkipTests,
    [switch] $SkipServerTools,
    [string] $PythonVersion = "3.15.2",
    [switch] $SkipInstaller,
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

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

# Si se indica -ServerUrl, se escribe en el launcher.properties que se distribuye.
if ($ServerUrl) {
    $props = Join-Path $PSScriptRoot "publish\launcher.properties"
    $text = [IO.File]::ReadAllText($props)
    $text = [Text.RegularExpressions.Regex]::Replace($text, "(?m)^server\.url=.*$", "server.url=$ServerUrl")
    [IO.File]::WriteAllText($props, $text, (New-Object Text.UTF8Encoding $false))
    Write-Host "server.url = $ServerUrl"
}

# ── 3. Herramientas del servidor ────────────────────────────────────────
# Python embebible de python.org con las dependencias ya instaladas, más el código del panel
# (LauncherBackEnd) y del gestor (ServerManager). El instalador los ofrece como componentes.
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

    # Comprobación: el Python empaquetado importa todo lo que necesitan los dos programas.
    & (Join-Path $pyDir "python.exe") -c "import fastapi, uvicorn, watchfiles, requests, passlib, jwt, sqlalchemy, bcrypt; from PySide6 import QtCore, QtGui, QtWidgets"
    if ($LASTEXITCODE -ne 0) { throw "El Python de las herramientas no importa sus dependencias." }
    "{0:N0} MB en {1}" -f ((Get-ChildItem $tools -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $tools
}

# ── 4. Instalador ───────────────────────────────────────────────────────
if (-not $SkipInstaller) {
    Write-Host "== Instalador (Inno Setup) ==" -ForegroundColor Cyan
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $candidates = @(
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw "No se encontró ISCC.exe. Instala Inno Setup (6.7+ o 7) o usa -SkipInstaller." }
    & $iscc "installer\KonnorLauncher.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC falló." }
    Get-ChildItem artifacts\*.exe | ForEach-Object { Write-Host "Instalador: $($_.FullName)" -ForegroundColor Green }
}
