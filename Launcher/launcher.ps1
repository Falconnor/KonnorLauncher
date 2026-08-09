$launcher = $PSScriptRoot
. "$PSScriptRoot\common.ps1"



# CONFIGURACION DE RUTAS
$propertiesFile = Join-Path $launcher "launcher.properties"

$gamePath = Get-Property `
    $propertiesFile `
    "game.path"

$game = Join-Path `
    $launcher `
    $gamePath

$game = (Resolve-Path $game).Path



# CONFIGURACION DEL CLIENTE
$gameConfig = Get-Content "$game\config.json" |
    ConvertFrom-Json

$javaPath = Get-Property `
    $propertiesFile `
    "java.path"


if ([System.IO.Path]::IsPathRooted($javaPath)) {

    $java = $javaPath

}
else {

    $java = Join-Path $game $javaPath

}



# CARGAR JSON DE LA VERSION SELECCIONADA
$version = Get-Property `
    $propertiesFile `
    "minecraft.version"

$json = Get-Content `
    "$game\versions\$version\$version.json" |
    ConvertFrom-Json

    $parentJson = $null



# CARGAR INHERITS
if ($json.inheritsFrom) {

    $parentJson = Get-Content `
        "$game\versions\$($json.inheritsFrom)\$($json.inheritsFrom).json" |
        ConvertFrom-Json

}



# DETECTAR TIPO DE CLIENTE
if ($version -like "fabric-loader-*") {

    $type = "fabric"
    $minecraftVersion =
        $version -replace '^fabric-loader-[^-]+-',''

}
elseif ($version -like "forge-*") {

    $type = "forge"
    $minecraftVersion = $json.inheritsFrom

}
else {

    $type = "vanilla"
    $minecraftVersion = $version

}



# OBTENER CLASSINDEX
if ($json.assetIndex) {

    $assetIndex = $json.assetIndex.id

}
elseif ($parentJson -and $parentJson.assetIndex) {

    $assetIndex = $parentJson.assetIndex.id

}
else {

    Write-Host "No se encontró asset index"
    exit 1

}



# DEFINIR DIRECTORIOS DE NATIVES
$nativesRoot = "$game\versions\$version\natives"

if (!(Test-Path $nativesRoot)) {

    Write-Host "No se encontró el directorio de natives:"
    Write-Host $nativesRoot

    exit 1

}


# Detectar estructura de natives

$nativeDirectories = @()


# DLL directamente dentro de natives
if (
    Get-ChildItem `
        $nativesRoot `
        -File `
        -Filter "*.dll" `
        -ErrorAction SilentlyContinue
) {

    $nativeDirectories += $nativesRoot

}


# DLL dentro de subdirectorios
$nativeDirectories += Get-ChildItem `
    $nativesRoot `
    -Directory `
    -ErrorAction SilentlyContinue |
    Where-Object {

        Get-ChildItem `
            $_.FullName `
            -File `
            -Filter "*.dll" `
            -ErrorAction SilentlyContinue

    } |
    ForEach-Object {

        $_.FullName

    }


# Eliminar duplicados
$nativeDirectories = $nativeDirectories | Select-Object -Unique


if ($nativeDirectories.Count -eq 0) {

    Write-Host "No se encontraron DLLs nativas en:"
    Write-Host $nativesRoot

    exit 1

}


$natives = $nativeDirectories -join ";"



# CONSTRUIR LISTA DE LIBRERIAS
$libraries = @()

if ($json.libraries) {

    $libraries += $json.libraries

}

if ($parentJson -and $parentJson.libraries) {

    $libraries += $parentJson.libraries

}



# OBTENER LIBRERIAS
$classpath = $libraries | ForEach-Object {

    if ($_.rules) {

        $allowed = $true

        foreach ($rule in $_.rules) {

            if ($rule.os -and $rule.os.name -ne "windows") {
                $allowed = $false
            }

        }

        if (!$allowed) {
            return
        }
    }

    if ($_.name -and $_.name -notmatch ":natives-") {

        # Usar la ruta indicada por el JSON cuando exista
        if ($_.downloads -and $_.downloads.artifact -and $_.downloads.artifact.path) {

            $libraryPath = Join-Path `
                $game `
                ("libraries\" + $_.downloads.artifact.path)

            if (Test-Path $libraryPath) {

                $libraryPath

            }

        }
        else {

            # Compatibilidad con JSON antiguos
            $parts = $_.name.Split(":")

            if ($parts.Count -ge 3) {

                $group = $parts[0].Replace(".", "\")
                $artifact = $parts[1]
                $versionLib = $parts[2]

                $libraryPath =
                    "$game\libraries\$group\$artifact\$versionLib\$artifact-$versionLib.jar"

                if (Test-Path $libraryPath) {

                    $libraryPath

                }

            }

        }

    }

} | Where-Object { $_ }


####### DEBUG LWJGL 26.#######

Write-Host ""
Write-Host "===== LWJGL ====="

$classpath -split ";" |
    Where-Object { $_ -match "\\org\\lwjgl\\" } |
    ForEach-Object {
        Write-Host $_
    }

Write-Host "================="
Write-Host ""

# AGREGAR JAR
$classpath = ($classpath -join ";")
$mainJar = "$game\versions\$version\$version.jar"

if (Test-Path $mainJar) {

    $classpath += ";$mainJar"

}



#Agregar JAR vanilla
$baseJar = "$game\versions\$minecraftVersion\$minecraftVersion.jar"

if (
    $minecraftVersion -ne $version -and
    (Test-Path $baseJar)
) {

    $classpath += ";$baseJar"

}


######## DEBUG TEMPORAL###################

Write-Host ""
Write-Host "===== DEBUG ====="

Write-Host "VERSION:" $version
Write-Host "TYPE:" $type
Write-Host "MINECRAFT VERSION:" $minecraftVersion
Write-Host "MAIN CLASS:" $json.mainClass
Write-Host "ASSET INDEX:" $assetIndex

Write-Host ""
Write-Host "JAVA:"
Write-Host $java

Write-Host ""
Write-Host "CLASSPATH LENGTH:"
Write-Host $classpath.Length

Write-Host "================="
Write-Host ""


 #EJECUTAR MINECRAFT
& $java `
"-Xmx$($gameConfig.java.memory)" ` `
"-Djava.library.path=$natives" `
-cp "$classpath" `
$json.mainClass `
--username $gameConfig.player.username `
--version $version `
--gameDir "$game" `
--assetsDir "$game\assets" `
--assetIndex $assetIndex `
--uuid $gameConfig.player.uuid `
--accessToken $gameConfig.player.accessToken