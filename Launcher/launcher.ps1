$launcher = $PSScriptRoot
. "$PSScriptRoot\common.ps1"

# Configuración de rutas del launcher
$propertiesFile = Join-Path $launcher "launcher.properties"


$gamePath = Get-Property `
    $propertiesFile `
    "game.path"


$game = Join-Path `
    $launcher `
    $gamePath


$game = (Resolve-Path $game).Path

# Configuración del cliente Minecraft
$gameConfig = Get-Content "$game\config.json" |
    ConvertFrom-Json

$javaPath = Get-Property `
    $propertiesFile `
    "java.path"


$java = Join-Path `
    $game `
    $javaPath

$versions = Get-ChildItem "$game\versions" -Directory | ForEach-Object {

    $jsonFile = Join-Path $_.FullName "$($_.Name).json"

    if (Test-Path $jsonFile) {

        $json = Get-Content $jsonFile | ConvertFrom-Json

        [PSCustomObject]@{
            Name = $json.id
            MainClass = $json.mainClass
            Path = $_.FullName
        }
    }
}

# Elegir la versi�n Fabric
$selectedVersion = $versions | Where-Object {
    $_.Name -like "fabric-loader-*"
} | Select-Object -First 1

$version = $selectedVersion.Name

$natives = "$game\versions\$version\natives"

# Obtener versi�n base de Minecraft
$minecraftVersion = $json.id -replace '^fabric-loader-[^-]+-',''

# Leer JSON de Minecraft base

# Crear classpath
$classpath = $json.libraries | ForEach-Object {

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


$classpath = ($classpath -join ";") + ";" +
"$game\versions\$minecraftVersion\$minecraftVersion.jar" + ";" +
"$game\versions\$version\$version.jar"


# Ejecutar Minecraft
& $java `
"-Xmx$($gameConfig.java.memory)" ` `
"-Djava.library.path=$natives" `
-cp "$classpath" `
$json.mainClass `
--username $gameConfig.player.username `
--version $version `
--gameDir "$game" `
--assetsDir "$game\assets" `
--assetIndex 17 `
--uuid $gameConfig.player.uuid `
--accessToken $gameConfig.player.accessToken