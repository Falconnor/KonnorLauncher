$game = $PSScriptRoot

$downloadServer = "http://192.168.100.5:59381"

$manifest = @{
    clientVersion = "1.0.0"
    minecraft = ""
    fabric = ""
    files = @{}
}


# Detectar versi�n instalada
$versionFolder = Get-ChildItem "$game\versions" -Directory | 
    Where-Object { $_.Name -like "fabric-loader-*" } |
    Select-Object -First 1


if ($versionFolder) {

    $manifest.fabric = $versionFolder.Name

    $json = Get-Content "$($versionFolder.FullName)\$($versionFolder.Name).json" | ConvertFrom-Json

    $manifest.minecraft = $json.id -replace '^fabric-loader-[^-]+-',''
}


# Carpetas a proteger
$protectedFolders = @(
    "mods"
    "versions",
    "libraries"
)


foreach ($folder in $protectedFolders) {

    $path = Join-Path $game $folder

    if (Test-Path $path) {

        Get-ChildItem $path -File -Recurse | ForEach-Object {

            $relative = $_.FullName.Substring($game.Length + 1)

            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash


            $manifest.files[$relative] = @{
                sha256 = $hash
                size = $_.Length
                url = "$downloadServer/$relative"
            }

            Write-Host "Registrado:" $relative
        }
    }
}


$manifest | ConvertTo-Json -Depth 5 |
    Set-Content "$game\client_manifest.json"


Write-Host ""
Write-Host "Manifest generado correctamente"