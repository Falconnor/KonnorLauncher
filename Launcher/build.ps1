<#
.SYNOPSIS
    Compila, prueba y publica Konnor Launcher y genera el instalador.

.DESCRIPTION
    1. dotnet test (Core)            → si alguna prueba falla, no se publica nada
    2. dotnet publish (App, win-x64) → ./publish  (autocontenido, single-file: no requiere .NET instalado)
    3. ISCC installer\KonnorLauncher.iss → ./artifacts/KonnorLauncher-Setup-<versión>.exe

.PARAMETER ServerUrl
    Si se indica, se escribe en el launcher.properties que se distribuye (server.url de producción).

.PARAMETER SkipTests
    Omite las pruebas (no recomendado).

.PARAMETER SkipInstaller
    Solo publica; no compila el instalador.

.EXAMPLE
    ./build.ps1 -ServerUrl "https://mi-servidor.example:8080"
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [switch] $SkipTests,
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

# ── 3. Instalador ───────────────────────────────────────────────────────
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
