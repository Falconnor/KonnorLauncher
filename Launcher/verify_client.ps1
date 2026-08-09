$launcher = $PSScriptRoot

. "$PSScriptRoot\common.ps1"


$propertiesFile = Join-Path $launcher "launcher.properties"

$verifyEnabled = Get-BooleanProperty `
    $propertiesFile `
    "enable.verify"


if (!$verifyEnabled) {

    Write-Host "verificador desactivado"

    exit 0

}

Write-Host "verificando archivos"

$gameRelative = Get-Property `
    $propertiesFile `
    "game.path"


$serverConfigUrl = Get-Property `
    $propertiesFile `
    "server.config"



$game = Join-Path `
    $launcher `
    $gameRelative


$game = (Resolve-Path $game).Path


#Write-Host "GAME PATH: $game"
#Write-Host "EXISTE MODS:" (Test-Path "$game\mods")
#Write-Host "EXISTE LIBRARIES:" (Test-Path "$game\libraries")
#Write-Host "EXISTE VERSIONS:" (Test-Path "$game\versions")



try {

    $serverConfig = Invoke-RestMethod `
        -Uri $serverConfigUrl `
        -TimeoutSec 3 `
        -ErrorAction Stop

}
catch {

    Write-Host "Servidor no responde"

    exit 1

}


$baseUrl = $serverConfig.baseUrl.TrimEnd("/")


$tempManifest = "$game\client_manifest_temp.json"


try {

    Invoke-WebRequest `
        -Uri "$baseUrl/client_manifest.json" `
        -OutFile $tempManifest `
        -ErrorAction Stop

}
catch {

    Write-Host "No se pudo descargar el manifest"

    exit 1

}



$manifest = Get-Content $tempManifest | ConvertFrom-Json



$protectedFolders = @(
    "mods",
    "versions",
    "libraries"
)



$tempFolder = "$game\temp"


if (!(Test-Path $tempFolder)) {

    New-Item `
        $tempFolder `
        -ItemType Directory | Out-Null

}

# Verificar y reparar archivos
$repairFiles = @()
foreach ($file in $manifest.files.PSObject.Properties) {
    $relativePath = $file.Name
    $expectedHash = $file.Value.sha256
    $downloadUrl = "$baseUrl/$($relativePath.Replace('\','/'))"
    $localPath = Join-Path $game $relativePath
    $needsRepair = $false

    if (!(Test-Path $localPath)) {
        Write-Host "[FALTA] $relativePath"
        $needsRepair = $true
    } else {
        $currentHash = (Get-FileHash $localPath -Algorithm SHA256).Hash
        if ($currentHash -ne $expectedHash) {
            Write-Host "[CORRUPTO] $relativePath"
            $needsRepair = $true
        }
    }

    if ($needsRepair) {
        $repairFiles += [pscustomobject]@{
            RelativePath = $relativePath
            ExpectedHash = $expectedHash
            DownloadUrl = $downloadUrl
            LocalPath = $localPath
        }
    }
}

$totalRepairs = $repairFiles.Count
$repairIndex = 0

foreach ($fileInfo in $repairFiles) {
    $repairIndex++
    $percentage = 0
    if ($totalRepairs -gt 0) {
        $percentage = [math]::Round(($repairIndex / $totalRepairs) * 100)
    }
    Write-Host "Descargando archivos $percentage%"
    Write-Host "Descargando $($fileInfo.RelativePath)"

    $tempFile = Join-Path $tempFolder ([System.IO.Path]::GetFileName($fileInfo.RelativePath))

    Invoke-WebRequest `
        -Uri $fileInfo.DownloadUrl `
        -OutFile $tempFile

    $downloadHash = (Get-FileHash $tempFile -Algorithm SHA256).Hash

    if ($downloadHash -eq $fileInfo.ExpectedHash) {
        $directory = Split-Path $fileInfo.LocalPath
        if (!(Test-Path $directory)) {
            New-Item $directory -ItemType Directory -Force | Out-Null
        }
        Move-Item $tempFile $fileInfo.LocalPath -Force
        Write-Host "[REPARADO] $($fileInfo.RelativePath)"
    } else {
        Write-Host "[ERROR HASH] Descarga inválida: $($fileInfo.RelativePath)"
        Remove-Item $tempFile -Force
    }
}
# Eliminar archivos no autorizados

$allowedFiles = $manifest.files.PSObject.Properties.Name


foreach ($folder in $protectedFolders) {


    $folderPath = Join-Path $game $folder


    if (!(Test-Path $folderPath)) {
        continue
    }


    Get-ChildItem $folderPath -File -Recurse | ForEach-Object {


        $relative = $_.FullName.Substring($game.Length + 1)


        if ($allowedFiles -notcontains $relative) {


            Write-Host "[ELIMINADO] No autorizado: $relative"


            Remove-Item $_.FullName -Force

        }

    }

}


Remove-Item $tempManifest -Force


Write-Host ""
Write-Host "Cliente sincronizado correctamente"