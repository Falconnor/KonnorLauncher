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



# DEFINIR DIRECTORIO DE NATIVES
$natives = "$game\versions\$version\natives"



# CONSTRUIR LISTA DE LIBRERIAS
$libraries = @()

if ($json.libraries) {

    $libraries += $json.libraries

}

if ($parentJson -and $parentJson.libraries) {

    $libraries += $parentJson.libraries

}



#OBTENER LIBRERIAS
$classpath = $libraries | ForEach-Object {

    if ($_.name -and $_.name -notmatch ":natives-") {

        $parts = $_.name.Split(":")

        if ($parts.Count -ge 3) {

            $group = $parts[0].Replace(".", "\")
            $artifact = $parts[1]
            $versionLib = $parts[2]

            "$game\libraries\$group\$artifact\$versionLib\$artifact-$versionLib.jar"
        }
    }

} | Where-Object { Test-Path $_ }



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